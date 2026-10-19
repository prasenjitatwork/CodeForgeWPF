using CodeForge.Application;
using CodeForge.Infrastructure.Ollama;
using CodeForge.Infrastructure.Persistence;
using CodeForge.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeForge.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeForgeStudio");

    public static IServiceCollection AddCodeForgeInfrastructure(this IServiceCollection services, string? dataDir = null)
    {
        dataDir ??= DefaultDataDirectory;
        services.AddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(Path.Combine(dataDir, "settings.json"), sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton<IConversationRepository>(_ =>
            new SqliteConversationRepository(Path.Combine(dataDir, "conversations.db")));
        // Timeout is enforced per request (idle timeout) in OllamaClient, so streaming isn't cut off.
        services.AddHttpClient<IChatModelClient, OllamaClient>(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        return services;
    }
}
