using System.Net;
using System.Text;
using CodeForge.Application;
using CodeForge.Infrastructure.Ollama;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeForge.Tests;

internal sealed class FakeSettings : ISettingsStore
{
    public AppSettings Current { get; set; } = new() { RequestTimeoutSeconds = 5 };
    public event EventHandler? Changed { add { } remove { } }
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) { Current = settings; return Task.CompletedTask; }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
        return _respond(request);
    }
}

public class OllamaClientTests
{
    private static OllamaClient Create(StubHandler h, FakeSettings? s = null) =>
        new(new HttpClient(h), s ?? new FakeSettings(), NullLogger<OllamaClient>.Instance);

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> s)
    {
        var l = new List<string>();
        await foreach (var x in s) l.Add(x);
        return l;
    }

    [Fact]
    public async Task ListModels_ParsesAndSortsNames()
    {
        var h = new StubHandler(_ => Json("""{"models":[{"name":"b:1"},{"name":"a:2"}]}"""));
        var models = await Create(h).ListModelsAsync(default);
        Assert.Equal(new[] { "a:2", "b:1" }, models);
        Assert.Equal("http://localhost:11434/api/tags", h.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListModels_UsesConfiguredUrl()
    {
        var h = new StubHandler(_ => Json("""{"models":[]}"""));
        var s = new FakeSettings { Current = new AppSettings { OllamaUrl = "http://10.0.0.5:9999/" } };
        await Create(h, s).ListModelsAsync(default);
        Assert.Equal("http://10.0.0.5:9999/api/tags", h.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListModels_ConnectionFailure_ThrowsUnavailable()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("refused"));
        var ex = await Assert.ThrowsAsync<ModelException>(() => Create(h).ListModelsAsync(default));
        Assert.Equal(ModelErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task ListModels_MalformedJson_ThrowsInvalidResponse()
    {
        var h = new StubHandler(_ => Json("not json"));
        var ex = await Assert.ThrowsAsync<ModelException>(() => Create(h).ListModelsAsync(default));
        Assert.Equal(ModelErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task ListModels_InvalidUrl_Throws()
    {
        var s = new FakeSettings { Current = new AppSettings { OllamaUrl = "nonsense" } };
        await Assert.ThrowsAsync<ModelException>(() => Create(new StubHandler(_ => Json("{}")), s).ListModelsAsync(default));
    }

    [Fact]
    public async Task StreamChat_YieldsChunksInOrderAndSendsStreamingRequest()
    {
        var body = "{\"message\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"done\":false}\n" +
                   "{\"message\":{\"role\":\"assistant\",\"content\":\"lo\"},\"done\":false}\n" +
                   "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n";
        var h = new StubHandler(_ => Json(body));
        var chunks = await Collect(Create(h).StreamChatAsync("m", new[] { new ModelChatMessage("user", "hi") }, default));
        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        Assert.Contains("\"stream\":true", h.LastBody);
        Assert.Contains("\"model\":\"m\"", h.LastBody);
        Assert.Equal("/api/chat", h.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task StreamChat_404_ThrowsModelNotFound()
    {
        var h = new StubHandler(_ => Json("""{"error":"model 'x' not found"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<ModelException>(() => Collect(Create(h).StreamChatAsync("x", Array.Empty<ModelChatMessage>(), default)));
        Assert.Equal(ModelErrorKind.ModelNotFound, ex.Kind);
        Assert.Contains("ollama pull x", ex.Message);
    }

    [Fact]
    public async Task StreamChat_MalformedLine_ThrowsInvalidResponse()
    {
        var h = new StubHandler(_ => Json("{broken\n"));
        var ex = await Assert.ThrowsAsync<ModelException>(() => Collect(Create(h).StreamChatAsync("m", Array.Empty<ModelChatMessage>(), default)));
        Assert.Equal(ModelErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task StreamChat_ErrorLineInStream_Throws()
    {
        var h = new StubHandler(_ => Json("{\"error\":\"boom\"}\n"));
        var ex = await Assert.ThrowsAsync<ModelException>(() => Collect(Create(h).StreamChatAsync("m", Array.Empty<ModelChatMessage>(), default)));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task StreamChat_Unreachable_ThrowsUnavailable()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("refused"));
        var ex = await Assert.ThrowsAsync<ModelException>(() => Collect(Create(h).StreamChatAsync("m", Array.Empty<ModelChatMessage>(), default)));
        Assert.Equal(ModelErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task StreamChat_Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = new StubHandler(_ => Json("{}\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(Create(h).StreamChatAsync("m", Array.Empty<ModelChatMessage>(), cts.Token)));
    }
}
