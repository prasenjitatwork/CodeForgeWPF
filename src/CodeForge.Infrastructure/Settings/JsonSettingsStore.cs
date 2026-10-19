using System.Text.Json;
using CodeForge.Application;
using Microsoft.Extensions.Logging;

namespace CodeForge.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _log;

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore> log)
    {
        _path = path;
        _log = log;
    }

    public AppSettings Current { get; private set; } = new();
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_path)) return;
            await using var fs = File.OpenRead(_path);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(fs, cancellationToken: ct) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read settings; using defaults.");
            Current = new();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(settings, Options), ct);
        File.Move(tmp, _path, overwrite: true);
        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
