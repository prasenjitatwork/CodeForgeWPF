using System.Windows;
using CodeForge.Agent;
using CodeForge.Infrastructure;
using CodeForge.Tools;
using CodeForge.Studio.Services;
using CodeForge.Studio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeForge.Studio;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            _services?.GetService<ILogger<App>>()?.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show("An unexpected error occurred: " + args.Exception.Message, "CodeForge Studio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        var dataDir = Environment.GetEnvironmentVariable("CODEFORGE_DATA_DIR") ?? Infrastructure.ServiceCollectionExtensions.DefaultDataDirectory;
        sc.AddCodeForgeInfrastructure(dataDir);
        sc.AddCodeForgeProjectTools(dataDir);
        sc.AddSingleton<ProjectViewModel>();
        sc.AddSingleton<ChatAgent>();
        sc.AddSingleton<IUiServices, UiServices>();
        sc.AddSingleton<MainViewModel>();
        sc.AddTransient<SettingsViewModel>();
        sc.AddTransient<OnboardingViewModel>();
        _services = sc.BuildServiceProvider();

        var vm = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        await vm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
