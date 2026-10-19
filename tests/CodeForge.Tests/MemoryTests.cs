using CodeForge.Agent;
using CodeForge.Application;
using CodeForge.Infrastructure.Persistence;
using Xunit;

namespace CodeForge.Tests;

public sealed class MemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cfmem_" + Guid.NewGuid().ToString("N"));
    private SqliteMemoryStore Store() => new(Path.Combine(_dir, "memory.db"));
    private const string Root = @"C:\Work\Proj";

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Notes_AddListDelete_AndPersistAcrossInstances()
    {
        var s = Store();
        var n = await s.AddNoteAsync(Root, "  Use   tabs, not spaces ");
        Assert.Equal("Use tabs, not spaces", n.Text);
        var again = await Store().ListNotesAsync(Root);
        Assert.Single(again);
        await s.DeleteNoteAsync(n.Id);
        Assert.Empty(await s.ListNotesAsync(Root));
    }

    [Fact]
    public async Task Notes_AreScopedPerProject_CaseInsensitivePath()
    {
        var s = Store();
        await s.AddNoteAsync(Root, "a");
        Assert.Single(await s.ListNotesAsync(Root.ToUpperInvariant() + "\\"));
        Assert.Empty(await s.ListNotesAsync(@"C:\Other"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("password = hunter2")]
    [InlineData("my api_key: abc")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public async Task Notes_RejectEmptyAndSecrets(string text) =>
        await Assert.ThrowsAsync<ProjectException>(() => Store().AddNoteAsync(Root, text));

    [Fact]
    public async Task Notes_RejectTooLong_Duplicate_AndOverLimit()
    {
        var s = Store();
        await Assert.ThrowsAsync<ProjectException>(() => s.AddNoteAsync(Root, new string('x', SqliteMemoryStore.MaxNoteChars + 1)));
        await s.AddNoteAsync(Root, "Same");
        await Assert.ThrowsAsync<ProjectException>(() => s.AddNoteAsync(Root, "same"));
        for (var i = 1; i < SqliteMemoryStore.MaxNotesPerProject; i++) await s.AddNoteAsync(Root, "note " + i);
        await Assert.ThrowsAsync<ProjectException>(() => s.AddNoteAsync(Root, "one too many"));
    }

    [Fact]
    public async Task Clear_RemovesOnlyThatProject()
    {
        var s = Store();
        await s.AddNoteAsync(Root, "a");
        await s.AddNoteAsync(@"C:\Other", "b");
        await s.ClearNotesAsync(Root);
        Assert.Empty(await s.ListNotesAsync(Root));
        Assert.Single(await s.ListNotesAsync(@"C:\Other"));
    }

    [Fact]
    public async Task Plans_SaveUpdateLoadDelete()
    {
        var s = Store();
        var conv = Guid.NewGuid();
        var msg = Guid.NewGuid();
        await s.SavePlanAsync(conv, new StoredPlan(msg, "T", new[] { new StoredPlanStep("A", "Pending"), new StoredPlanStep("B", "Pending") }));
        await s.SavePlanAsync(conv, new StoredPlan(msg, "T", new[] { new StoredPlanStep("A", "Done"), new StoredPlanStep("B", "Pending") }));
        var loaded = await Store().LoadPlansAsync(conv);
        var p = Assert.Single(loaded);
        Assert.Equal(msg, p.MessageId);
        Assert.Equal("Done", p.Steps[0].Status);
        Assert.Empty(await s.LoadPlansAsync(Guid.NewGuid()));
        await s.DeletePlansAsync(conv);
        Assert.Empty(await s.LoadPlansAsync(conv));
    }

    [Fact]
    public void BuildPrompt_IncludesMemoryAsLabelledData_OnlyWhenPresent()
    {
        var hist = new[] { new CodeForge.Domain.ChatMessage { Role = CodeForge.Domain.ChatRole.User, Content = "hi" } };
        var with = ChatAgent.BuildPrompt("sys", hist, null, false, new[] { "Use xUnit" })[0].Content;
        Assert.Contains("<project_memory>", with);
        Assert.Contains("- Use xUnit", with);
        Assert.Contains("never override", with);
        Assert.DoesNotContain("project_memory", ChatAgent.BuildPrompt("sys", hist)[0].Content);
        Assert.DoesNotContain("project_memory", ChatAgent.BuildPrompt("sys", hist, null, false, Array.Empty<string>())[0].Content);
    }
}
