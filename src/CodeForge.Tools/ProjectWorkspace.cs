using System.Text;
using CodeForge.Application;
using CodeForge.Security;

namespace CodeForge.Tools;

public sealed class ProjectWorkspace : IProjectWorkspace, IProjectContextProvider
{
    private const int MaxFileResults = 100;
    private const int MaxTextResults = 200;
    private const long MaxSearchFileBytes = 1_000_000;
    private const int MaxSummaryChars = 6000;
    private const int ContextSummaryChars = 1800;

    private volatile ProjectSession? _session;
    private string? _summaryCache;

    public event EventHandler? Changed;

    public ProjectInfo? Current => _session?.Info;
    public int IndexedFileCount => _session?.Files.Count ?? 0;
    public bool IndexTruncated => _session?.Truncated ?? false;
    public bool ContextEnabled { get; set; } = true;

    private ProjectSession Require() => _session ?? throw new ProjectException("No project is open.");

    private static T Guarded<T>(Func<T> action)
    {
        try { return action(); }
        catch (PathAccessException ex) { throw new ProjectException("Access denied: " + ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException("File system error: " + ex.Message, ex);
        }
    }

    public async Task<ProjectInfo> OpenAsync(string path, CancellationToken ct = default)
    {
        var info = ProjectLocator.Locate(path);
        var session = await Task.Run(() => Guarded(() => ProjectSession.Create(info, ct)), ct).ConfigureAwait(false);
        _session = session;
        _summaryCache = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var current = Require().Info;
        var session = await Task.Run(() => Guarded(() => ProjectSession.Create(current, ct)), ct).ConfigureAwait(false);
        _session = session;
        _summaryCache = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        _session = null;
        _summaryCache = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<ProjectEntry>> ListDirectoryAsync(string relativeDirectory, CancellationToken ct = default)
    {
        var s = Require();
        return Task.Run(() => Guarded(() => s.List(relativeDirectory)), ct);
    }

    public async Task<FileReadResult> ReadFileAsync(string relativePath, int startLine = 1, int maxLines = 400, CancellationToken ct = default)
    {
        var s = Require();
        try { return await s.ReadAsync(relativePath, startLine, maxLines, ct).ConfigureAwait(false); }
        catch (PathAccessException ex) { throw new ProjectException("Access denied: " + ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException("File system error: " + ex.Message, ex);
        }
    }

    public Task<IReadOnlyList<FileMatch>> SearchFilesAsync(string query, CancellationToken ct = default)
    {
        var s = Require();
        return Task.Run<IReadOnlyList<FileMatch>>(() =>
        {
            var tokens = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Replace('\\', '/')).ToArray();
            if (tokens.Length == 0) return Array.Empty<FileMatch>();

            return s.Files
                .Select(f => (File: f, Norm: f.RelativePath.Replace('\\', '/')))
                .Where(x => tokens.All(t => Matches(x.Norm, t)))
                .OrderByDescending(x => tokens.Any(t => Path.GetFileName(x.Norm).Contains(t.Trim('*'), StringComparison.OrdinalIgnoreCase)))
                .ThenBy(x => x.Norm.Length)
                .Take(MaxFileResults)
                .Select(x => new FileMatch(x.File.RelativePath, x.File.Size))
                .ToList();
        }, ct);
    }

    private static bool Matches(string path, string token)
    {
        if (!token.Contains('*') && !token.Contains('?')) return path.Contains(token, StringComparison.OrdinalIgnoreCase);
        var pattern = "^.*" + System.Text.RegularExpressions.Regex.Escape(token).Replace("\\*", ".*").Replace("\\?", ".") + ".*$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }

    public Task<IReadOnlyList<TextMatch>> SearchTextAsync(string query, CancellationToken ct = default)
    {
        var s = Require();
        return Task.Run<IReadOnlyList<TextMatch>>(() =>
        {
            query = (query ?? "").Trim();
            if (query.Length < 2) return Array.Empty<TextMatch>();
            var results = new List<TextMatch>();
            foreach (var file in s.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.Size > MaxSearchFileBytes || file.Size == 0 ||
                    ProjectFilePolicy.IsBinaryExtension(Path.GetExtension(file.RelativePath))) continue;
                try
                {
                    if (!s.TryOpen(file.RelativePath, out var fs, out _, out _)) continue;
                    using (fs)
                    using (var reader = new StreamReader(fs!, Encoding.UTF8, true))
                    {
                        var n = 0;
                        while (reader.ReadLine() is { } line)
                        {
                            n++;
                            if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                            var snippet = line.Trim();
                            results.Add(new TextMatch(file.RelativePath, n, snippet.Length > 200 ? snippet[..200] + "…" : snippet));
                            if (results.Count >= MaxTextResults) return results;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathAccessException) { }
            }
            return results;
        }, ct);
    }

    public Task<string> GetSummaryAsync(CancellationToken ct = default)
    {
        var s = Require();
        return Task.Run(() => Guarded(() => _summaryCache ??= ProjectInspector.Summarize(s, MaxSummaryChars)), ct);
    }

    public Task<IReadOnlyList<Declaration>> GetOutlineAsync(string relativePath, CancellationToken ct = default)
    {
        var s = Require();
        return Task.Run<IReadOnlyList<Declaration>>(() => Guarded(() =>
        {
            var lines = s.ReadAllLinesOrNull(relativePath, 1_000_000)
                        ?? throw new ProjectException("That file cannot be read (blocked, binary, or too large).");
            return CodeOutliner.Outline(Path.GetExtension(relativePath), lines);
        }), ct);
    }

    public async Task<ProjectContext?> BuildContextAsync(string userQuery, CancellationToken ct = default)
    {
        var s = _session;
        if (s is null || !ContextEnabled) return null;
        return await Task.Run(() =>
        {
            var summary = ProjectInspector.Summarize(s, ContextSummaryChars);
            return ContextBuilder.Build(s, userQuery ?? "", summary, ct);
        }, ct).ConfigureAwait(false);
    }
}
