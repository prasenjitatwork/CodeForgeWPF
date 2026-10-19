using CodeForge.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeForge.Studio.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;

    public SettingsViewModel(ISettingsStore store)
    {
        _store = store;
        var s = store.Current;
        _ollamaUrl = s.OllamaUrl;
        _model = s.Model;
        _timeoutSeconds = s.RequestTimeoutSeconds;
        _systemPrompt = s.SystemPrompt;
        _contextWindowTokens = s.ContextWindowTokens;
    }

    public event EventHandler? CloseRequested;

    [ObservableProperty] private int _contextWindowTokens;
    [ObservableProperty] private string _ollamaUrl;
    [ObservableProperty] private string _model;
    [ObservableProperty] private int _timeoutSeconds;
    [ObservableProperty] private string _systemPrompt;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!Uri.TryCreate(OllamaUrl?.Trim(), UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            Error = "Enter a valid http(s) URL, e.g. " + AppSettings.DefaultOllamaUrl;
            return;
        }
        if (string.IsNullOrWhiteSpace(Model)) { Error = "Model name is required."; return; }
        if (TimeoutSeconds < 5 || TimeoutSeconds > 3600) { Error = "Timeout must be between 5 and 3600 seconds."; return; }
        if (ContextWindowTokens < 2048 || ContextWindowTokens > 131072) { Error = "Context window must be between 2048 and 131072 tokens."; return; }
        try
        {
            var next = _store.Current.Clone();
            next.OllamaUrl = OllamaUrl.Trim();
            next.Model = Model.Trim();
            next.RequestTimeoutSeconds = TimeoutSeconds;
            next.SystemPrompt = SystemPrompt ?? string.Empty;
            next.ContextWindowTokens = ContextWindowTokens;
            await _store.SaveAsync(next);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Error = "Could not save settings: " + ex.Message; }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var d = new AppSettings();
        OllamaUrl = d.OllamaUrl; Model = d.Model; TimeoutSeconds = d.RequestTimeoutSeconds; SystemPrompt = d.SystemPrompt;
        ContextWindowTokens = d.ContextWindowTokens;
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
