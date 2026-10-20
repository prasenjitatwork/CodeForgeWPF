using CodeForge.Application;
using CodeForge.Infrastructure;
using CodeForge.Studio.Services;
using CodeForge.Studio.ViewModels;
using CodeForge.Tools;
using CodeForge.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeForge.Studio.Tests;

internal sealed class FakeUi : IUiServices
{
    public string? FolderToPick { get; set; }
    public bool AllowWrite { get; set; }
    public bool AllowRun { get; set; }
    public int WritePrompts { get; private set; }
    public int RunPrompts { get; private set; }
    public int OnboardingShown { get; private set; }
    public string? LastClipboard { get; private set; }

    public void CopyToClipboard(string text) => LastClipboard = text;
    public void ShowSettings() { }
    public string? PickFolder() => FolderToPick;
    public string? PickProjectFile() => null;
    public bool ConfirmWriteAccess(string folder) { WritePrompts++; return AllowWrite; }
    public bool ConfirmRunCommands(string folder, string exampleCommand) { RunPrompts++; return AllowRun; }
    public void ShowOnboarding() => OnboardingShown++;
}

internal sealed class FakeModelClient : IChatModelClient
{
    public List<string> Models { get; set; } = new() { AppSettings.DefaultModel };
    public ModelException? ListError { get; set; }
    public string Reply { get; set; } = "ok";
    public List<IReadOnlyList<ModelChatMessage>> Requests { get; } = new();

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        ListError is not null ? Task.FromException<IReadOnlyList<string>>(ListError) : Task.FromResult<IReadOnlyList<string>>(Models.ToList());

    public async IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<ModelChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(messages);
        await Task.Yield();
        yield return Reply;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cfs_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Root);

    public string Write(string rel, string content)
    {
        var p = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

/// <summary>Real view models wired to real local services in temp folders, with only Ollama and dialogs faked.</summary>
internal sealed class Harness : IDisposable
{
    private readonly ServiceProvider _sp;
    public TempDir Data { get; }
    public TempDir Project { get; }
    private readonly bool _shared;
    public FakeUi Ui { get; } = new();
    public FakeModelClient Client { get; } = new();
    public MainViewModel Main { get; }
    public ProjectViewModel ProjectVm => Main.Project;
    public ISettingsStore Settings { get; }

    public Harness(bool seedProject = true, Harness? restartOf = null)
    {
        if (restartOf is not null) { Data = restartOf.Data; Project = restartOf.Project; _shared = true; }
        else { Data = new(); Project = new(); }
        if (seedProject && restartOf is null)
        {
            Project.Write("App.sln", "Microsoft Visual Studio Solution File\n");
            Project.Write("README.md", "# Demo\nInvoices.\n");
            Project.Write("src/InvoiceCalculator.cs", "namespace Core;\npublic class InvoiceCalculator\n{\n    public decimal ComputeTotal(decimal net) => net * 1.2m;\n}\n");
            Project.Write("src/Unrelated.cs", "namespace Core;\npublic class Banana { }\n");
            Project.Write(".env", "API_KEY=super-secret\n");
        }
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddCodeForgeInfrastructure(Data.Root);
        sc.AddCodeForgeProjectTools(Data.Root);
        sc.AddSingleton<IChatModelClient>(Client);
        sc.AddSingleton<IUiServices>(Ui);
        sc.AddSingleton<ProjectViewModel>();
        sc.AddSingleton<ChatAgent>();
        sc.AddSingleton<MainViewModel>();
        sc.AddTransient<OnboardingViewModel>();
        _sp = sc.BuildServiceProvider();
        Settings = _sp.GetRequiredService<ISettingsStore>();
        Main = _sp.GetRequiredService<MainViewModel>();
    }

    public T Get<T>() where T : notnull => _sp.GetRequiredService<T>();

    public async Task SendAsync(string text, bool planMode = false)
    {
        Main.PlanMode = planMode;
        Main.InputText = text;
        await Main.SendCommand.ExecuteAsync(null);
    }

    public static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (_shared) return;
        Data.Dispose();
        Project.Dispose();
    }
}
