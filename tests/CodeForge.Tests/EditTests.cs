using CodeForge.Agent;
using CodeForge.Application;
using CodeForge.Domain;
using CodeForge.Tools;
using Xunit;

namespace CodeForge.Tests;

public class DiffEngineTests
{
    [Fact]
    public void Identical_HasNoChanges()
    {
        var (_, a, r) = DiffEngine.Diff("a\nb\n", "a\nb\n");
        Assert.Equal(0, a); Assert.Equal(0, r);
    }

    [Fact]
    public void Modify_CountsAddedAndRemoved()
    {
        var (lines, a, r) = DiffEngine.Diff("a\nb\nc\n", "a\nB\nc\nd\n");
        Assert.Equal(2, a); Assert.Equal(1, r);
        Assert.Contains(lines, l => l.Kind == DiffKind.Removed && l.Text == "b");
        Assert.Contains(lines, l => l.Kind == DiffKind.Added && l.Text == "B");
    }

    [Fact]
    public void Create_AllAdded()
    {
        var (lines, a, r) = DiffEngine.Diff("", "x\ny\n");
        Assert.Equal(2, a); Assert.Equal(0, r);
        Assert.All(lines, l => Assert.Equal(DiffKind.Added, l.Kind));
    }

    [Fact]
    public void LongUnchangedRuns_AreCollapsed()
    {
        var old = string.Join("\n", Enumerable.Range(0, 100).Select(i => "line" + i));
        var (lines, a, r) = DiffEngine.Diff(old, old.Replace("line20", "CHANGED").Replace("line80", "CHANGED2"));
        Assert.Equal(2, a); Assert.Equal(2, r);
        Assert.Single(lines, l => l.Kind == DiffKind.Gap);
        Assert.True(lines.Count < 30);
    }
}

public class EditParserTests
{
    private const string Good = "Here you go:\nFILE: src/A.cs\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE\n";

    [Fact]
    public void ParsesSingleBlock()
    {
        var (blocks, problems) = EditParser.Parse(Good);
        Assert.Empty(problems);
        var b = Assert.Single(blocks);
        Assert.Equal("src/A.cs", b.Path); Assert.Equal("old", b.Search.Trim()); Assert.Equal("new", b.Replace.Trim());
    }

    [Fact]
    public void ParsesMultipleBlocksAndCleansPath()
    {
        var text = Good + "FILE: `src\\B.cs`\n<<<<<<< SEARCH\n=======\nx\n>>>>>>> REPLACE\n";
        var (blocks, _) = EditParser.Parse(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("src/B.cs", blocks[1].Path);
        Assert.Equal("", blocks[1].Search.Trim());
    }

    [Fact]
    public void IncompleteBlock_IsProblem()
    {
        var (blocks, problems) = EditParser.Parse("FILE: a.cs\n<<<<<<< SEARCH\nold\n=======\nnew\n");
        Assert.Empty(blocks); Assert.NotEmpty(problems);
    }

    [Fact]
    public void MissingPath_IsProblem()
    {
        var (blocks, problems) = EditParser.Parse("<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE\n");
        Assert.Empty(blocks); Assert.NotEmpty(problems);
    }

    [Fact]
    public void PlainText_YieldsNothing()
    {
        var (blocks, problems) = EditParser.Parse("Just an explanation.");
        Assert.Empty(blocks); Assert.Empty(problems);
    }
}

public class EditServiceTests : IAsyncLifetime
{
    private readonly TempProject _p = new();
    private readonly string _backup = Path.Combine(Path.GetTempPath(), "cf_bk_" + Guid.NewGuid().ToString("N"));
    private readonly ProjectWorkspace _w = new();
    private EditService _e = null!;

    public async Task InitializeAsync() { await _w.OpenAsync(_p.Root); _e = new EditService(_w, _backup); }
    public Task DisposeAsync()
    {
        _p.Dispose();
        if (Directory.Exists(_backup)) Directory.Delete(_backup, true);
        return Task.CompletedTask;
    }

    private static string Block(string path, string search, string replace) =>
        $"FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    private string Read(string rel) => File.ReadAllText(Path.Combine(_p.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
    private bool Exists(string rel) => File.Exists(Path.Combine(_p.Root, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task Prepare_Modify_BuildsDiffAndDoesNotWrite()
    {
        var before = Read("src/Core/Unrelated.cs");
        var r = await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "public class Banana { }", "public class Apple { }"));
        var c = Assert.Single(r.Changes);
        Assert.Equal(EditKind.Modify, c.Kind); Assert.Equal(1, c.Added); Assert.Equal(1, c.Removed);
        Assert.Equal(before, Read("src/Core/Unrelated.cs"));
    }

    [Fact]
    public async Task Prepare_Create_Works()
    {
        var r = await _e.PrepareAsync(Block("src/New/Thing.cs", "", "class Thing { }"));
        Assert.Equal(EditKind.Create, Assert.Single(r.Changes).Kind);
        Assert.False(Exists("src/New/Thing.cs"));
    }

    [Theory]
    [InlineData("not present anywhere")]
    public async Task Prepare_NotFound_IsProblem(string search)
    {
        var r = await _e.PrepareAsync(Block("src/Core/Unrelated.cs", search, "x"));
        Assert.Empty(r.Changes); Assert.Single(r.Problems);
    }

    [Fact]
    public async Task Prepare_Ambiguous_IsProblem()
    {
        _p.Write("dup.txt", "a\na\n");
        var r = await _e.PrepareAsync(Block("dup.txt", "a", "b"));
        Assert.Empty(r.Changes); Assert.Contains("more than once", r.Problems[0]);
    }

    [Fact]
    public async Task Prepare_NoOp_And_EmptySearchOnExisting_AreProblems()
    {
        var r1 = await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "class Banana", "class Banana"));
        Assert.Empty(r1.Changes); Assert.NotEmpty(r1.Problems);
        var r2 = await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "", "whole new content"));
        Assert.Empty(r2.Changes); Assert.NotEmpty(r2.Problems);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("secrets/server.pem")]
    [InlineData("bin/Debug/out.txt")]
    [InlineData("node_modules/x/index.js")]
    [InlineData("image.png")]
    [InlineData("data.txt")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("C:/Windows/win.ini")]
    public async Task Prepare_ProtectedOrEscapingPaths_AreRejected(string path)
    {
        var r = await _e.PrepareAsync(Block(path, "", "hacked"));
        Assert.Empty(r.Changes); Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public async Task Apply_WritesFile_CreatesBackup_AndUndoRestores()
    {
        var original = Read("src/Core/Unrelated.cs");
        var c = (await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "class Banana", "class Apple"))).Changes[0];
        await _e.ApplyAsync(c);
        Assert.Contains("class Apple", Read("src/Core/Unrelated.cs"));
        Assert.True(Directory.Exists(_backup));
        Assert.Single(Directory.GetFiles(_backup, "Unrelated.cs", SearchOption.AllDirectories));

        await _e.UndoAsync(c);
        Assert.Equal(original, Read("src/Core/Unrelated.cs"));
    }

    [Fact]
    public async Task Apply_Create_ThenUndoDeletes()
    {
        var c = (await _e.PrepareAsync(Block("src/New/Thing.cs", "", "class Thing { }"))).Changes[0];
        await _e.ApplyAsync(c);
        Assert.True(Exists("src/New/Thing.cs"));
        await _e.UndoAsync(c);
        Assert.False(Exists("src/New/Thing.cs"));
    }

    [Fact]
    public async Task Apply_PreservesCrlfAndBom()
    {
        var path = Path.Combine(_p.Root, "win.txt");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("one\r\ntwo\r\n")).ToArray());
        var c = (await _e.PrepareAsync(Block("win.txt", "two", "three"))).Changes[0];
        await _e.ApplyAsync(c);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("one\r\nthree\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Apply_StaleFile_IsRefused()
    {
        var c = (await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "class Banana", "class Apple"))).Changes[0];
        _p.Write("src/Core/Unrelated.cs", "changed behind our back\n");
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(c));
        Assert.Equal("changed behind our back\n", Read("src/Core/Unrelated.cs"));
    }

    [Fact]
    public async Task Apply_CreateOverExisting_IsRefused()
    {
        var c = (await _e.PrepareAsync(Block("fresh.txt", "", "x"))).Changes[0];
        _p.Write("fresh.txt", "someone else\n");
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(c));
        Assert.Equal("someone else\n", Read("fresh.txt"));
    }

    [Fact]
    public async Task Apply_ForgedChange_IsRefused()
    {
        var forged = new FileChange(Guid.NewGuid(), ".env", EditKind.Create, "", "x", new List<DiffLine>(), 1, 0, null);
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(forged));
    }

    [Fact]
    public async Task Apply_Twice_IsRefused()
    {
        var c = (await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "class Banana", "class Apple"))).Changes[0];
        await _e.ApplyAsync(c);
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(c));
    }

    [Fact]
    public async Task Undo_IsRefused_WhenFileModifiedAfterApply()
    {
        var c = (await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "class Banana", "class Apple"))).Changes[0];
        await _e.ApplyAsync(c);
        _p.Write("src/Core/Unrelated.cs", "newer work\n");
        await Assert.ThrowsAsync<ProjectException>(() => _e.UndoAsync(c));
        Assert.Equal("newer work\n", Read("src/Core/Unrelated.cs"));
    }

    [Fact]
    public async Task Apply_AfterProjectSwitch_IsRefused()
    {
        var c = (await _e.PrepareAsync(Block("src/Core/Unrelated.cs", "class Banana", "class Apple"))).Changes[0];
        using var other = new TempProject();
        await _w.OpenAsync(other.Root);
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(c));
    }

    [Fact]
    public void ChatAgent_PromptIncludesEditInstructions_OnlyWithContext()
    {
        var hist = new[] { new ChatMessage { Role = ChatRole.User, Content = "hi" } };
        Assert.Contains("SEARCH", ChatAgent.BuildPrompt("sys", hist, "ctx")[0].Content);
        Assert.DoesNotContain("SEARCH", ChatAgent.BuildPrompt("sys", hist, null)[0].Content);
    }
}
