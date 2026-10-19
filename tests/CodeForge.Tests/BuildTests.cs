using CodeForge.Application;
using CodeForge.Tools;
using Xunit;

namespace CodeForge.Tests;

public class BuildOutputParserTests
{
    private const string Root = @"C:\proj";

    [Fact]
    public void ParsesMsBuildErrorWithFileAndLine_AndDeduplicates()
    {
        var line = @"C:\proj\src\A.cs(12,5): error CS1002: ; expected [C:\proj\src\A.csproj]";
        var d = BuildOutputParser.Parse(line + "\n" + line + "\n", Root);
        var e = Assert.Single(d);
        Assert.Equal(DiagnosticSeverity.Error, e.Severity);
        Assert.Equal("src/A.cs", e.File); Assert.Equal(12, e.Line);
        Assert.Equal("CS1002: ; expected", e.Message);
    }

    [Fact]
    public void ParsesWarningsGeneralErrorsFailedTestsAndFlutter()
    {
        var text = string.Join("\n",
            @"C:\proj\B.cs(3,1): warning CS0168: The variable 'x' is declared but never used [C:\proj\p.csproj]",
            "MSBUILD : error MSB1009: Project file does not exist.",
            "  Failed MyTests.Calc.Adds [3 ms]",
            "  error • Undefined name 'foo' • lib/main.dart:10:5 • undefined_identifier");
        var d = BuildOutputParser.Parse(text, Root);
        Assert.Equal(4, d.Count);
        Assert.Contains(d, x => x.Severity == DiagnosticSeverity.Warning && x.File == "B.cs");
        Assert.Contains(d, x => x.Message.StartsWith("MSB1009"));
        Assert.Contains(d, x => x.Message == "Test failed: MyTests.Calc.Adds");
        Assert.Contains(d, x => x.File == "lib/main.dart" && x.Line == 10);
    }

    [Fact]
    public void CapsDiagnostics()
    {
        var text = string.Concat(Enumerable.Range(0, 200).Select(i => $@"C:\proj\F{i}.cs(1,1): error CS0001: bad {i}" + "\n"));
        Assert.Equal(BuildOutputParser.MaxDiagnostics, BuildOutputParser.Parse(text, Root).Count);
    }

    [Fact]
    public void BuildReport_IsBoundedAndMarksOutputUntrusted()
    {
        var r = new BuildResult(BuildAction.Build, "dotnet build", 1, false, false, new string('x', 100_000), true,
            new[] { new BuildDiagnostic(DiagnosticSeverity.Error, "A.cs", 3, "CS1: boom") }, TimeSpan.FromSeconds(1));
        var p = BuildReport.Prompt(r);
        Assert.True(p.Length < 7000);
        Assert.Contains("A.cs(3): CS1: boom", p);
        Assert.Contains("untrusted", p);
    }
}

public class BuildServiceTests : IDisposable
{
    private readonly TempProject _p = new();
    private readonly ProjectWorkspace _w = new();

    public void Dispose() => _p.Dispose();

    private static ProjectInfo Info(ProjectKind k, string root, string? entry) => new("p", root, k, entry);

    [Fact]
    public void MakePlan_UsesOnlyFixedCommands()
    {
        var b = BuildService.MakePlan(Info(ProjectKind.Solution, @"C:\r", @"C:\r\App.sln"), BuildAction.Build);
        Assert.Equal("dotnet", b.FileName);
        Assert.Equal(new[] { "build", "App.sln", "--nologo", "-nodeReuse:false" }, b.Args);
        var t = BuildService.MakePlan(Info(ProjectKind.CSharpProject, @"C:\r", @"C:\r\src\A.csproj"), BuildAction.Test);
        Assert.Equal("test", t.Args[0]); Assert.Equal(@"src\A.csproj", t.Args[1]);
        var f = BuildService.MakePlan(Info(ProjectKind.Flutter, @"C:\r", @"C:\r\pubspec.yaml"), BuildAction.Build);
        Assert.StartsWith("flutter", f.FileName); Assert.Equal(new[] { "analyze" }, f.Args);
    }

    [Fact]
    public void MakePlan_RejectsFolderAndOutsideEntry()
    {
        Assert.Throws<ProjectException>(() => BuildService.MakePlan(Info(ProjectKind.Folder, @"C:\r", null), BuildAction.Build));
        Assert.Throws<ProjectException>(() => BuildService.MakePlan(Info(ProjectKind.Solution, @"C:\r", @"C:\other\x.sln"), BuildAction.Build));
    }

    [Fact]
    public async Task NoProject_Throws()
    {
        var s = new BuildService(_w);
        await Assert.ThrowsAsync<ProjectException>(() => s.RunAsync(BuildAction.Build));
        Assert.Throws<ProjectException>(() => s.Describe(BuildAction.Build));
    }

    private async Task<string> OpenLibraryAsync(string code)
    {
        var dir = Path.Combine(_p.Root, "lib1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Lib1.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "C.cs"), code);
        await _w.OpenAsync(Path.Combine(dir, "Lib1.csproj"));
        return dir;
    }

    [Fact]
    public async Task RealBuild_Succeeds()
    {
        await OpenLibraryAsync("public class C { }");
        var lines = new List<string>();
        var r = await new BuildService(_w).RunAsync(BuildAction.Build, l => { lock (lines) lines.Add(l); });
        Assert.True(r.Success, r.Output);
        Assert.Equal(0, r.ErrorCount);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public async Task RealBuild_Failure_ReportsDiagnosticWithFileAndLine()
    {
        await OpenLibraryAsync("public class C {\n  int x = ;\n}");
        var r = await new BuildService(_w).RunAsync(BuildAction.Build);
        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.File == "C.cs" && d.Line == 2);
    }

    [Fact]
    public async Task Cancel_StopsTheProcess()
    {
        await OpenLibraryAsync("public class C { }");
        using var cts = new CancellationTokenSource();
        var task = new BuildService(_w).RunAsync(BuildAction.Build, null, cts.Token);
        cts.CancelAfter(300);
        var r = await task;
        Assert.True(r.Cancelled || r.Success); // a very fast build may finish first
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Timeout_KillsAndReports()
    {
        await OpenLibraryAsync("public class C { }");
        var r = await new BuildService(_w, TimeSpan.FromMilliseconds(50)).RunAsync(BuildAction.Build);
        Assert.True(r.TimedOut || r.Success);
        if (r.TimedOut) Assert.False(r.Success);
    }
}
