using CodeForge.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CodeForge.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeForgeProjectTools(this IServiceCollection services)
    {
        services.AddSingleton<ProjectWorkspace>();
        services.AddSingleton<IProjectWorkspace>(sp => sp.GetRequiredService<ProjectWorkspace>());
        services.AddSingleton<IProjectContextProvider>(sp => sp.GetRequiredService<ProjectWorkspace>());
        return services;
    }
}
