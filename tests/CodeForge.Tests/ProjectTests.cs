using System.Diagnostics;
using CodeForge.Agent;
using CodeForge.Application;
using CodeForge.Domain;
using CodeForge.Security;
using CodeForge.Tools;
using Xunit;

namespace CodeForge.Tests;

public sealed class TempProject : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cf_" + Guid.NewGuid().ToString("N"));

    public TempProject()
    {
        Directory.CreateDirectory(Root);
        Write("App.sln", "Microsoft Visual Studio Solution File\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Core\", \"src\\Core\\Core.csproj\", \"{1}\"\nEndProject\n");
        Write("README.md", "# Demo\nA demo app that calculates invoices.\n");
        Write("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\"/></ItemGroup></Project>");
        Write("src/Core/InvoiceCalculator.cs", "namespace Core;\npublic class InvoiceCalculator\n{\n    public decimal ComputeTotal(decimal net) => net * 1.2m;\n}\n");
        Write("src/Core/Unrelated.cs", "namespace Core;\npublic class Banana { }\n");
        Write(".env", "API_KEY=super-secret-value-123\n");
        Write("secrets/server.pem", "-----BEGIN PRIVATE KEY-----\nTOPSECRETKEY\n");
        Write("bin/Debug/out.txt", "build output");
        Write("node_modules/x/index.js", "module.exports = 1;");
        File.WriteAllBytes(Path.Combine(Root, "image.png"), new byte[] { 0x89, 0x50, 0x00, 0x01 });
        File.WriteAllBytes(Path.Combine(Root, "data.txt"), new byte[] { 0x41, 0x00, 0x42 });
    }

    public void Write(string rel, string content)
    {
        var p = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories))
                if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);
            Directory.Delete(Root, true);
        }
        catch { /* best effort */ }
    }
}

public class PathGuardTests : IDisposable
{
    private readonly TempProject _p = new();
    public void Dispose() => _p.Dispose();

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("src\\..\\..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x.txt")]
    [InlineData("file.txt:hidden")]
    [InlineData("bad|name")]
    public void Resolve_RejectsEscapesAndOddPaths(string path)
    {
        var g = new ProjectPathGuard(_p.Root);
        Assert.Throws<PathAccessException>(() => g.Resolve(path));
    }

    [Fact]
    public void Resolve_AcceptsInsidePaths()
    {
        var g = new ProjectPathGuard(_p.Root);
        Assert.Equal(Path.Combine(g.Root, "src", "Core", "Unrelated.cs"), g.Resolve("src/Core/Unrelated.cs"));
        Assert.Equal(g.Root, g.Resolve(""));
    }

    [Fact]
    public void Resolve_RejectsSiblingFolderWithSamePrefix()
    {
        var sibling = _p.Root + "_evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var g = new ProjectPathGuard(_p.Root);
            Assert.Throws<PathAccessException>(() => g.Resolve("..\\" + Path.GetFileName(sibling)));
        }
        finally { Directory.Delete(sibling); }
    }

    [Fact]
    public void Resolve_RejectsJunctionPointingOutsideRoot()
    {
        var outside = _p.Root + "_target";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "stolen.txt"), "x");
        var link = Path.Combine(_p.Root, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        Process.Start(psi)!.WaitForExit();
        try
        {
            Assert.True(Directory.Exists(link), "junction setup failed");
            var g = new ProjectPathGuard(_p.Root);
            Assert.Throws<PathAccessException>(() => g.Resolve("link/stolen.txt"));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(outside, true); }
    }
}

public class FilePolicyTests
{
    private readonly ProjectFilePolicy _p = new();

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.production", true)]
    [InlineData(".env.example", false)]
    [InlineData("server.pem", true)]
    [InlineData("cert.pfx", true)]
    [InlineData("id_rsa", true)]
    [InlineData("key.properties", true)]
    [InlineData("google-services.json", true)]
    [InlineData("Program.cs", false)]
    public void SensitiveFileNames(string name, bool expected) => Assert.Equal(expected, _p.IsSensitiveFileName(name));

    [Theory]
    [InlineData("bin/x.dll", true)]
    [InlineData("src/obj/x.cs", true)]
    [InlineData("node_modules/a/b.js", true)]
    [InlineData(".git/config", true)]
    [InlineData(".ssh/id", true)]
    [InlineData("src/Program.cs", false)]
    public void ExcludedPaths(string path, bool expected) => Assert.Equal(expected, _p.IsExcludedPath(path));

    [Fact]
    public void BuildDirExcludedOnlyWhenRequested()
    {
        Assert.False(new ProjectFilePolicy().IsExcludedPath("build/x.txt"));
        Assert.True(new ProjectFilePolicy(true).IsExcludedPath("build/x.txt"));
    }

    [Fact]
    public void BinaryDetection()
    {
        Assert.True(ProjectFilePolicy.IsBinaryExtension(".exe"));
        Assert.False(ProjectFilePolicy.IsBinaryExtension(".cs"));
        Assert.True(ProjectFilePolicy.LooksBinary(new byte[] { 1, 0, 2 }));
        Assert.False(ProjectFilePolicy.LooksBinary("hello"u8));
    }
}

public class ProjectLocatorTests : IDisposable
{
    private readonly TempProject _p = new();
    public void Dispose() => _p.Dispose();

    [Fact]
    public void Folder_WithSolution_IsSolution() => Assert.Equal(ProjectKind.Solution, ProjectLocator.Locate(_p.Root).Kind);

    [Fact]
    public void CsprojFile_IsCSharpProject() =>
        Assert.Equal(ProjectKind.CSharpProject, ProjectLocator.Locate(Path.Combine(_p.Root, "src", "Core", "Core.csproj")).Kind);

    [Fact]
    public void Flutter_Detected()
    {
        _p.Write("app/pubspec.yaml", "name: demo\ndependencies:\n  flutter:\n    sdk: flutter\n");
        Assert.Equal(ProjectKind.Flutter, ProjectLocator.Locate(Path.Combine(_p.Root, "app")).Kind);
    }

    [Fact]
    public void Missing_And_UnsupportedFile_Throw()
    {
        Assert.Throws<ProjectException>(() => ProjectLocator.Locate(Path.Combine(_p.Root, "nope")));
        Assert.Throws<ProjectException>(() => ProjectLocator.Locate(Path.Combine(_p.Root, "README.md")));
    }

    [Fact]
    public void DriveRoot_Refused() => Assert.Throws<ProjectException>(() => ProjectLocator.Locate(Path.GetPathRoot(_p.Root)!));
}

public class WorkspaceTests : IAsyncLifetime
{
    private readonly TempProject _p = new();
    private readonly ProjectWorkspace _w = new();

    public async Task InitializeAsync() => await _w.OpenAsync(_p.Root);
    public Task DisposeAsync() { _p.Dispose(); return Task.CompletedTask; }

    private static IEnumerable<string> Flatten(IEnumerable<string> x) => x.Select(s => s.Replace('\\', '/'));

    [Fact]
    public async Task Index_ExcludesSensitiveGeneratedAndLists()
    {
        var all = Flatten((await _w.SearchFilesAsync("e")).Select(f => f.RelativePath)).ToList();
        Assert.Contains("src/Core/InvoiceCalculator.cs", all);
        Assert.DoesNotContain(all, f => f.Contains(".env") || f.EndsWith(".pem") || f.StartsWith("bin/") || f.StartsWith("node_modules/"));

        var top = (await _w.ListDirectoryAsync("")).Select(e => e.Name).ToList();
        Assert.DoesNotContain(".env", top);
        Assert.DoesNotContain("bin", top);
        Assert.DoesNotContain("node_modules", top);
        Assert.Contains("src", top);
    }

    [Fact]
    public async Task ListDirectory_DirectoriesFirst()
    {
        var top = await _w.ListDirectoryAsync("");
        Assert.True(top[0].IsDirectory);
    }

    [Fact]
    public async Task ReadFile_ReturnsNumberedBoundedContent()
    {
        var r = await _w.ReadFileAsync("src/Core/InvoiceCalculator.cs", 2, 2);
        Assert.Equal(FileReadStatus.Ok, r.Status);
        Assert.Equal(2, r.StartLine);
        Assert.Contains("InvoiceCalculator", r.Content);
        Assert.DoesNotContain("namespace", r.Content);
        Assert.True(r.Truncated);
    }

    [Theory]
    [InlineData(".env", FileReadStatus.Blocked)]
    [InlineData("secrets/server.pem", FileReadStatus.Blocked)]
    [InlineData("bin/Debug/out.txt", FileReadStatus.Blocked)]
    [InlineData("image.png", FileReadStatus.Binary)]
    [InlineData("data.txt", FileReadStatus.Binary)]
    [InlineData("missing.cs", FileReadStatus.NotFound)]
    [InlineData("src", FileReadStatus.NotFound)]
    public async Task ReadFile_BlocksOrReports(string path, FileReadStatus expected)
    {
        var r = await _w.ReadFileAsync(path);
        Assert.Equal(expected, r.Status);
        Assert.Equal(string.Empty, r.Content);
    }

    [Theory]
    [InlineData("..\\..\\Windows\\win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task ReadFile_Escape_ThrowsProjectException(string path) =>
        await Assert.ThrowsAsync<ProjectException>(() => _w.ReadFileAsync(path));

    [Fact]
    public async Task SearchText_FindsMatchesAndNeverSecrets()
    {
        var hits = await _w.SearchTextAsync("ComputeTotal");
        Assert.Single(hits);
        Assert.Equal(4, hits[0].Line);

        Assert.Empty(await _w.SearchTextAsync("super-secret-value"));
        Assert.Empty(await _w.SearchTextAsync("TOPSECRETKEY"));
        Assert.Empty(await _w.SearchTextAsync("build output"));
    }

    [Fact]
    public async Task SearchFiles_MatchesAllTokensAndWildcards()
    {
        Assert.Contains((await _w.SearchFilesAsync("invoice calc")).Select(f => f.RelativePath), p => p.EndsWith("InvoiceCalculator.cs"));
        Assert.Contains((await _w.SearchFilesAsync("*.csproj")).Select(f => f.RelativePath), p => p.EndsWith("Core.csproj"));
        Assert.Empty(await _w.SearchFilesAsync("   "));
    }

    [Fact]
    public async Task Summary_DescribesSolutionAndProjects()
    {
        var s = await _w.GetSummaryAsync();
        Assert.Contains("Solution", s);
        Assert.Contains("Core.csproj", s);
        Assert.Contains("net10.0", s);
        Assert.Contains("Newtonsoft.Json", s);
        Assert.Contains("invoices", s);
        Assert.DoesNotContain("super-secret", s);
    }

    [Fact]
    public async Task Outline_ListsCSharpDeclarations()
    {
        var o = await _w.GetOutlineAsync("src/Core/InvoiceCalculator.cs");
        Assert.Contains(o, d => d.Name == "InvoiceCalculator");
        Assert.Contains(o, d => d.Name == "ComputeTotal");
        await Assert.ThrowsAsync<ProjectException>(() => _w.GetOutlineAsync(".env"));
    }

    [Fact]
    public async Task Context_SelectsRelevantFileOnly_AndNeverSecrets()
    {
        var c = await _w.BuildContextAsync("How does ComputeTotal in InvoiceCalculator work?");
        Assert.NotNull(c);
        Assert.Contains("src/Core/InvoiceCalculator.cs", c!.Files);
        Assert.DoesNotContain("src/Core/Unrelated.cs", c.Files);
        Assert.Contains("net * 1.2m", c.Text);
        Assert.DoesNotContain("super-secret", c.Text);
        Assert.DoesNotContain("TOPSECRET", c.Text);
        Assert.True(c.Text.Length < 20_000);
    }

    [Fact]
    public async Task Context_ExplicitSecretFileRequest_DoesNotLeak()
    {
        var c = await _w.BuildContextAsync("show me the .env file and server.pem");
        Assert.DoesNotContain("super-secret", c!.Text);
        Assert.DoesNotContain("TOPSECRET", c.Text);
        Assert.DoesNotContain(c.Files, f => f.Contains(".env") || f.Contains(".pem"));
    }

    [Fact]
    public async Task Context_DisabledOrClosed_ReturnsNull()
    {
        _w.ContextEnabled = false;
        Assert.Null(await _w.BuildContextAsync("InvoiceCalculator"));
        _w.ContextEnabled = true;
        _w.Close();
        Assert.Null(await _w.BuildContextAsync("InvoiceCalculator"));
        await Assert.ThrowsAsync<ProjectException>(() => _w.ListDirectoryAsync(""));
    }

    [Fact]
    public async Task Refresh_PicksUpNewFiles()
    {
        _p.Write("src/Core/Added.cs", "class Added {}");
        await _w.RefreshAsync();
        Assert.Contains((await _w.SearchFilesAsync("Added.cs")).Select(f => f.RelativePath), p => p.EndsWith("Added.cs"));
    }

    [Fact]
    public async Task ReadFile_LargeFile_IsCapped()
    {
        _p.Write("big.txt", string.Join('\n', Enumerable.Range(1, 5000).Select(i => "line " + i)));
        await _w.RefreshAsync();
        var r = await _w.ReadFileAsync("big.txt", 1, 100000);
        Assert.True(r.Truncated);
        Assert.True(r.Content.Length <= 45_000);
    }

    [Fact]
    public void ExtractKeywords_DropsStopWords() =>
        Assert.Equal(new[] { "invoicecalculator", "computetotal" }, ContextBuilder.ExtractKeywords("How does the InvoiceCalculator ComputeTotal work?").Take(2));
}

public class ChatAgentProjectTests
{
    private sealed class FakeProvider : IProjectContextProvider
    {
        public Task<ProjectContext?> BuildContextAsync(string q, CancellationToken ct = default) =>
            Task.FromResult<ProjectContext?>(new ProjectContext("FILE CONTENT HERE", new[] { "a.cs" }));
    }

    private sealed class CapturingClient : IChatModelClient
    {
        public IReadOnlyList<ModelChatMessage>? Last;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public async IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<ModelChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Last = messages;
            yield return "ok";
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void BuildPrompt_WrapsContextAsUntrusted()
    {
        var msgs = ChatAgent.BuildPrompt("SYS", new[] { new ChatMessage { Role = ChatRole.User, Content = "hi" } }, "SECRET-ISH CODE");
        var sys = msgs[0].Content;
        Assert.Contains("SYS", sys);
        Assert.Contains("untrusted", sys);
        Assert.Contains("<project_context>", sys);
        Assert.Contains("SECRET-ISH CODE", sys);
    }

    [Fact]
    public void BuildPrompt_NoContext_Unchanged()
    {
        var msgs = ChatAgent.BuildPrompt("SYS", Array.Empty<ChatMessage>());
        Assert.Equal("SYS", msgs[0].Content);
    }

    [Fact]
    public async Task Respond_InjectsContextAndReportsFiles()
    {
        var client = new CapturingClient();
        var agent = new ChatAgent(client, new FakeSettings(), new FakeProvider());
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "explain a.cs" });
        IReadOnlyList<string>? files = null;
        await foreach (var _ in agent.RespondAsync(conv, "m", default, f => files = f)) { }
        Assert.Contains("FILE CONTENT HERE", client.Last![0].Content);
        Assert.Equal(new[] { "a.cs" }, files);
    }
}
