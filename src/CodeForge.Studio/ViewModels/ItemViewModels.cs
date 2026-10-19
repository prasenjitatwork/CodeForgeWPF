using System.Collections.ObjectModel;
using CodeForge.Application;
using CodeForge.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeForge.Studio.ViewModels;

public enum ChangeState { Pending, Applied, Rejected, Undone, Failed }

public sealed class DiffLineViewModel
{
    public DiffLineViewModel(DiffLine l)
    {
        Kind = l.Kind;
        var no = l.NewLine ?? l.OldLine;
        Number = l.Kind == DiffKind.Gap ? "" : no?.ToString() ?? "";
        Prefix = l.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", DiffKind.Gap => "", _ => " " };
        Text = l.Kind == DiffKind.Gap ? "⋯" : l.Text;
    }
    public DiffKind Kind { get; }
    public string Number { get; }
    public string Prefix { get; }
    public string Text { get; }
}

public sealed partial class ChangeViewModel : ObservableObject
{
    private readonly IEditService _edits;
    private readonly Func<Task> _afterChange;

    public FileChange Change { get; }
    public IReadOnlyList<DiffLineViewModel> Lines { get; }
    public string Title => (Change.Kind == EditKind.Create ? "New file: " : "") + Change.RelativePath;
    public string Stats => $"+{Change.Added} −{Change.Removed}";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanUndo), nameof(CanReject), nameof(StateText))]
    private ChangeState _state = ChangeState.Pending;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanApply => State == ChangeState.Pending && !_busy;
    public bool CanReject => State == ChangeState.Pending && !_busy;
    public bool CanUndo => State == ChangeState.Applied && !_busy;
    public string StateText => State switch
    {
        ChangeState.Applied => "Applied", ChangeState.Rejected => "Rejected", ChangeState.Undone => "Undone",
        ChangeState.Failed => "Failed", _ => "Pending approval"
    };

    private bool _busy;

    public ChangeViewModel(FileChange change, IEditService edits, Func<Task> afterChange)
    {
        Change = change; _edits = edits; _afterChange = afterChange;
        Lines = change.Diff.Select(d => new DiffLineViewModel(d)).ToList();
    }

    private void SetBusy(bool v)
    {
        _busy = v;
        OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(CanReject)); OnPropertyChanged(nameof(CanUndo));
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply) return;
        SetBusy(true); Error = null;
        try { await _edits.ApplyAsync(Change); State = ChangeState.Applied; await _afterChange(); }
        catch (Exception ex) { Error = ex.Message; State = ChangeState.Failed; }
        finally { SetBusy(false); }
    }

    [RelayCommand]
    private void Reject()
    {
        if (CanReject) State = ChangeState.Rejected;
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!CanUndo) return;
        SetBusy(true); Error = null;
        try { await _edits.UndoAsync(Change); State = ChangeState.Undone; await _afterChange(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { SetBusy(false); }
    }
}

public sealed partial class MessageViewModel : ObservableObject
{
    public ChatMessage Model { get; }

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasContextNote))] private string? _contextNote;

    public bool HasContextNote => !string.IsNullOrEmpty(ContextNote);

    public ObservableCollection<ChangeViewModel> Changes { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblems))] private string? _problems;
    public bool HasProblems => !string.IsNullOrEmpty(Problems);
    public bool HasChanges => Changes.Count > 0;
    public void RaiseChangesChanged() => OnPropertyChanged(nameof(HasChanges));

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
