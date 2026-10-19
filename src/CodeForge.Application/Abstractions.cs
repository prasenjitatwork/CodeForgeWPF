using CodeForge.Domain;

namespace CodeForge.Application;

public sealed class AppSettings
{
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultModel = "qwen2.5-coder:7b";

    public string OllamaUrl { get; set; } = DefaultOllamaUrl;
    public string Model { get; set; } = DefaultModel;
    public int RequestTimeoutSeconds { get; set; } = 300;
    public int ContextWindowTokens { get; set; } = 8192;
    public string? LastProjectPath { get; set; }
    public string SystemPrompt { get; set; } =
        "You are CodeForge, a helpful local coding assistant. Answer concisely and use Markdown code blocks for code.";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public enum ModelErrorKind { Unavailable, ModelNotFound, InvalidResponse, Timeout, Other }

public sealed class ModelException : Exception
{
    public ModelErrorKind Kind { get; }
    public ModelException(ModelErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

public sealed record ModelChatMessage(string Role, string Content);

/// <summary>Provider abstraction so other model providers can be added later.</summary>
public interface IChatModelClient
{
    /// <summary>Returns installed model names; throws <see cref="ModelException"/> when unreachable.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

    IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<ModelChatMessage> messages, CancellationToken ct);
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    event EventHandler? Changed;
}

public interface IConversationRepository
{
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default);
    Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Conversation conversation, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
