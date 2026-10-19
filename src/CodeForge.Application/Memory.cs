namespace CodeForge.Application;

public sealed record MemoryNote(long Id, string Text, DateTimeOffset CreatedAt);

public sealed record StoredPlanStep(string Text, string Status);

public sealed record StoredPlan(Guid MessageId, string Title, IReadOnlyList<StoredPlanStep> Steps);

/// <summary>Local, user-controlled memory: short notes per project, and saved plans per conversation.</summary>
public interface IMemoryStore
{
    Task<IReadOnlyList<MemoryNote>> ListNotesAsync(string projectRoot, CancellationToken ct = default);
    /// <summary>Throws <see cref="ProjectException"/> for empty, too long, secret-looking, duplicate or over-limit notes.</summary>
    Task<MemoryNote> AddNoteAsync(string projectRoot, string text, CancellationToken ct = default);
    Task DeleteNoteAsync(long id, CancellationToken ct = default);
    Task ClearNotesAsync(string projectRoot, CancellationToken ct = default);

    Task SavePlanAsync(Guid conversationId, StoredPlan plan, CancellationToken ct = default);
    Task<IReadOnlyList<StoredPlan>> LoadPlansAsync(Guid conversationId, CancellationToken ct = default);
    Task DeletePlansAsync(Guid conversationId, CancellationToken ct = default);
}
