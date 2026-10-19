namespace CodeForge.Application;

public enum EditKind { Modify, Create }

public enum DiffKind { Context, Added, Removed, Gap }

public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>A reviewed-but-not-yet-applied change to one file. Nothing is written until the user approves it.</summary>
public sealed record FileChange(
    Guid Id,
    string RelativePath,
    EditKind Kind,
    string OriginalContent,
    string NewContent,
    IReadOnlyList<DiffLine> Diff,
    int Added,
    int Removed,
    string? BaseHash);

/// <summary>Result of turning model output into concrete changes; Problems are shown to the user, never silently dropped.</summary>
public sealed record PreparedEdits(IReadOnlyList<FileChange> Changes, IReadOnlyList<string> Problems);

public interface IEditService
{
    /// <summary>Parses SEARCH/REPLACE blocks from assistant text and builds validated diffs. Writes nothing.</summary>
    Task<PreparedEdits> PrepareAsync(string assistantText, CancellationToken ct = default);

    /// <summary>Applies one approved change after re-validating path, policy and that the file has not changed. Backs up first.</summary>
    Task ApplyAsync(FileChange change, CancellationToken ct = default);

    /// <summary>Restores the pre-apply state of a change applied in this session (only if the file is still as applied).</summary>
    Task UndoAsync(FileChange change, CancellationToken ct = default);
}
