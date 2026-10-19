using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodeForge.Application;

namespace CodeForge.Tools;

public sealed class BuildService : IBuildService
{
    public const int MaxOutputChars = 200_000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly IProjectWorkspace _workspace;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _running = new(1, 1);

    public BuildService(IProjectWorkspace workspace) : this(workspace, DefaultTimeout) { }
    internal BuildService(IProjectWorkspace workspace, TimeSpan timeout) { _workspace = workspace; _timeout = timeout; }

    internal sealed record Plan(string FileName, string[] Args, string WorkingDirectory)
    {
        public string CommandLine => FileName.Replace(".bat", "") + " " + string.Join(' ', Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    // Only fixed commands; the single variable argument is the project's own entry file, made relative to the root.
    internal static Plan MakePlan(ProjectInfo info, BuildAction action)
    {
        var root = info.RootPath;
        switch (info.Kind)
        {
            case ProjectKind.Solution:
            case ProjectKind.CSharpProject:
                if (info.EntryFile is null) throw new ProjectException("No solution or project file was found.");
                var rel = Path.GetRelativePath(root, info.EntryFile);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                    throw new ProjectException("The project file is outside the project folder.");
                return new Plan("dotnet", new[] { action == BuildAction.Build ? "build" : "test", rel, "--nologo", "-nodeReuse:false" }, root);
            case ProjectKind.Flutter:
                return new Plan(OperatingSystem.IsWindows() ? "flutter.bat" : "flutter",
                    new[] { action == BuildAction.Build ? "analyze" : "test" }, root);
            default:
                throw new ProjectException("This folder has no .sln, .csproj or Flutter pubspec.yaml, so there is nothing to build or test.");
        }
    }

    public string Describe(BuildAction action)
    {
        var info = _workspace.Current ?? throw new ProjectException("No project is open.");
        return MakePlan(info, action).CommandLine;
    }

    public async Task<BuildResult> RunAsync(BuildAction action, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var info = _workspace.Current ?? throw new ProjectException("No project is open.");
        var plan = MakePlan(info, action);
        if (!await _running.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            throw new ProjectException("A build or test run is already in progress.");
        try
        {
            return await Task.Run(() => Execute(plan, action, info.RootPath, onOutput, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _running.Release(); }
    }

    private BuildResult Execute(Plan plan, BuildAction action, string root, Action<string>? onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(plan.FileName)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.Args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["TERM"] = "dumb";

        var sb = new StringBuilder();
        var truncated = false;
        var gate = new object();
        void Add(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                sb.AppendLine(line);
                if (sb.Length > MaxOutputChars) { sb.Remove(0, sb.Length - MaxOutputChars); truncated = true; }
            }
            try { onOutput?.Invoke(line); } catch { /* UI callback failures must not break the run */ }
        }

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.OutputDataReceived += (_, e) => Add(e.Data);
            proc.ErrorDataReceived += (_, e) => Add(e.Data);
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ProjectException($"Could not start '{plan.FileName.Replace(".bat", "")}'. Is it installed and on PATH? ({ex.Message})", ex);
        }
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var timedOut = false;
        var cancelled = false;
        var deadline = DateTime.UtcNow + _timeout;
        while (!proc.WaitForExit(200))
        {
            if (ct.IsCancellationRequested) { cancelled = true; Kill(proc); break; }
            if (DateTime.UtcNow > deadline) { timedOut = true; Kill(proc); break; }
        }
        proc.WaitForExit(); // flush async readers
        sw.Stop();

        string output;
        lock (gate) output = sb.ToString();
        var exit = timedOut || cancelled ? -1 : proc.ExitCode;
        return new BuildResult(action, plan.CommandLine, exit, timedOut, cancelled, output, truncated,
            BuildOutputParser.Parse(output, root), sw.Elapsed);
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
}
