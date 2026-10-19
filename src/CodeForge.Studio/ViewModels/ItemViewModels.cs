using CodeForge.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeForge.Studio.ViewModels;

public sealed partial class MessageViewModel : ObservableObject
{
    public ChatMessage Model { get; }

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasContextNote))] private string? _contextNote;

    public bool HasContextNote => !string.IsNullOrEmpty(ContextNote);

    public MessageViewModel(ChatMessage model)
    {
        Model = model;
        _content = model.Content;
    }

    public bool IsUser => Model.Role == ChatRole.User;

    public string RoleLabel => IsUser ? "You" : "CodeForge";

    public void Append(string chunk)
    {
        Model.Content += chunk;
        Content = Model.Content;
    }
}

public sealed partial class ConversationItemViewModel : ObservableObject
{
    public Guid Id { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] private DateTimeOffset _updatedAt;

    public ConversationItemViewModel(Conversation c)
    {
        Id = c.Id;
        _title = c.Title;
        _updatedAt = c.UpdatedAt.ToLocalTime();
    }

    public void Refresh(Conversation c)
    {
        Title = c.Title;
        UpdatedAt = c.UpdatedAt.ToLocalTime();
    }
}

public enum ConnectionState { Checking, Connected, Disconnected }
