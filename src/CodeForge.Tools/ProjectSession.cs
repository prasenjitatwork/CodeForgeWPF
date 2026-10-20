using System.Text;
using CodeForge.Application;
using CodeForge.Security;

namespace CodeForge.Tools;

internal sealed record IndexedFile(string RelativePath, long Size);

/// <summary>Immutable snapshot of an open project: validated root, policy, and a flat file index. All file access goes through here.</summary>
internal sealed class ProjectSession
{
    public const int MaxIndexedFiles = 20000;
    public const int MaxReadChars = 40_000;
    public const int MaxLineChars = 2000;

    public ProjectInfo Info { get; }
    public ProjectPathGuard Guard { get; }
    public ProjectFilePolicy Policy { get; }
    public IReadOnlyList<IndexedFile> Files { get; private set; } = Array.Empty<IndexedFile>();
    public bool Truncated { get; private set; }

    private ProjectIndex? _index;
    private readonly object _indexLock = new();

    /// <summary>Lazily built, cached for the lifetime of this snapshot (a new snapshot is made after every refresh or applied edit).</summary>
    internal ProjectIndex GetIndex(CancellationToken ct)
    {
        lock (_indexLock) return _index ??= ProjectIndex.Build(this, ct);
    }

    private ProjectSession(ProjectInfo info)
    {
        Info = info;
        Guard = new ProjectPathGuard(info.RootPath);
        Policy = new ProjectFilePolicy(excludeBuildDir: info.Kind == ProjectKind.Flutter);
    }

    public static ProjectSession Create(ProjectInfo info, CancellationToken ct)
    {
        var s = new ProjectSession(info);
        s.Scan(ct);
        return s;
    }

    private void Scan(CancellationToken ct)
    {
        var files = new List<IndexedFile>();
        var pending = new Stack<string>();
        pending.Push(Guard.Root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (ProjectPathGuard.IsLink(child)) continue;
                if (child is DirectoryInfo d)
                {
                    if (!Policy.IsExcludedDirectory(d.Name)) pending.Push(d.FullName);
                }
                else if (child is FileInfo f && !Policy.IsSensitiveFileName(f.Name))
                {
                    if (files.Count >= MaxIndexedFiles) { Truncated = true; pending.Clear(); break; }
                    files.Add(new IndexedFile(Guard.ToRelative(f.FullName), f.Length));
                }
            }
        }
        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        Files = files;
    }

    public IReadOnlyList<ProjectEntry> List(string relativeDirectory)
    {
        var full = Guard.Resolve(relativeDirectory);
        if (!Directory.Exists(full)) throw new ProjectException("Folder not found.");
        var rel = Guard.ToRelative(full);
        if (rel != "." && Policy.IsExcludedPath(Path.Combine(rel, "x"))) throw new ProjectException("That folder is excluded by default.");

        var entries = new List<ProjectEntry>();
        try
        {
            foreach (var child in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                if (ProjectPathGuard.IsLink(child)) continue;
                var childRel = Guard.ToRelative(child.FullName);
                if (child is DirectoryInfo d)
                {
                    if (!Policy.IsExcludedDirectory(d.Name)) entries.Add(new ProjectEntry(d.Name, childRel, true, 0));
                }
                else if (child is FileInfo f && !Policy.IsSensitiveFileName(f.Name))
                    entries.Add(new ProjectEntry(f.Name, childRel, false, f.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException("Cannot read this folder: " + ex.Message, ex);
        }
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Validates a path for reading. Throws PathAccessException for escapes; otherwise reports a status.</summary>
    public bool TryOpen(string relativePath, out FileStream? stream, out string normalizedRelative, out FileReadResult? failure)
    {
        stream = null;
        failure = null;
        var full = Guard.Resolve(relativePath);
        var rel = Guard.ToRelative(full);
        normalizedRelative = rel;

        FileReadResult Fail(FileReadStatus status, string message) =>
            new(status, rel, string.Empty, 0, 0, false, message);

        if (Directory.Exists(full)) { failure = Fail(FileReadStatus.NotFound, "That path is a folder."); return false; }
        if (!File.Exists(full)) { failure = Fail(FileReadStatus.NotFound, "File not found."); return false; }
        if (Policy.IsExcludedPath(normalizedRelative))
        { failure = Fail(FileReadStatus.Blocked, "File is inside a folder excluded by default."); return false; }
        if (Policy.IsSensitivePath(normalizedRelative))
        { failure = Fail(FileReadStatus.Blocked, "File is excluded because it may contain secrets."); return false; }
        if (ProjectFilePolicy.IsBinaryExtension(Path.GetExtension(full)))
        { failure = Fail(FileReadStatus.Binary, "Binary file."); return false; }

        FileStream fs;
        try
        {
            fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = Fail(FileReadStatus.Blocked, "Cannot open file: " + ex.Message);
            return false;
        }

        var head = new byte[4096];
        var n = fs.Read(head, 0, head.Length);
        if (ProjectFilePolicy.LooksBinary(head.AsSpan(0, n)))
        {
            fs.Dispose();
            failure = Fail(FileReadStatus.Binary, "Binary file.");
            return false;
        }
        fs.Position = 0;
        stream = fs;
        return true;
    }

    public async Task<FileReadResult> ReadAsync(string relativePath, int startLine, int maxLines, CancellationToken ct)
    {
        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, 2000);
        if (!TryOpen(relativePath, out var fs, out var rel, out var failure)) return failure!;

        using (fs)
        using (var reader = new StreamReader(fs!, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var sb = new StringBuilder();
            var lineNo = 0;
            var end = startLine - 1;
            var truncated = false;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lineNo++;
                if (lineNo < startLine) continue;
                if (lineNo >= startLine + maxLines || sb.Length >= MaxReadChars) { truncated = true; break; }
                if (line.Length > MaxLineChars) line = line[..MaxLineChars] + "…";
                sb.Append(line).Append('\n');
                end = lineNo;
            }
            var message = end < startLine ? "Start line is beyond the end of the file." : null;
            return new FileReadResult(FileReadStatus.Ok, rel, sb.ToString().TrimEnd('\n'), startLine, end, truncated, message);
        }
    }

    /// <summary>Reads a whole small text file; null if it cannot be read, is blocked, binary, or larger than maxBytes.</summary>
    public string[]? ReadAllLinesOrNull(string relativePath, long maxBytes)
    {
        try
        {
            if (!TryOpen(relativePath, out var fs, out _, out _)) return null;
            using (fs)
            {
                if (fs!.Length > maxBytes) return null;
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = new List<string>();
                while (reader.ReadLine() is { } l) lines.Add(l.Length > MaxLineChars ? l[..MaxLineChars] : l);
                return lines.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathAccessException)
        {
            return null;
        }
    }
}
