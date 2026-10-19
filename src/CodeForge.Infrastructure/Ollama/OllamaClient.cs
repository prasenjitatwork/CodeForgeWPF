using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeForge.Application;
using Microsoft.Extensions.Logging;

namespace CodeForge.Infrastructure.Ollama;

internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")] public List<OllamaModelInfo>? Models { get; set; }
}

internal sealed class OllamaModelInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class OllamaChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; set; } = new();
    [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaOptions
{
    [JsonPropertyName("num_ctx")] public int NumCtx { get; set; }
}

internal sealed class OllamaMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

internal sealed class OllamaChatChunk
{
    [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class OllamaClient : IChatModelClient
{
    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly ILogger<OllamaClient> _log;

    public OllamaClient(HttpClient http, ISettingsStore settings, ILogger<OllamaClient> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    public static Uri BuildUri(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var b) ||
            (b.Scheme != Uri.UriSchemeHttp && b.Scheme != Uri.UriSchemeHttps))
            throw new ModelException(ModelErrorKind.Other, $"Invalid Ollama URL: '{baseUrl}'. Use e.g. {AppSettings.DefaultOllamaUrl}");
        return new Uri(b.ToString().TrimEnd('/') + path);
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(5, _settings.Current.RequestTimeoutSeconds));

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var uri = BuildUri(_settings.Current.OllamaUrl, "/api/tags");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var resp = await _http.GetAsync(uri, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new ModelException(ModelErrorKind.Unavailable, $"Ollama returned HTTP {(int)resp.StatusCode}.");
            var body = await resp.Content.ReadFromJsonAsync<OllamaTagsResponse>(cts.Token).ConfigureAwait(false);
            return (body?.Models ?? new()).Select(m => m.Name).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (ModelException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelException(ModelErrorKind.Timeout, "Timed out contacting Ollama.");
        }
        catch (JsonException ex)
        {
            throw new ModelException(ModelErrorKind.InvalidResponse, "Ollama returned an unreadable model list.", ex);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Ollama unreachable: {Message}", ex.Message);
            throw new ModelException(ModelErrorKind.Unavailable, $"Cannot reach Ollama at {_settings.Current.OllamaUrl}. Is it running?", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model, IReadOnlyList<ModelChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        var uri = BuildUri(_settings.Current.OllamaUrl, "/api/chat");
        var payload = new OllamaChatRequest
        {
            Model = model,
            Messages = messages.Select(m => new OllamaMessage { Role = m.Role, Content = m.Content }).            ToList(),
                        Options = new OllamaOptions { NumCtx = Math.Clamp(_settings.Current.ContextWindowTokens, 2048, 131072) }
                    };
                    using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(payload) };

        // Idle timeout: reset on every received line, so long generations are fine but stalls are not.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(Timeout);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelException(ModelErrorKind.Timeout, "Timed out waiting for the model to respond.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Ollama unreachable: {Message}", ex.Message);
            throw new ModelException(ModelErrorKind.Unavailable, $"Cannot reach Ollama at {_settings.Current.OllamaUrl}. Is it running?", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound || detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    throw new ModelException(ModelErrorKind.ModelNotFound,
                        $"Model '{model}' was not found. Install it with: ollama pull {model}");
                throw new ModelException(ModelErrorKind.Other, $"Ollama returned HTTP {(int)resp.StatusCode}: {detail}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (true)
            {
                string? line;
                try
                {
                    idle.CancelAfter(Timeout);
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ModelException(ModelErrorKind.Timeout, "The model stopped responding.");
                }
                catch (IOException ex)
                {
                    throw new ModelException(ModelErrorKind.Unavailable, "Connection to Ollama was lost.", ex);
                }
                if (line is null) yield break;
                if (line.Length == 0) continue;

                OllamaChatChunk? chunk;
                try { chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line); }
                catch (JsonException ex)
                {
                    throw new ModelException(ModelErrorKind.InvalidResponse, "Received a malformed response from Ollama.", ex);
                }
                if (chunk is null) continue;
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    var kind = chunk.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        ? ModelErrorKind.ModelNotFound : ModelErrorKind.Other;
                    throw new ModelException(kind, kind == ModelErrorKind.ModelNotFound
                        ? $"Model '{model}' was not found. Install it with: ollama pull {model}"
                        : $"Ollama error: {chunk.Error}");
                }
                if (!string.IsNullOrEmpty(chunk.Message?.Content))
                    yield return chunk.Message!.Content;
                if (chunk.Done) yield break;
            }
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
        catch { return string.Empty; }
    }
}
