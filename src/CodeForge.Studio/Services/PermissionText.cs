namespace CodeForge.Studio.Services;

/// <summary>Plain-language wording for permission prompts, kept separate from MessageBox so it can be tested.</summary>
public static class PermissionText
{
    public static string RunCommands(string folder, string command) =>
        "Allow CodeForge to run build and test commands in this folder?\n\n" +
        "Folder:\n" + folder + "\n\n" +
        "Command:\n" + command + "\n\n" +
        "⚠ Building or testing a project can run code that the project itself controls — for example build scripts, " +
        "MSBuild targets, package restore steps and the project's own test code. Choose Yes only if you trust this project.\n\n" +
        "If you approve, these fixed commands (dotnet build, dotnet test, flutter analyze, flutter test) may run in this folder " +
        "for as long as CodeForge stays open. CodeForge never runs anything else, and each run is started by you with a button.\n\n" +
        "Run commands in this folder?";
}
