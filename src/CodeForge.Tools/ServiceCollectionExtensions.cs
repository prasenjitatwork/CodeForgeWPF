using CodeForge.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CodeForge.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeForgeProjectTools(this IServiceCollection services, string dataDir)
    {
        services.AddSingleton<ProjectWorkspace>();
        services.AddSingleton<IProjectWorkspace>(sp => sp.GetRequiredService<ProjectWorkspace>());
        services.AddSingleton<IProjectContextProvider>(sp => sp.GetRequiredService<ProjectWorkspace>());
        services.AddSingleton<IBuildService, BuildService>();
        services.AddSingleton<IEditService>(sp =>
            new EditService(sp.GetRequiredService<ProjectWorkspace>(), Path.Combine(dataDir, "backups")));
        return services;
    }
}
