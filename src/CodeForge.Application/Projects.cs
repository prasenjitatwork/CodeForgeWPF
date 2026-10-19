namespace CodeForge.Application;

public enum ProjectKind { Folder, Solution, CSharpProject, Flutter }

public sealed record ProjectInfo(string Name, string RootPath, ProjectKind Kind, string? EntryFile);

public sealed record ProjectEntry(string Name, string RelativePath, bool IsDirectory, long Size);

public enum FileReadStatus { Ok, NotFound, Blocked, Binary }

public sealed record FileReadResult(
    FileReadStatus Status, string RelativePath, string Content, int StartLine, int EndLine, bool Truncated, string? Message);

public sealed record FileMatch(string RelativePath, long Size);

public sealed record TextMatch(string RelativePath, int Line, string Snippet);

public sealed record Declaration(string Kind, string Name, int Line);

public sealed record ProjectContext(string Text, IReadOnlyList<string> Files);

/// <summary>Expected, user-presentable failure (bad path, access denied by policy, etc.).</summary>
public sealed class ProjectException : Exception
{
    public ProjectException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Read-only view of the open project. All paths are root-relative and validated by the Security layer.</summary>
public interface IProjectWorkspace
{
    ProjectInfo? Current { get; }
    int IndexedFileCount { get; }
    bool IndexTruncated { get; }
    bool ContextEnabled { get; set; }
    event EventHandler? Changed;

    Task<ProjectInfo> OpenAsync(string path, CancellationToken ct = default);
    Task RefreshAsync(CancellationToken ct = default);
    void Close();

    Task<IReadOnlyList<ProjectEntry>> ListDirectoryAsync(string relativeDirectory, CancellationToken ct = default);
    Task<FileReadResult> ReadFileAsync(string relativePath, int startLine = 1, int maxLines = 400, CancellationToken ct = default);
    Task<IReadOnlyList<FileMatch>> SearchFilesAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<TextMatch>> SearchTextAsync(string query, CancellationToken ct = default);
    Task<string> GetSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Declaration>> GetOutlineAsync(string relativePath, CancellationToken ct = default);
}

public interface IProjectContextProvider
{
    /// <summary>Retrieves bounded, policy-filtered excerpts relevant to the query; null when no project is open or context is disabled.</summary>
    Task<ProjectContext?> BuildContextAsync(string userQuery, CancellationToken ct = default);
}
