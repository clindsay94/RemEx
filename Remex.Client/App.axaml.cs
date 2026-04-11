using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.Services;
using Remex.Client.ViewModels;
using Remex.Client.Views;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Client;

public partial class App : Application
{
    private TrayFlyoutWindow? _flyout;
    public static IServiceProvider Services { get; private set; } = null!;
    
    public static int? OverrideHostPort { get; set; }
    public static Action<IServiceCollection>? RegisterPlatformServices { get; set; }
    public static Func<Task>? StopEmbeddedHostAsync { get; set; }
    public static string? EmbeddedHostInstanceId { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var collection = new ServiceCollection();

        collection.AddSingleton<ILauncherStorageService, LauncherStorageService>();
        collection.AddSingleton<IIconExtractionService, IconExtractionService>();
        collection.AddSingleton<DashboardLayoutService>();
        collection.AddSingleton<ThemeService>();
        collection.AddSingleton<IMdnsDiscoveryService, MdnsDiscoveryService>();

        collection.AddSingleton<ConnectionViewModel>();
        collection.AddTransient<AppLauncherViewModel>();
        collection.AddTransient<AddProgramViewModel>();
        collection.AddTransient<TaskManagerViewModel>();
        collection.AddSingleton<HomeViewModel>();
        collection.AddSingleton<ShellViewModel>();

        RegisterPlatformServices?.Invoke(collection);

        var configBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        CommandModeContext.ConfigureServices(collection, configBuilder.Build());

        Services = collection.BuildServiceProvider();
        CommandModeContext.StartListener(Services);

        _ = InitializeAppAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAppAsync()
    {
        try
        {
            var layoutService = Services.GetRequiredService<DashboardLayoutService>();
            var profile = await layoutService.LoadAsync();

            if (profile != null && !string.IsNullOrWhiteSpace(profile.Language))
            {
                try
                {
                    var culture = new System.Globalization.CultureInfo(profile.Language);
                    Remex.Client.Localization.Strings.Culture = culture;
                    System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                }
                catch (System.Globalization.CultureNotFoundException)
                {
                    // Invalid language code in settings — ignore, use default culture
                }
            }

            var viewModel = Services.GetRequiredService<ShellViewModel>();
            viewModel.NavigateToHome();

            if (OverrideHostPort.HasValue)
            {
                viewModel.Connection.HostAddress = $"ws://localhost:{OverrideHostPort.Value}{Remex.Core.RemexConstants.WebSocketPath}";
            }
            else if (profile != null && !string.IsNullOrWhiteSpace(profile.HostAddress))
            {
                viewModel.Connection.HostAddress = profile.HostAddress;
            }

            _ = viewModel.Connection.AutoConnectAsync();

            if (OperatingSystem.IsAndroid())
            {
                viewModel.Connection.TelemetryReceived += (t) => TriggerPlatformWidgetUpdate();
                viewModel.Connection.ProcessListReceived += (p) => TriggerPlatformWidgetUpdate();
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    if (desktop.MainWindow == null)
                    {
                        desktop.MainWindow = new MainWindow { DataContext = viewModel };
                    }
                    desktop.MainWindow.Show();
                }
                else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
                {
                    singleViewPlatform.MainView = new ShellView { DataContext = viewModel };
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize app: {ex.Message}");
        }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ToggleLiveGlance();
    }

    private void OnToggleLiveGlance(object? sender, EventArgs e) => ToggleLiveGlance();

    private void ToggleLiveGlance()
    {
        var homeVm = Services.GetRequiredService<HomeViewModel>();
        homeVm.RefreshPinnedSensors();

        if (_flyout == null)
        {
            _flyout = new TrayFlyoutWindow
            {
                DataContext = homeVm
            };
        }

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
        }
        else
        {
            _flyout.ShowAtTray();
        }
    }

    private void OnShowMainWindow(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow == null)
            {
                var viewModel = Services.GetRequiredService<ShellViewModel>();
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
            }
            
            desktop.MainWindow.Show();
            desktop.MainWindow.Activate();
            desktop.MainWindow.WindowState = WindowState.Normal;
        }
    }

    private void OnExitApp(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _flyout?.Close();
            _flyout = null;

            if (Services.GetService<ShellViewModel>() is IDisposable disposableVm)
                disposableVm.Dispose();

            desktop.Shutdown();
        }
    }

    public static Action? RequestPlatformWidgetUpdate { get; set; }
    private void TriggerPlatformWidgetUpdate() => RequestPlatformWidgetUpdate?.Invoke();
}
