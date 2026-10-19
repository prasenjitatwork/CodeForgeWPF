using System.Windows;
using CodeForge.Studio.ViewModels;
using CodeForge.Studio.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CodeForge.Studio.Services;

public interface IUiServices
{
    void CopyToClipboard(string text);
    void ShowSettings();
    string? PickFolder();
    string? PickProjectFile();
}

public sealed class UiServices : IUiServices
{
    private readonly IServiceProvider _sp;
    public UiServices(IServiceProvider sp) => _sp = sp;

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; ignore */ }
    }

    public string? PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select a project folder" };
        return dlg.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dlg.FolderName : null;
    }

    public string? PickProjectFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a solution, project or pubspec file",
            Filter = "Project files|*.sln;*.slnx;*.csproj;pubspec.yaml|All files|*.*"
        };
        return dlg.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public void ShowSettings()
    {
        var vm = _sp.GetRequiredService<SettingsViewModel>();
        var win = new SettingsWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        vm.CloseRequested += (_, _) => win.Close();
        win.ShowDialog();
    }
}
