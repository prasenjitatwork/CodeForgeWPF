namespace CodeForge.Application;

public enum EditKind { Modify, Create, Delete, Rename }

public enum DiffKind { Context, Added, Removed, Gap }

public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>
/// A reviewed-but-not-yet-applied change to one file. Nothing is written until the user approves it.
/// For Rename, RelativePath is the current path and NewPath is the destination.
/// </summary>
public sealed record FileChange(
    Guid Id,
    string RelativePath,
    EditKind Kind,
    string OriginalContent,
    string NewContent,
    IReadOnlyList<DiffLine> Diff,
    int Added,
    int Removed,
    string? BaseHash,
    string? NewPath = null);

/// <summary>Result of turning model output into concrete changes; Problems are shown to the user, never silently dropped.</summary>
public sealed record PreparedEdits(IReadOnlyList<FileChange> Changes, IReadOnlyList<string> Problems);

/// <summary>What the service remembers about a change it applied, so it can be persisted and undone after a restart.</summary>
public sealed record AppliedInfo(string RelativePath, EditKind Kind, string AppliedHash, string? BackupPath, string? OldRelativePath);

public enum RestoreStatus { Pending, Applied, Stale, OtherProject }

/// <summary>Outcome of revalidating a persisted proposal. Change carries the rebuilt diff when the status is Pending or Applied.</summary>
public sealed record RestoreOutcome(RestoreStatus Status, string? Reason, FileChange? Change = null);

public interface IEditService
{
    /// <summary>Parses SEARCH/REPLACE blocks (and DELETE/RENAME lines) from assistant text and builds validated diffs. Writes nothing.</summary>
    Task<PreparedEdits> PrepareAsync(string assistantText, CancellationToken ct = default);

    /// <summary>Applies one approved change after re-validating path, policy and that the file has not changed. Backs up first.</summary>
    Task ApplyAsync(FileChange change, CancellationToken ct = default);

    /// <summary>Restores the pre-apply state of a change applied earlier (only if the file is still as applied).</summary>
    Task UndoAsync(FileChange change, CancellationToken ct = default);

    /// <summary>Details of a change applied by this service, for persistence; null if not applied.</summary>
    AppliedInfo? GetApplied(Guid id);

    /// <summary>
    /// Revalidates a persisted proposal (project, paths, policy, file contents) and, if still valid, re-arms it so it
    /// can be applied (applied == null) or undone (applied != null). Never writes.
    /// </summary>
    Task<RestoreOutcome> RestoreAsync(FileChange change, string projectRoot, AppliedInfo? applied, CancellationToken ct = default);
}

/// <summary>A proposal saved so it survives an app restart.</summary>
public sealed record StoredEdit(
    Guid Id, Guid ConversationId, Guid MessageId, string ProjectRoot,
    string RelativePath, EditKind Kind, string OriginalContent, string NewContent, string? BaseHash, string? NewPath,
    AppliedInfo? Applied);

public interface IPendingEditStore
{
    Task SaveAsync(StoredEdit edit, CancellationToken ct = default);
    Task<IReadOnlyList<StoredEdit>> ListAsync(Guid conversationId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteForConversationAsync(Guid conversationId, CancellationToken ct = default);
}
