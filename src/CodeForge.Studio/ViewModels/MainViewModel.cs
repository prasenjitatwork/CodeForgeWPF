using System.Collections.ObjectModel;
using CodeForge.Agent;
using CodeForge.Application;
using CodeForge.Domain;
using CodeForge.Studio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeForge.Studio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ChatAgent _agent;
    private readonly IChatModelClient _client;
    private readonly IConversationRepository _repo;
    private readonly ISettingsStore _settings;
    private readonly IUiServices _ui;
    private readonly IEditService _edits;
    private readonly HashSet<string> _writeGranted = new(StringComparer.OrdinalIgnoreCase);
    private (PlanViewModel Plan, PlanStepViewModel Step)? _pendingStep;

    [ObservableProperty] private bool _planMode;

    private async void OnStepRequested(object? sender, (PlanViewModel Plan, PlanStepViewModel Step) e)
    {
        if (IsGenerating) { ErrorMessage = "Wait for the current reply to finish before running a step."; return; }
        ErrorMessage = null;
        e.Step.Status = PlanStepStatus.Running;
        _pendingStep = e;
        InputText = e.Plan.BuildStepPrompt(e.Step);
        PlanMode = false;
        await SendAsync();
    }
    private readonly ILogger<MainViewModel> _log;

    private Conversation _current = new();
    private CancellationTokenSource? _generationCts;
    private bool _suppressModelSave;

    private string _lastUrl = string.Empty;

    public MainViewModel(ChatAgent agent, IChatModelClient client, IConversationRepository repo,
        ISettingsStore settings, IUiServices ui, ProjectViewModel project, IEditService edits, ILogger<MainViewModel> log)
    {
        _agent = agent; _client = client; _repo = repo; _settings = settings; _ui = ui; _log = log; _edits = edits;
        Project = project;
        Project.PromptRequested += async (_, prompt) =>
        {
            if (IsGenerating) return;
            InputText = prompt;
            await SendAsync();
        };
        Project.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectViewModel.HasProject) && !Project.HasProject) ShowProjectTab = false;
        };
        _settings.Changed += async (_, _) =>
        {
            var url = _settings.Current.OllamaUrl;
            if (url == _lastUrl) return;
            _lastUrl = url;
            await RefreshConnectionAsync();
        };
    }

    public ProjectViewModel Project { get; }

    [RelayCommand] private void SelectChatsTab() => ShowProjectTab = false;
    [RelayCommand] private void SelectProjectTab() => ShowProjectTab = true;

    [ObservableProperty] private bool _showProjectTab;
    public bool ShowChatsTab => !ShowProjectTab;
    partial void OnShowProjectTabChanged(bool value) => OnPropertyChanged(nameof(ShowChatsTab));


    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = new();
    public ObservableCollection<MessageViewModel> Messages { get; } = new();
    public ObservableCollection<string> Models { get; } = new();

    [ObservableProperty] private ConversationItemViewModel? _selectedConversation;
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _currentTitle = "New conversation";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _connectionText = "Checking Ollama…";
    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.Checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditConversations))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(NewConversationCommand), nameof(DeleteConversationCommand))]
    private bool _isGenerating;

    public bool CanEditConversations => !IsGenerating;
    public bool HasMessages => Messages.Count > 0;

    public async Task InitializeAsync()
    {
        try
        {
            await _settings.LoadAsync();
            var list = await _repo.ListAsync();
            foreach (var c in list) Conversations.Add(new ConversationItemViewModel(c));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load conversation history");
            ErrorMessage = "Could not load saved conversations: " + ex.Message;
        }
        await StartNewConversationCoreAsync();
        _lastUrl = _settings.Current.OllamaUrl;
        await RefreshConnectionAsync();
        await Project.RestoreLastProjectAsync();
        if (Project.HasProject) ShowProjectTab = true;
    }

    [RelayCommand]
    private async Task RefreshConnectionAsync()
    {
        ConnectionState = ConnectionState.Checking;
        ConnectionText = "Checking Ollama…";
        try
        {
            var models = await _client.ListModelsAsync(CancellationToken.None);
            _suppressModelSave = true;
            Models.Clear();
            foreach (var m in models) Models.Add(m);
            var wanted = _settings.Current.Model;
            SelectedModel = Models.Contains(wanted) ? wanted : Models.FirstOrDefault();
            _suppressModelSave = false;
            ConnectionState = ConnectionState.Connected;
            if (models.Count == 0)
                ConnectionText = $"Connected — no models installed. Run: ollama pull {AppSettings.DefaultModel}";
            else if (!Models.Contains(wanted))
                ConnectionText = $"Connected — configured model '{wanted}' is not installed (using '{SelectedModel}')";
            else
                ConnectionText = $"Connected — {models.Count} model(s)";
            if (ErrorMessage is not null && ErrorMessage.StartsWith("Cannot reach", StringComparison.Ordinal)) ErrorMessage = null;
        }
        catch (ModelException ex)
        {
            _suppressModelSave = false;
            ConnectionState = ConnectionState.Disconnected;
            ConnectionText = ex.Message;
        }
        catch (Exception ex)
        {
            _suppressModelSave = false;
            _log.LogError(ex, "Unexpected error checking connection");
            ConnectionState = ConnectionState.Disconnected;
            ConnectionText = "Unexpected error: " + ex.Message;
        }
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_suppressModelSave || string.IsNullOrEmpty(value) || value == _settings.Current.Model) return;
        var s = _settings.Current.Clone();
        s.Model = value;
        _ = SaveModelAsync(s);
    }

    private async Task SaveModelAsync(AppSettings s)
    {
        try
        {
            _suppressModelSave = true;
            await _settings.SaveAsync(s);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save model selection"); }
        finally { _suppressModelSave = false; }
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (value is null || value.Id == _current.Id) return;
        _ = LoadConversationAsync(value.Id);
    }

    private async Task LoadConversationAsync(Guid id)
    {
        try
        {
            var conv = await _repo.GetAsync(id);
            if (conv is null) return;
            ShowConversation(conv);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open conversation");
            ErrorMessage = "Could not open conversation: " + ex.Message;
        }
    }

    private void ShowConversation(Conversation conv)
    {
        _current = conv;
        Messages.Clear();
        foreach (var m in conv.Messages) Messages.Add(new MessageViewModel(m));
        CurrentTitle = conv.Title;
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasMessages));
    }

    private Task StartNewConversationCoreAsync()
    {
        ShowConversation(new Conversation());
        SelectedConversation = null;
        return Task.CompletedTask;
    }

    private bool CanChangeConversation() => !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanChangeConversation))]
    private Task NewConversationAsync() => StartNewConversationCoreAsync();

    [RelayCommand(CanExecute = nameof(CanChangeConversation))]
    private async Task DeleteConversationAsync(ConversationItemViewModel? item)
    {
        item ??= SelectedConversation;
        if (item is null) return;
        try
        {
            await _repo.DeleteAsync(item.Id);
            var wasCurrent = item.Id == _current.Id;
            Conversations.Remove(item);
            if (wasCurrent) await StartNewConversationCoreAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete conversation");
            ErrorMessage = "Could not delete conversation: " + ex.Message;
        }
    }

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (IsGenerating) return;
        var text = InputText.Trim();
        if (text.Length == 0) return;
        var model = SelectedModel ?? _settings.Current.Model;
        var planMode = PlanMode;
        var stepCtx = _pendingStep;
        _pendingStep = null;

        ErrorMessage = null;
        InputText = string.Empty;
        IsGenerating = true;
        _generationCts = new CancellationTokenSource();
        var ct = _generationCts.Token;

        var userMsg = new ChatMessage { Role = ChatRole.User, Content = text };
        _current.Messages.Add(userMsg);
        if (_current.Messages.Count == 1)
            _current.Title = text.Length > 40 ? text[..40].TrimEnd() + "…" : text;
        _current.UpdatedAt = DateTimeOffset.UtcNow;
        Messages.Add(new MessageViewModel(userMsg));
        CurrentTitle = _current.Title;

        var assistantModel = new ChatMessage { Role = ChatRole.Assistant };
        var assistantVm = new MessageViewModel(assistantModel) { IsStreaming = true };
        Messages.Add(assistantVm);
        OnPropertyChanged(nameof(HasMessages));
        await PersistAsync();

        try
        {
            await foreach (var chunk in _agent.RespondAsync(_current, model, ct,
                files => assistantVm.ContextNote = files.Count == 0 ? "Project context: overview only" : "Project context: " + string.Join(", ", files),
                                planMode))
                                assistantVm.Append(chunk);
                            if (assistantModel.Content.Length == 0)
                                ErrorMessage = "The model returned an empty response.";
                            else if (!ct.IsCancellationRequested)
                            {
                                if (planMode) AttachPlan(assistantVm, assistantModel.Content);
                                else
                                {
                                    await ProposeEditsAsync(assistantVm, assistantModel.Content);
                                    if (stepCtx is { } s && s.Step.Status == PlanStepStatus.Running
                                        && assistantVm.Changes.Count > 0 && !assistantVm.HasProblems
                                        && assistantVm.Changes.All(c => c.State == ChangeState.Applied))
                                        s.Step.Status = PlanStepStatus.Done;
                                }
                            }
                        }
        catch (OperationCanceledException) { /* user pressed Stop; keep partial output */ }
        catch (ModelException ex)
        {
            ErrorMessage = ex.Message;
            if (ex.Kind == ModelErrorKind.Unavailable) await RefreshConnectionAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error during generation");
            ErrorMessage = "Unexpected error: " + ex.Message;
        }
        finally
        {
            assistantVm.IsStreaming = false;
            if (assistantModel.Content.Length == 0)
                Messages.Remove(assistantVm);
            else
                _current.Messages.Add(assistantModel);
            _current.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistAsync();
            _generationCts.Dispose();
            _generationCts = null;
            IsGenerating = false;
            OnPropertyChanged(nameof(HasMessages));
        }
    }

    private void AttachPlan(MessageViewModel vm, string content)
    {
        var parsed = PlanParser.Parse(content);
        if (parsed is null)
        {
            vm.Problems = "No step list was found in that reply, so no plan was created. Try asking again.";
            return;
        }
        var plan = new PlanViewModel(parsed);
        plan.StepRequested += OnStepRequested;
        vm.Plan = plan;
        PlanMode = false;
    }

    private async Task ProposeEditsAsync(MessageViewModel vm, string content)
    {
        if (!Project.HasProject || !content.Contains("<<<<<<< SEARCH")) return;
        try
        {
            var prepared = await _edits.PrepareAsync(content);
            foreach (var c in prepared.Changes)
                vm.Changes.Add(new ChangeViewModel(c, _edits, () => Project.RefreshCommand.ExecuteAsync(null)));
            if (prepared.Problems.Count > 0) vm.Problems = string.Join("\n", prepared.Problems);
            vm.RaiseChangesChanged();

            var root = Project.Info?.RootPath;
            if (root is null || vm.Changes.Count == 0) return;
            if (!_writeGranted.Contains(root))
            {
                if (!_ui.ConfirmWriteAccess(root)) return; // stays Pending; manual Apply still available
                _writeGranted.Add(root);
            }
            foreach (var change in vm.Changes) await change.ApplyCommand.ExecuteAsync(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not prepare edits");
            vm.Problems = "Could not prepare edits: " + ex.Message;
        }
    }

    // ChatAgent reads _current.Messages as history; the assistant reply is appended only after streaming ends.
    private async Task PersistAsync()
    {
        try
        {
            await _repo.SaveAsync(_current);
            var existing = Conversations.FirstOrDefault(c => c.Id == _current.Id);
            if (existing is null)
            {
                existing = new ConversationItemViewModel(_current);
                Conversations.Insert(0, existing);
                _suppressSelection(existing);
            }
            else
            {
                existing.Refresh(_current);
                var idx = Conversations.IndexOf(existing);
                if (idx > 0) Conversations.Move(idx, 0);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save conversation");
            ErrorMessage = "Could not save conversation: " + ex.Message;
        }
    }

    private void _suppressSelection(ConversationItemViewModel item) => SelectedConversation = item;

    private bool CanStop() => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _generationCts?.Cancel();

    [RelayCommand]
    private void CopyMessage(MessageViewModel? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Content)) return;
        _ui.CopyToClipboard(message.Content);
    }

    [RelayCommand]
    private void OpenSettings() => _ui.ShowSettings();

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;
}
