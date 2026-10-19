namespace CodeForge.Domain;

public enum ChatRole { System, User, Assistant }

public sealed class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChatRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; init; } = new();
}
