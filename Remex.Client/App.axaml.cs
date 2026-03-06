using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Remex.Client.Services;
using Remex.Client.ViewModels;
using Remex.Client.Views;

namespace Remex.Client;

public partial class App : Application
{
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
        var layoutService = new DashboardLayoutService();
        var viewModel = new ShellViewModel(layoutService);

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
