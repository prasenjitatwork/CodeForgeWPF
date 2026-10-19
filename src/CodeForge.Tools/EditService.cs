using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeForge.Application;
using CodeForge.Security;

namespace CodeForge.Tools;

/// <summary>
/// The only component that writes to the project. Changes are built from model output, shown as diffs,
/// and applied only on explicit approval, after re-validating the path, policy and file freshness.
/// </summary>
public sealed class EditService : IEditService
{
    public const long MaxFileBytes = 1_000_000;

    private sealed record Prepared(string Root, FileChange Change);
    private sealed record Applied(string Root, string RelativePath, EditKind Kind, string AppliedHash, string? BackupPath);
    private sealed record DiskFile(string Text, string Hash, bool Bom, bool Crlf);

    private readonly ProjectWorkspace _workspace;
    private readonly string _backupRoot;
    private readonly ConcurrentDictionary<Guid, Prepared> _prepared = new();
    private readonly ConcurrentDictionary<Guid, Applied> _applied = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public EditService(ProjectWorkspace workspace, string backupRoot)
    {
        _workspace = workspace;
        _backupRoot = backupRoot;
    }

    private static DiskFile? ReadDisk(string full)
    {
        var info = new FileInfo(full);
        if (!info.Exists) return null;
        if (info.Length > MaxFileBytes) throw new ProjectException("File is larger than the 1 MB edit limit.");
        var bytes = File.ReadAllBytes(full);
        if (ProjectFilePolicy.LooksBinary(bytes)) throw new ProjectException("File looks binary and cannot be edited.");
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        try { text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { throw new ProjectException("File is not valid UTF-8; editing is not supported."); }
        var crlf = text.Contains("\r\n");
        return new DiskFile(text.Replace("\r\n", "\n"), Convert.ToHexString(SHA256.HashData(bytes)), bom, crlf);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteAtomic(string full, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".cf_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, full, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static byte[] Encode(string normalized, bool bom, bool crlf)
    {
        var text = crlf ? normalized.Replace("\n", "\r\n") : normalized;
        var body = Encoding.UTF8.GetBytes(text);
        return bom ? Encoding.UTF8.GetPreamble().Concat(body).ToArray() : body;
    }

    private static (string Full, string Rel) ResolveWritable(ProjectSession s, string path)
    {
        var full = s.Guard.Resolve(path);
        var rel = s.Guard.ToRelative(full);
        if (rel == ".") throw new ProjectException("A file path is required.");
        if (Directory.Exists(full)) throw new ProjectException($"'{rel}' is a folder.");
        if (s.Policy.IsExcludedPath(rel)) throw new ProjectException($"'{rel}' is inside a protected or generated folder.");
        if (s.Policy.IsSensitivePath(rel)) throw new ProjectException($"'{rel}' may contain secrets and cannot be edited.");
        if (ProjectFilePolicy.IsBinaryExtension(Path.GetExtension(full))) throw new ProjectException($"'{rel}' is a binary file type.");
        if (File.Exists(full) && ProjectPathGuard.IsLink(new FileInfo(full))) throw new ProjectException($"'{rel}' is a link.");
        return (full, rel);
    }

    public Task<PreparedEdits> PrepareAsync(string assistantText, CancellationToken ct = default) =>
        Task.Run(() => Prepare(assistantText ?? "", ct), ct);

    private PreparedEdits Prepare(string text, CancellationToken ct)
    {
        var s = _workspace.Session ?? throw new ProjectException("No project is open.");
        var (blocks, problems) = EditParser.Parse(text);
        var changes = new List<FileChange>();

        foreach (var group in blocks.GroupBy(b => b.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (full, rel) = ResolveWritable(s, group.Key);
                var disk = ReadDisk(full);
                var original = disk?.Text ?? "";
                var content = original;
                var first = true;
                foreach (var b in group)
                {
                    var search = b.Search.Replace("\r\n", "\n");
                    var replace = b.Replace.Replace("\r\n", "\n");
                    if (search.Trim().Length == 0)
                    {
                        if (disk is not null || !first) throw new ProjectException("An empty SEARCH is only allowed to create a new file.");
                        content = replace;
                    }
                    else content = ApplyBlock(content, search, replace);
                    first = false;
                }
                if (disk is not null && content == original) { problems.Add($"{rel}: the proposed change makes no difference."); continue; }
                if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes) throw new ProjectException("Result would exceed the 1 MB edit limit.");

                var (diff, added, removed) = DiffEngine.Diff(original, content);
                var change = new FileChange(Guid.NewGuid(), rel.Replace('\\', '/'), disk is null ? EditKind.Create : EditKind.Modify,
                    original, content, diff, added, removed, disk?.Hash);
                _prepared[change.Id] = new Prepared(s.Guard.Root, change);
                changes.Add(change);
            }
            catch (PathAccessException ex) { problems.Add($"{group.Key}: access denied — {ex.Message}"); }
            catch (ProjectException ex) { problems.Add($"{group.Key}: {ex.Message}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{group.Key}: {ex.Message}"); }
        }
        return new PreparedEdits(changes, problems);
    }

    private static string ApplyBlock(string content, string search, string replace)
    {
        var idx = content.IndexOf(search, StringComparison.Ordinal);
        if (idx >= 0)
        {
            if (content.IndexOf(search, idx + 1, StringComparison.Ordinal) >= 0)
                throw new ProjectException("The SEARCH text matches more than once; it must be unique.");
            return content[..idx] + replace + content[(idx + search.Length)..];
        }

        // Fallback: tolerate differences in trailing whitespace only.
        var lines = content.Split('\n');
        var sLines = search.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var hits = new List<int>();
        for (var i = 0; i + sLines.Length <= lines.Length; i++)
        {
            var ok = true;
            for (var k = 0; k < sLines.Length && ok; k++) ok = lines[i + k].TrimEnd() == sLines[k];
            if (ok) hits.Add(i);
        }
        if (hits.Count == 0) throw new ProjectException("The SEARCH text was not found in the file (the file may differ from what the model assumed).");
        if (hits.Count > 1) throw new ProjectException("The SEARCH text matches more than once; it must be unique.");
        var result = lines[..hits[0]].Concat(replace.Split('\n')).Concat(lines[(hits[0] + sLines.Length)..]);
        return string.Join('\n', result);
    }

    public async Task ApplyAsync(FileChange change, CancellationToken ct = default)
    {
        if (!_prepared.TryGetValue(change.Id, out var prepared))
            throw new ProjectException("This change was not prepared by this session and cannot be applied.");
        var s = _workspace.Session ?? throw new ProjectException("No project is open.");
        if (!string.Equals(s.Guard.Root, prepared.Root, StringComparison.OrdinalIgnoreCase))
            throw new ProjectException("The project changed since this edit was proposed. Ask again.");
        if (_applied.ContainsKey(change.Id)) throw new ProjectException("This change was already applied.");
        var c = prepared.Change;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    var (full, rel) = ResolveWritable(s, c.RelativePath);
                    var disk = ReadDisk(full);
                    string? backup = null;
                    byte[] bytes;
                    if (c.Kind == EditKind.Create)
                    {
                        if (disk is not null || File.Exists(full)) throw new ProjectException("The file now exists; refusing to overwrite it.");
                        bytes = Encode(c.NewContent, false, Environment.NewLine == "\r\n");
                    }
                    else
                    {
                        if (disk is null) throw new ProjectException("The file no longer exists.");
                        if (disk.Hash != c.BaseHash) throw new ProjectException("The file changed on disk since the proposal. Ask for the edit again.");
                        backup = Path.Combine(_backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + c.Id.ToString("N")[..8], rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(full, backup, overwrite: false);
                        bytes = Encode(c.NewContent, disk.Bom, disk.Crlf);
                    }
                    WriteAtomic(full, bytes);
                    _applied[c.Id] = new Applied(s.Guard.Root, rel, c.Kind, Hash(bytes), backup);
                }
                catch (PathAccessException ex) { throw new ProjectException("Access denied: " + ex.Message, ex); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ProjectException("Could not write the file: " + ex.Message, ex);
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        await _workspace.RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(FileChange change, CancellationToken ct = default)
    {
        if (!_applied.TryGetValue(change.Id, out var a)) throw new ProjectException("Nothing to undo for this change.");
        var s = _workspace.Session ?? throw new ProjectException("No project is open.");
        if (!string.Equals(s.Guard.Root, a.Root, StringComparison.OrdinalIgnoreCase))
            throw new ProjectException("A different project is open now; cannot undo.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    var (full, _) = ResolveWritable(s, a.RelativePath);
                    if (!File.Exists(full) || Hash(File.ReadAllBytes(full)) != a.AppliedHash)
                        throw new ProjectException("The file was modified after the change was applied; refusing to undo over newer work.");
                    if (a.Kind == EditKind.Create) File.Delete(full);
                    else WriteAtomic(full, File.ReadAllBytes(a.BackupPath!));
                    _applied.TryRemove(change.Id, out _);
                }
                catch (PathAccessException ex) { throw new ProjectException("Access denied: " + ex.Message, ex); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ProjectException("Could not restore the file: " + ex.Message, ex);
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        await _workspace.RefreshAsync(ct).ConfigureAwait(false);
    }
}
