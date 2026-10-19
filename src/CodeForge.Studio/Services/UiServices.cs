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
    bool ConfirmWriteAccess(string folder);
    bool ConfirmRunCommands(string folder, string exampleCommand);
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

    public bool ConfirmWriteAccess(string folder) =>
        MessageBox.Show(System.Windows.Application.Current.MainWindow,
            $"Allow CodeForge to create and modify files inside this folder?\n\n{folder}\n\n" +
            "Proposed edits will be applied automatically while this app is running. " +
            "Backups are kept and every change can be undone. Secret files and anything outside the folder stay blocked.",
            "Folder permission", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmRunCommands(string folder, string exampleCommand) =>
        MessageBox.Show(System.Windows.Application.Current.MainWindow,
            $"Allow CodeForge to run build and test commands in this folder?\n\n{folder}\n\nFirst command: {exampleCommand}\n\n" +
            "Building or testing runs the project's own code and build scripts on this computer. Only fixed commands " +
            "(dotnet build/test, flutter analyze/test) are ever run, and you start each one yourself.",
            "Run commands permission", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowSettings()
    {
        var vm = _sp.GetRequiredService<SettingsViewModel>();
        var win = new SettingsWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        vm.CloseRequested += (_, _) => win.Close();
        win.ShowDialog();
    }
}
