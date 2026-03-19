using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.Services;
using Remex.Client.ViewModels;
using Remex.Client.Views;
using Remex.Core.Services;

namespace Remex.Client;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    
    /// <summary>
    /// When set by the platform-specific entry point (e.g. Desktop Program.cs),
    /// overrides the client's default host address to the embedded host's actual port.
    /// This ensures the client connects to the in-process host even if a service
    /// is running on the default port.
    /// </summary>
    public static int? OverrideHostPort { get; set; }

    /// <summary>
    /// Extension point for platform-specific services (e.g. desktop-only icon extractors)
    /// </summary>
    public static Action<IServiceCollection>? RegisterPlatformServices { get; set; }

    /// <summary>
    /// When set by the platform host, allows the client to stop the embedded host
    /// (e.g. to free the port before installing the Windows Service).
    /// </summary>
    public static Func<Task>? StopEmbeddedHostAsync { get; set; }

    /// <summary>
    /// Unique instance ID for the embedded host in this process (if any).
    /// Set by the Desktop entry point so the remote desktop client can
    /// detect self-connections and prevent infinite mirror.
    /// </summary>
    public static string? EmbeddedHostInstanceId { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();

        // Register Core Services
        collection.AddSingleton<ILauncherStorageService, LauncherStorageService>();
        collection.AddSingleton<IIconExtractionService, IconExtractionService>();
        collection.AddSingleton<DashboardLayoutService>();
        collection.AddSingleton<ThemeService>();
        collection.AddSingleton<Remex.Client.Services.Network.MdnsDiscoveryService>();

        // Register ViewModels
        collection.AddSingleton<ConnectionViewModel>();
        collection.AddTransient<AppLauncherViewModel>();
        collection.AddTransient<AddProgramViewModel>();
        collection.AddTransient<TaskManagerViewModel>();
        collection.AddSingleton<ShellViewModel>();

        RegisterPlatformServices?.Invoke(collection);

        var configBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        CommandModeContext.ConfigureServices(collection, configBuilder.Build());

        Services = collection.BuildServiceProvider();

        CommandModeContext.StartListener(Services);

        var viewModel = Services.GetRequiredService<ShellViewModel>();

        // Load persisted host address so the client remembers what the user configured.
        var layoutService = Services.GetRequiredService<DashboardLayoutService>();
        _ = Task.Run(async () =>
        {
            var profile = await layoutService.LoadAsync().ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Desktop override takes priority over persisted address.
                if (OverrideHostPort.HasValue)
                {
                    var port = OverrideHostPort.Value;
                    viewModel.Connection.HostAddress =
                        $"ws://localhost:{port}{Remex.Core.RemexConstants.WebSocketPath}";
                }
                else if (!string.IsNullOrWhiteSpace(profile.HostAddress))
                {
                    viewModel.Connection.HostAddress = profile.HostAddress;
                }

                // Auto-connect to the host.
                _ = viewModel.Connection.AutoConnectAsync();
            });
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new ShellView
            {
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}