using CodeForge.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeForge.Studio.ViewModels;

public sealed partial class OnboardingViewModel : ObservableObject
{
    public const string DownloadUrl = "https://ollama.com/download";

    private readonly IChatModelClient _client;
    private readonly ISettingsStore _settings;

    [ObservableProperty] private string _statusText = "Not checked yet.";
    [ObservableProperty] private string? _hintText;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isOk;

    public event EventHandler? CloseRequested;
    public event EventHandler<ConnectionReport>? Checked;

    public OnboardingViewModel(IChatModelClient client, ISettingsStore settings)
    {
        _client = client;
        _settings = settings;
    }

    public string PullCommand => $"ollama pull {SelectedModel}";
    public string SelectedModel => string.IsNullOrWhiteSpace(_settings.Current.Model) ? AppSettings.DefaultModel : _settings.Current.Model;
    public string Url => _settings.Current.OllamaUrl;

    [RelayCommand]
    public async Task CheckAsync()
    {
        IsChecking = true;
        StatusText = "Checking…";
        HintText = null;
        try
        {
            var report = await ConnectionDiagnostics.CheckAsync(_client, SelectedModel, CancellationToken.None);
            StatusText = report.Summary;
            HintText = report.Hint;
            IsOk = report.IsOk;
            Checked?.Invoke(this, report);
        }
        finally { IsChecking = false; }
    }

    [RelayCommand]
    public async Task FinishAsync()
    {
        if (!_settings.Current.OnboardingCompleted)
        {
            var s = _settings.Current.Clone();
            s.OnboardingCompleted = true;
            await _settings.SaveAsync(s);
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
