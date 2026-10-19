using System.Collections.ObjectModel;
using System.Text;
using CodeForge.Application;
using CodeForge.Studio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeForge.Studio.ViewModels;

public sealed partial class TreeNodeViewModel : ObservableObject
{
    private static readonly TreeNodeViewModel Placeholder = new("…", "", false, 0);
    private readonly Func<TreeNodeViewModel, Task>? _load;

    public string Name { get; }
    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
    public string Icon => IsDirectory ? "📁" : "📄";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public TreeNodeViewModel(string name, string rel, bool isDir, long size, Func<TreeNodeViewModel, Task>? load = null)
    {
        Name = name; RelativePath = rel; IsDirectory = isDir; Size = size; _load = load;
        if (isDir && load is not null) Children.Add(Placeholder);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && _load is not null && Children.Count == 1 && ReferenceEquals(Children[0], Placeholder))
            _ = _load(this);
    }
}

public sealed record SearchResultItem(string RelativePath, int Line, string Display, string? Snippet)
{
    public bool HasSnippet => !string.IsNullOrEmpty(Snippet);
}

public sealed partial class ProjectViewModel : ObservableObject
{
    private readonly IProjectWorkspace _workspace;
    private readonly ISettingsStore _settings;
    private readonly IUiServices _ui;
    private readonly ILogger<ProjectViewModel> _log;
    private CancellationTokenSource? _searchCts;
    private string? _previewPath;

    public ProjectViewModel(IProjectWorkspace workspace, ISettingsStore settings, IUiServices ui, ILogger<ProjectViewModel> log)
    {
        _workspace = workspace; _settings = settings; _ui = ui; _log = log;
    }

    public event EventHandler<string>? PromptRequested;

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = new();
    public ObservableCollection<SearchResultItem> Results { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private ProjectInfo? _info;

    [ObservableProperty] private string _infoText = "No project open";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _resultsHeader = string.Empty;
    [ObservableProperty] private string _previewTitle = "Select a file to preview";
    [ObservableProperty] private string _previewText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _contextEnabled = true;

    public bool HasProject => Info is not null;
    public string Title => Info is null ? "No project" : $"{Info.Name} ({Info.Kind})";

    partial void OnContextEnabledChanged(bool value) => _workspace.ContextEnabled = value;

    partial void OnSearchTextChanged(string value) => _ = SearchAsync();

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var p = _ui.PickFolder();
        if (p is not null) await OpenAsync(p, persist: true);
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var p = _ui.PickProjectFile();
        if (p is not null) await OpenAsync(p, persist: true);
    }

    public async Task OpenAsync(string path, bool persist)
    {
        IsBusy = true;
        Error = null;
        try
        {
            var info = await _workspace.OpenAsync(path);
            Info = info;
            _workspace.ContextEnabled = ContextEnabled;
            await ReloadTreeAsync();
            ClearPreview();
            if (persist) await SaveLastPathAsync(path);
        }
        catch (ProjectException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open project");
            Error = "Could not open project: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task RestoreLastProjectAsync()
    {
        var last = _settings.Current.LastProjectPath;
        if (string.IsNullOrWhiteSpace(last) || !(System.IO.Directory.Exists(last) || System.IO.File.Exists(last))) return;
        await OpenAsync(last, persist: false);
    }

    private async Task SaveLastPathAsync(string? path)
    {
        try
        {
            var s = _settings.Current.Clone();
            s.LastProjectPath = path;
            await _settings.SaveAsync(s);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not remember last project"); }
    }

    private async Task ReloadTreeAsync()
    {
        Roots.Clear();
        if (_workspace.Current is null)
        {
            InfoText = "No project open";
            return;
        }
        InfoText = $"{_workspace.IndexedFileCount} files{(_workspace.IndexTruncated ? " (truncated)" : "")} · {_workspace.Current.RootPath}";
        foreach (var e in await _workspace.ListDirectoryAsync(""))
            Roots.Add(ToNode(e));
    }

    private TreeNodeViewModel ToNode(ProjectEntry e) =>
        new(e.Name, e.RelativePath, e.IsDirectory, e.Size, e.IsDirectory ? LoadChildrenAsync : null);

    private async Task LoadChildrenAsync(TreeNodeViewModel node)
    {
        try
        {
            var entries = await _workspace.ListDirectoryAsync(node.RelativePath);
            node.Children.Clear();
            foreach (var e in entries) node.Children.Add(ToNode(e));
        }
        catch (ProjectException ex)
        {
            node.Children.Clear();
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!HasProject) return;
        IsBusy = true;
        try
        {
            await _workspace.RefreshAsync();
            await ReloadTreeAsync();
        }
        catch (ProjectException ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CloseProjectAsync()
    {
        _workspace.Close();
        Info = null;
        Roots.Clear();
        Results.Clear();
        HasResults = false;
        SearchText = string.Empty;
        InfoText = "No project open";
        ClearPreview();
        await SaveLastPathAsync(null);
    }

    private void ClearPreview()
    {
        _previewPath = null;
        PreviewTitle = "Select a file to preview";
        PreviewText = string.Empty;
    }

    [RelayCommand]
    private void ExplainProject()
    {
        if (!HasProject) return;
        PromptRequested?.Invoke(this,
            "Explain this project: what it does, its structure, main technologies, and where I should start reading the code.");
    }

    [RelayCommand]
    public async Task PreviewAsync(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        Error = null;
        try
        {
            var r = await _workspace.ReadFileAsync(relativePath, 1, 400);
            _previewPath = r.RelativePath;
            PreviewTitle = r.RelativePath;
            PreviewText = r.Status == FileReadStatus.Ok
                ? r.Content + (r.Truncated ? $"\n\n… showing lines {r.StartLine}-{r.EndLine}; file continues" : "")
                : $"[{r.Status}] {r.Message}";
        }
        catch (ProjectException ex) { Error = ex.Message; }
    }

    public async Task SelectNodeAsync(TreeNodeViewModel? node)
    {
        if (node is { IsDirectory: false }) await PreviewAsync(node.RelativePath);
    }

    [RelayCommand]
    private async Task ShowOutlineAsync()
    {
        if (_previewPath is null) return;
        try
        {
            var decls = await _workspace.GetOutlineAsync(_previewPath);
            var sb = new StringBuilder($"Outline of {_previewPath}\n\n");
            if (decls.Count == 0) sb.Append("(no declarations recognised; outline supports .cs and .dart)");
            foreach (var d in decls) sb.AppendLine($"{d.Line,5}  {d.Kind,-10} {d.Name}");
            PreviewText = sb.ToString();
        }
        catch (ProjectException ex) { Error = ex.Message; }
    }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var q = SearchText.Trim();
        if (!HasProject || q.Length < 2)
        {
            Results.Clear();
            HasResults = false;
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var files = await _workspace.SearchFilesAsync(q, cts.Token);
            var text = await _workspace.SearchTextAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            Results.Clear();
            foreach (var f in files.Take(30)) Results.Add(new SearchResultItem(f.RelativePath, 0, "📄 " + f.RelativePath, null));
            foreach (var t in text) Results.Add(new SearchResultItem(t.RelativePath, t.Line, $"{t.RelativePath}:{t.Line}", t.Snippet));
            HasResults = true;
            ResultsHeader = $"{Math.Min(files.Count, 30)} file name match(es), {text.Count} text match(es)";
        }
        catch (OperationCanceledException) { }
        catch (ProjectException ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void DismissError() => Error = null;
}
