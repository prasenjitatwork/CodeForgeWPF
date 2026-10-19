using CodeForge.Application;
using CodeForge.Infrastructure.Ollama;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeForge.Tests;

/// <summary>Live tests against a local Ollama. They pass trivially (and report so) when Ollama is not running.</summary>
public class LiveOllamaTests
{
    private static async Task<bool> OllamaUpAsync(HttpClient http)
    {
        try { return (await http.GetAsync(AppSettings.DefaultOllamaUrl + "/api/tags")).IsSuccessStatusCode; }
        catch { return false; }
    }

    [Fact]
    public async Task LiveChat_StreamsResponseFromInstalledModel()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!await OllamaUpAsync(http)) return;
        var client = new OllamaClient(http, new FakeSettings { Current = new AppSettings { RequestTimeoutSeconds = 240 } }, NullLogger<OllamaClient>.Instance);
        var models = await client.ListModelsAsync(default);
        if (!models.Contains(AppSettings.DefaultModel)) return;

        var chunks = new List<string>();
        await foreach (var c in client.StreamChatAsync(AppSettings.DefaultModel,
            new[] { new ModelChatMessage("user", "Reply with the single word: pong") }, default))
            chunks.Add(c);
        Assert.NotEmpty(chunks);
        Assert.Contains("pong", string.Concat(chunks), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveChat_MissingModel_ThrowsModelNotFound()
    {
        using var http = new HttpClient();
        if (!await OllamaUpAsync(http)) return;
        var client = new OllamaClient(http, new FakeSettings(), NullLogger<OllamaClient>.Instance);
        var ex = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync("definitely-not-a-model:0b",
                new[] { new ModelChatMessage("user", "hi") }, default)) { }
        });
        Assert.Equal(ModelErrorKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task LiveChat_CancelMidStream_StopsPromptly()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!await OllamaUpAsync(http)) return;
        var client = new OllamaClient(http, new FakeSettings { Current = new AppSettings { RequestTimeoutSeconds = 240 } }, NullLogger<OllamaClient>.Instance);
        if (!(await client.ListModelsAsync(default)).Contains(AppSettings.DefaultModel)) return;

        using var cts = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(AppSettings.DefaultModel,
                new[] { new ModelChatMessage("user", "Write a very long story about a dragon.") }, cts.Token))
            {
                if (++count == 3) cts.Cancel();
            }
        });
        Assert.True(count >= 3);
    }
}
