namespace CodeForge.Application;

public enum ConnectionStatus { Ok, OllamaUnavailable, ModelMissing, NoModels, Timeout, Error }

public sealed record ConnectionReport(
    ConnectionStatus Status,
    string Summary,
    string? Hint,
    IReadOnlyList<string> InstalledModels)
{
    public bool IsOk => Status == ConnectionStatus.Ok;
}

/// <summary>Checks the local Ollama server and the selected model. Only talks to the configured (local) Ollama URL.</summary>
public static class ConnectionDiagnostics
{
    public static async Task<ConnectionReport> CheckAsync(IChatModelClient client, string selectedModel, CancellationToken ct)
    {
        try
        {
            var models = await client.ListModelsAsync(ct);
            return Evaluate(models, selectedModel);
        }
        catch (ModelException ex)
        {
            return FromError(ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ConnectionReport(ConnectionStatus.Timeout, "Ollama did not respond in time.",
                "Ollama may still be starting or busy loading a model. Wait a moment and check again, or raise the timeout in Settings.", []);
        }
        catch (Exception ex)
        {
            return new ConnectionReport(ConnectionStatus.Error, "Unexpected error: " + ex.Message, null, []);
        }
    }

    public static ConnectionReport Evaluate(IReadOnlyList<string> models, string selectedModel)
    {
        if (models.Count == 0)
            return new ConnectionReport(ConnectionStatus.NoModels, "Ollama is running, but no models are installed.",
                $"Download one with:  ollama pull {AppSettings.DefaultModel}", models);
        if (!models.Contains(selectedModel))
            return new ConnectionReport(ConnectionStatus.ModelMissing,
                $"Ollama is running, but the selected model '{selectedModel}' is not installed.",
                $"Download it with:  ollama pull {selectedModel}   — or choose an installed model ({string.Join(", ", models)}).", models);
        return new ConnectionReport(ConnectionStatus.Ok, $"Connected — model '{selectedModel}' is ready ({models.Count} installed).", null, models);
    }

    private static ConnectionReport FromError(ModelException ex) => ex.Kind switch
    {
        ModelErrorKind.Unavailable => new ConnectionReport(ConnectionStatus.OllamaUnavailable,
            "Cannot reach Ollama.",
            "Install Ollama from https://ollama.com/download, start it (it runs in the system tray), then check again. " +
            "If it runs on another port, update the URL in Settings.", []),
        ModelErrorKind.Timeout => new ConnectionReport(ConnectionStatus.Timeout, "Ollama did not respond in time.",
            "Ollama may still be starting or busy loading a model. Wait a moment and check again, or raise the timeout in Settings.", []),
        ModelErrorKind.ModelNotFound => new ConnectionReport(ConnectionStatus.ModelMissing, ex.Message,
            "Download the model with:  ollama pull <model>", []),
        _ => new ConnectionReport(ConnectionStatus.Error, ex.Message, null, [])
    };
}
