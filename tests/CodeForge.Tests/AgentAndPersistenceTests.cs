using CodeForge.Agent;
using CodeForge.Application;
using CodeForge.Domain;
using CodeForge.Infrastructure.Persistence;
using CodeForge.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeForge.Tests;

public class ChatAgentTests
{
    [Fact]
    public void BuildPrompt_AddsSystemPromptAndMapsRoles()
    {
        var history = new[]
        {
            new ChatMessage { Role = ChatRole.User, Content = "q" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "a" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "" }
        };
        var p = ChatAgent.BuildPrompt("sys", history);
        Assert.Equal(new[] { "system", "user", "assistant" }, p.Select(m => m.Role));
    }

    [Fact]
    public void BuildPrompt_LimitsContext()
    {
        var history = Enumerable.Range(0, 100).Select(i => new ChatMessage { Role = ChatRole.User, Content = i.ToString() });
        var p = ChatAgent.BuildPrompt("", history);
        Assert.Equal(ChatAgent.MaxContextMessages, p.Count);
        Assert.Equal("99", p[^1].Content);
    }

    private sealed class FakeClient : IChatModelClient
    {
        public IReadOnlyList<ModelChatMessage>? Seen;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
        public async IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<ModelChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Seen = messages;
            yield return "x"; await Task.Yield(); yield return "y";
        }
    }

    [Fact]
    public async Task RespondAsync_StreamsFromClient()
    {
        var client = new FakeClient();
        var agent = new ChatAgent(client, new FakeSettings());
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi" });
        var got = new List<string>();
        await foreach (var c in agent.RespondAsync(conv, "m", default)) got.Add(c);
        Assert.Equal(new[] { "x", "y" }, got);
        Assert.Equal("hi", client.Seen![^1].Content);
    }
}

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cf-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Repository_SaveThenReopen_RoundTripsMessagesInOrder()
    {
        var path = Path.Combine(_dir, "c.db");
        var conv = new Conversation { Title = "Hello" };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "one ✓ \"quoted\"" });
        conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "two\nlines" });
        await new SqliteConversationRepository(path).SaveAsync(conv);

        var repo2 = new SqliteConversationRepository(path); // simulates app restart
        var list = await repo2.ListAsync();
        Assert.Single(list);
        var loaded = await repo2.GetAsync(conv.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Hello", loaded!.Title);
        Assert.Equal(new[] { "one ✓ \"quoted\"", "two\nlines" }, loaded.Messages.Select(m => m.Content));
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, loaded.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Repository_ResaveReplacesMessages_AndDeleteRemoves()
    {
        var repo = new SqliteConversationRepository(Path.Combine(_dir, "c.db"));
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "a" });
        await repo.SaveAsync(conv);
        conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "b" });
        await repo.SaveAsync(conv);
        Assert.Equal(2, (await repo.GetAsync(conv.Id))!.Messages.Count);
        await repo.DeleteAsync(conv.Id);
        Assert.Null(await repo.GetAsync(conv.Id));
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Settings_DefaultsWhenMissing_AndPersistAcrossInstances()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s1 = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
        await s1.LoadAsync();
        Assert.Equal(AppSettings.DefaultOllamaUrl, s1.Current.OllamaUrl);
        Assert.Equal(AppSettings.DefaultModel, s1.Current.Model);

        await s1.SaveAsync(new AppSettings { OllamaUrl = "http://host:1234", Model = "other:1b" });
        var s2 = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
        await s2.LoadAsync();
        Assert.Equal("http://host:1234", s2.Current.OllamaUrl);
        Assert.Equal("other:1b", s2.Current.Model);
    }

    [Fact]
    public async Task Settings_CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var s = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
        await s.LoadAsync();
        Assert.Equal(AppSettings.DefaultModel, s.Current.Model);
    }
}
