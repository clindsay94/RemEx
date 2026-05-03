using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.Models;
using Remex.Client.Services;
using Remex.Client.ViewModels;
using Remex.Client.Views;
using Remex.Client.Services.Security;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Client;

public partial class App : Application
{
    private TrayFlyoutWindow? _flyout;
    private NativeMenuItem? _themeToggleMenuItem;
    public static IServiceProvider Services { get; private set; } = null!;

    public static int? OverrideHostPort { get; set; }
    public static Action<IServiceCollection>? RegisterPlatformServices { get; set; }
    public static Func<Task>? StopEmbeddedHostAsync { get; set; }
    public static string? EmbeddedHostInstanceId { get; set; }

    /// <summary>
    /// Service provider of the in-process embedded host (desktop only). Used by the
    /// Avalonia UI to subscribe to host-side events (e.g. pairing PIN display).
    /// </summary>
    public static IServiceProvider? EmbeddedHostServices { get; set; }

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
        collection.AddSingleton<PinnedCertStore>();

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

        // Synchronously apply the saved theme before the window opens.
        // This prevents a dark-glass flash for SolarFlare (or any non-default) users.
        ApplyThemeBeforeWindowShown();

        _ = InitializeAppAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyThemeBeforeWindowShown()
    {
        try
        {
            var baseFolder = OperatingSystem.IsAndroid()
                ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var filePath = Path.Combine(baseFolder, "Remex", "dashboard_layout.json");

            if (!File.Exists(filePath)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!doc.RootElement.TryGetProperty("customization", out var customization)) return;
            if (!customization.TryGetProperty("baseTheme", out var baseThemeProp)) return;

            var themeId = baseThemeProp.GetString();
            if (string.IsNullOrWhiteSpace(themeId)) return;
            if (!Enum.TryParse<AppTheme>(themeId, ignoreCase: true, out var theme)) return;

            Services.GetRequiredService<ThemeService>().ApplyThemeSync(theme);
        }
        catch
        {
            // Silently fall back to the default dark theme already declared in App.axaml.
        }
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
                viewModel.Connection.HostAddress = $"wss://localhost:{OverrideHostPort.Value}{Remex.Core.RemexConstants.WebSocketPath}";
            }
            else if (profile != null && !string.IsNullOrWhiteSpace(profile.HostAddress))
            {
                viewModel.Connection.HostAddress = profile.HostAddress;
            }

            // Wire the embedded host's pairing service so the desktop UI can display
            // the PIN that the user's phone is asking for.
            if (EmbeddedHostServices?.GetService(typeof(Remex.Core.Services.Security.IPairingService))
                is Remex.Core.Services.Security.IPairingService pairingService)
            {
                viewModel.Connection.AttachEmbeddedPairingService(pairingService);
            }

            _ = viewModel.Connection.AutoConnectAsync();

            if (OperatingSystem.IsAndroid())
            {
                viewModel.Connection.TelemetryReceived += (t) => TriggerPlatformWidgetUpdate();
                viewModel.Connection.ProcessListReceived += (p) => TriggerPlatformWidgetUpdate();
            }

            // P8-G: keep tray icon tooltip in sync with live sensor readings
            viewModel.Connection.TelemetryReceived += telemetry =>
            {
                viewModel.UpdateTrayStatus(telemetry);
                UpdateTrayTooltip(viewModel.TrayStatusSummary);
            };

            // Update tooltip once on connect/disconnect state change
            viewModel.Connection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
                {
                    viewModel.UpdateTrayStatus(viewModel.Connection.Telemetry);
                    UpdateTrayTooltip(viewModel.TrayStatusSummary);
                }
            };

            // P8-H: seed the theme toggle label from the persisted theme
            UpdateThemeToggleLabel(profile?.Customization?.ThemeId);

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

    // ═══════════════ P8-G: Tray Tooltip ═══════════════

    private void UpdateTrayTooltip(string text)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons?.FirstOrDefault() is { } icon)
            icon.ToolTipText = text;
    }

    // ═══════════════ P8-H: Theme Toggle ═══════════════

    private void OnToggleTheme(object? sender, EventArgs e)
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        var layoutService = Services.GetRequiredService<DashboardLayoutService>();
        var currentThemeId = layoutService.CurrentProfile?.Customization.ThemeId ?? "BaseDarkGlass";

        bool isCurrentlyLight = string.Equals(currentThemeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);
        var newThemeId = isCurrentlyLight ? "BaseDarkGlass" : "SolarFlare";

        var currentCustomization = layoutService.CurrentProfile?.Customization ?? new Remex.Core.Models.CustomizationSettings();
        var newCustomization = currentCustomization with { ThemeId = newThemeId };
        themeService.ApplyCustomization(newCustomization);

        var profile = layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
        layoutService.RequestSave(profile with { Customization = newCustomization });

        // Sync the shell's Customization property so the UI reflects the change
        if (Services.GetService<ShellViewModel>() is { } shellVm)
            shellVm.Customization = newCustomization;

        UpdateThemeToggleLabel(newThemeId);
    }

    private void UpdateThemeToggleLabel(string? themeId)
    {
        _themeToggleMenuItem ??= FindThemeToggleMenuItem();
        if (_themeToggleMenuItem is null) return;

        bool isLight = string.Equals(themeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);
        _themeToggleMenuItem.Header = isLight ? "Switch to Dark Mode" : "Switch to Light Mode";
    }

    private NativeMenuItem? FindThemeToggleMenuItem()
    {
        var icons = TrayIcon.GetIcons(this);
        return icons?.FirstOrDefault()?.Menu?.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault(m => m.Header?.ToString()?.Contains("Mode") == true);
    }
}
