using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
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

        // Register ViewModels
        collection.AddSingleton<ConnectionViewModel>();
        collection.AddTransient<AppLauncherViewModel>();
        collection.AddTransient<AddProgramViewModel>();
        collection.AddSingleton<ShellViewModel>();

        var configBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        CommandModeContext.ConfigureServices(collection, configBuilder.Build());

        Services = collection.BuildServiceProvider();

        CommandModeContext.StartListener(Services);

        var viewModel = Services.GetRequiredService<ShellViewModel>();

        // If the desktop entry point started an embedded host on a specific port,
        // override the connection address so the client connects to it.
        if (OverrideHostPort.HasValue)
        {
            var port = OverrideHostPort.Value;
            viewModel.Connection.HostAddress =
                $"ws://localhost:{port}{Remex.Core.RemexConstants.WebSocketPath}";
        }

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