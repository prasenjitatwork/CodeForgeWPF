using System.Collections.ObjectModel;
using CodeForge.Agent;
using CodeForge.Application;
using CodeForge.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeForge.Studio.ViewModels;

public enum ChangeState { Pending, Applied, Rejected, Undone, Failed, Stale }

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
    private readonly Func<ChangeViewModel, Task>? _stateChanged;

    public FileChange Change { get; }
    public IReadOnlyList<DiffLineViewModel> Lines { get; }
    public string Title => Change.Kind switch
    {
        EditKind.Create => "New file: " + Change.RelativePath,
        EditKind.Delete => "Delete file: " + Change.RelativePath,
        EditKind.Rename => $"Rename: {Change.RelativePath} → {Change.NewPath}",
        _ => Change.RelativePath
    };
    public string Stats => $"+{Change.Added} −{Change.Removed}";

    /// <summary>Delete and rename are never applied automatically, even after folder permission.</summary>
    public bool RequiresManualApproval => Change.Kind is EditKind.Delete or EditKind.Rename;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanUndo), nameof(CanReject), nameof(StateText))]
    private ChangeState _state = ChangeState.Pending;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNote))] private string? _note;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool CanApply => State == ChangeState.Pending && !_busy;
    public bool CanReject => State is ChangeState.Pending or ChangeState.Stale && !_busy;
    public bool CanUndo => State == ChangeState.Applied && !_busy;
    public string StateText => State switch
    {
        ChangeState.Applied => "Applied", ChangeState.Rejected => "Rejected", ChangeState.Undone => "Undone",
        ChangeState.Failed => "Failed", ChangeState.Stale => "Out of date", _ => RequiresManualApproval ? "Needs your approval" : "Pending approval"
    };

    private bool _busy;

    public ChangeViewModel(FileChange change, IEditService edits, Func<Task> afterChange, Func<ChangeViewModel, Task>? stateChanged = null)
    {
        Change = change; _edits = edits; _afterChange = afterChange; _stateChanged = stateChanged;
        Lines = change.Diff.Select(d => new DiffLineViewModel(d)).ToList();
    }

    /// <summary>Used when rehydrating a saved proposal after revalidation.</summary>
    public void MarkRestored(ChangeState state, string? note)
    {
        State = state;
        if (state == ChangeState.Stale) Error = note; else Note = note;
    }

    private void SetBusy(bool v)
    {
        _busy = v;
        OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(CanReject)); OnPropertyChanged(nameof(CanUndo));
    }

    private async Task NotifyAsync()
    {
        if (_stateChanged is not null) await _stateChanged(this);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply) return;
        SetBusy(true); Error = null;
        try { await _edits.ApplyAsync(Change); State = ChangeState.Applied; await NotifyAsync(); await _afterChange(); }
        catch (Exception ex) { Error = ex.Message; State = ChangeState.Failed; await NotifyAsync(); }
        finally { SetBusy(false); }
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (!CanReject) return;
        State = ChangeState.Rejected;
        await NotifyAsync();
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!CanUndo) return;
        SetBusy(true); Error = null;
        try { await _edits.UndoAsync(Change); State = ChangeState.Undone; await NotifyAsync(); await _afterChange(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { SetBusy(false); }
    }
}

public sealed partial class PlanStepViewModel : ObservableObject
{
    public int Number { get; }
    public string Text { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(CanRun), nameof(CanDone), nameof(CanSkip))]
    private PlanStepStatus _status = PlanStepStatus.Pending;

    public string Icon => Status switch
    {
        PlanStepStatus.Done => "✓", PlanStepStatus.Running => "▶", PlanStepStatus.Skipped => "⏭", _ => "○"
    };
    public bool CanRun => Status != PlanStepStatus.Done;
    public bool CanDone => Status is PlanStepStatus.Pending or PlanStepStatus.Running;
    public bool CanSkip => Status is PlanStepStatus.Pending or PlanStepStatus.Running;

    public PlanStepViewModel(int number, string text) { Number = number; Text = text; }
}

public sealed partial class PlanViewModel : ObservableObject
{
    public string Title { get; }
    public List<PlanStepViewModel> Steps { get; }
    public event EventHandler<(PlanViewModel Plan, PlanStepViewModel Step)>? StepRequested;

    public event EventHandler? Changed;

    public PlanViewModel(ParsedPlan plan) : this(plan.Title, plan.Steps.Select(s => (s, PlanStepStatus.Pending))) { }

    public PlanViewModel(StoredPlan stored) : this(stored.Title, stored.Steps.Select(s =>
    {
        var st = Enum.TryParse<PlanStepStatus>(s.Status, out var v) ? v : PlanStepStatus.Pending;
        return (s.Text, st == PlanStepStatus.Running ? PlanStepStatus.Pending : st); // a run cannot survive a restart
    })) { }

    private PlanViewModel(string title, IEnumerable<(string Text, PlanStepStatus Status)> steps)
    {
        Title = title;
        Steps = steps.Select((s, i) => new PlanStepViewModel(i + 1, s.Text) { Status = s.Status }).ToList();
        foreach (var s in Steps)
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PlanStepViewModel.Status)) return;
                OnPropertyChanged(nameof(Progress));
                Changed?.Invoke(this, EventArgs.Empty);
            };
    }

    public StoredPlan ToStored(Guid messageId) =>
        new(messageId, Title, Steps.Select(s => new StoredPlanStep(s.Text, s.Status.ToString())).ToList());

    public string Progress
    {
        get
        {
            var done = Steps.Count(s => s.Status == PlanStepStatus.Done);
            var skipped = Steps.Count(s => s.Status == PlanStepStatus.Skipped);
            return $"{done}/{Steps.Count} done" + (skipped > 0 ? $", {skipped} skipped" : "");
        }
    }

    public string BuildStepPrompt(PlanStepViewModel step) =>
        PlanParser.StepPrompt(Title, Steps.Select(s => (s.Text, s.Status)).ToList(), Steps.IndexOf(step));

    [RelayCommand] private void RunStep(PlanStepViewModel? step) { if (step is { CanRun: true }) StepRequested?.Invoke(this, (this, step)); }
    [RelayCommand] private void MarkDone(PlanStepViewModel? step) { if (step is { CanDone: true }) step.Status = PlanStepStatus.Done; }
    [RelayCommand] private void Skip(PlanStepViewModel? step) { if (step is { CanSkip: true }) step.Status = PlanStepStatus.Skipped; }

    [RelayCommand]
    private void RunNext()
    {
        var next = Steps.FirstOrDefault(s => s.Status == PlanStepStatus.Running) ?? Steps.FirstOrDefault(s => s.Status == PlanStepStatus.Pending);
        if (next is not null) StepRequested?.Invoke(this, (this, next));
    }
}

public sealed partial class MessageViewModel : ObservableObject
{
    public ChatMessage Model { get; }

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasContextNote))] private string? _contextNote;

    public bool HasContextNote => !string.IsNullOrEmpty(ContextNote);

    /// <summary>Exact files and excerpts sent to the model for this reply (shown in the "Inspect context" expander).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasContextDetails))] private string? _contextDetails;
    public bool HasContextDetails => !string.IsNullOrEmpty(ContextDetails);

    public void SetContext(ProjectContext c)
    {
        ContextNote = c.Files.Count == 0 ? "Project context: overview only" : "Project context: " + string.Join(", ", c.Files);
        ContextDetails = FormatContext(c);
    }

    public static string FormatContext(ProjectContext c)
    {
        if (c.Details.Count == 0)
            return "Only the project overview (name, type, file counts) was sent. No file excerpts matched this question.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{c.Details.Count} excerpt(s) were sent to the local model (untrusted data, size-limited; secret files are never included):");
        foreach (var d in c.Details)
        {
            sb.AppendLine().Append(d.File).Append("  (").Append(d.Range).Append(")  — ").AppendLine(d.Reason);
            sb.AppendLine(d.Text);
        }
        return sb.ToString().TrimEnd();
    }

    public ObservableCollection<ChangeViewModel> Changes { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblems))] private string? _problems;
    public bool HasProblems => !string.IsNullOrEmpty(Problems);
    public bool HasChanges => Changes.Count > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPlan))] private PlanViewModel? _plan;
    public bool HasPlan => Plan is not null;
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
