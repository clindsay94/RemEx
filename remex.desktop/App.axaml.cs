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
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;
using Remex.Desktop.Services.Security;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Desktop;

public partial class App : Application
{
    private TrayFlyoutWindow? _flyout;
    private NativeMenuItem? _themeToggleMenuItem;
    public static IServiceProvider Services { get; private set; } = null!;
    public static bool IsShuttingDown { get; set; }

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
        collection.AddSingleton<RemexSavefileService>(sp =>
        {
            // Host-side dashboard storage resolves the same ProgramData path regardless of
            // whether it happens to be registered in this container.
            var hostProfileStorage = sp.GetService<Remex.Core.Services.IDashboardProfileStorageService>()
                ?? new Remex.Core.Services.DashboardProfileStorageService();

            return new RemexSavefileService(
                sp.GetRequiredService<DashboardLayoutService>(),
                sp.GetRequiredService<ILauncherStorageService>(),
                new FileTransferRootSettingsService(),
                hostProfileStorage);
        });
        collection.AddSingleton<ThemeService>();
        collection.AddSingleton<HardwareThemeService>();
        collection.AddSingleton<IMdnsDiscoveryService, MdnsDiscoveryService>();
        collection.AddSingleton<PinnedCertStore>();
        collection.AddSingleton<IPairingPinQueryService, IpcPairingPinQueryService>();

        collection.AddSingleton<ConnectionViewModel>();
        collection.AddTransient<AppLauncherViewModel>();
        collection.AddTransient<AddProgramViewModel>();
        collection.AddTransient<TaskManagerViewModel>();
        collection.AddSingleton<HomeViewModel>();
        collection.AddSingleton<ShellViewModel>();
        collection.AddTransient<DiagnosticLogsViewModel>();

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

            var json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var profile = JsonSerializer.Deserialize<Remex.Core.Models.DashboardProfile>(json, options);

            if (profile?.Customization == null) return;

            var themeService = Services.GetRequiredService<ThemeService>();
            
            // Apply the full customization object immediately.
            // This ensures colors, corner radii, and base theme are set BEFORE the window is shown.
            themeService.ApplyCustomization(profile.Customization);
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
            var savefileService = Services.GetRequiredService<RemexSavefileService>();
            var profile = await layoutService.LoadAsync();

            // Silent rolling auto-snapshot: restart the debounce timer every time the dashboard
            // profile is written to disk (canvas moves, settings changes, theme swaps, etc.).
            layoutService.ProfileSaved += () => savefileService.NotifyStateChanged();

            // First-run restore: dashboard_layout.json was missing on load (fresh install or a
            // wiped %LocalAppData%\Remex) but a rolling auto-snapshot exists from a previous
            // install. Offer to restore from it once the main window exists.
            var profileFileWasMissing = layoutService.ProfileFileMissingOnLoad;

            if (profile != null && !string.IsNullOrWhiteSpace(profile.Language))
            {
                try
                {
                    var culture = new System.Globalization.CultureInfo(profile.Language);
                    Remex.Desktop.Localization.Strings.Culture = culture;
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

            if (OperatingSystem.IsWindows() && !CommandModeContext.IsServerMode)
            {
                var pairingPinQueryService = Services.GetService<IPairingPinQueryService>(); // optional service
                if (pairingPinQueryService != null)
                {
                    viewModel.Connection.AttachStandalonePairingPinQueryService(pairingPinQueryService);
                    await viewModel.Connection.RefreshStandalonePairingPinAsync();
                    viewModel.Connection.StartStandalonePairingPinPolling();
                }
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
            UpdateTrayMenuHeaders();
            LocalizationService.Instance.PropertyChanged += (s, e) => UpdateTrayMenuHeaders();

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    if (desktop.MainWindow == null)
                    {
                        desktop.MainWindow = new MainWindow { DataContext = viewModel };
                    }
                    
                    bool startMinimized = desktop.Args != null && Array.Exists(desktop.Args, arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
                    if (!startMinimized)
                    {
                        desktop.MainWindow.Show();
                    }
                }
                else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
                {
                    singleViewPlatform.MainView = new ShellView { DataContext = viewModel };
                }
            });

            // First-run restore prompt (desktop lifetimes only): dashboard_layout.json was
            // missing on load — not merely corrupt — and a rolling auto-snapshot from a previous
            // install exists. Offer to restore everything from it, no restart required.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime restoreDesktop
                && profileFileWasMissing
                && savefileService.TryGetLatestSnapshotPath() is { } snapshotPath
                && restoreDesktop.MainWindow is { } mainWindow)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!mainWindow.IsVisible)
                    {
                        mainWindow.Show();
                    }

                    var shouldRestore = await RestorePromptWindow.ShowAsync(mainWindow, snapshotPath);
                    if (shouldRestore)
                    {
                        await using var stream = File.OpenRead(snapshotPath);
                        await savefileService.ImportAsync(stream);
                    }
                });
            }
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

    private void OnExitApp(object? sender, EventArgs e) => ShutdownApplication();

    private bool _shutdownInitiated;

    /// <summary>
    /// Tears the entire application down — the single source of truth for "Exit",
    /// used by the tray menu, the in-app Exit button, and the close-to-exit window
    /// behavior. Stops the embedded host gracefully (time-boxed), releases the
    /// service mutex, then force-terminates the process via <see cref="Environment.Exit"/>
    /// so that no lingering foreground thread (Kestrel, the mDNS responder, the
    /// network listener, or hardware-monitor interop) can keep the process alive.
    /// </summary>
    public void ShutdownApplication()
    {
        if (_shutdownInitiated) return;
        _shutdownInitiated = true;
        IsShuttingDown = true;

        // Hide the UI immediately so exit feels instant while the host stops.
        try { _flyout?.Close(); } catch { /* best-effort */ }
        _flyout = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { desktop.MainWindow?.Hide(); } catch { /* best-effort */ }
        }

        if (Services?.GetService<ShellViewModel>() is IDisposable disposableVm) // optional service
        {
            try { disposableVm.Dispose(); } catch { /* best-effort */ }
        }

        // Graceful, time-boxed host shutdown on a background thread, then a hard exit.
        _ = Task.Run(async () =>
        {
            try
            {
                if (StopEmbeddedHostAsync is { } stopHost)
                    await stopHost().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* best-effort — we force-exit regardless */ }

            try { CommandModeContext.Cleanup(); } catch { /* best-effort */ }

            Environment.Exit(0);
        });
    }

    /// <summary>
    /// Convenience entry point for callers that only hold a reference to
    /// <see cref="Application.Current"/> (e.g. windows and view-models).
    /// </summary>
    public static void RequestApplicationShutdown()
    {
        if (Current is App app)
            app.ShutdownApplication();
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
        if (Services.GetService<ShellViewModel>() is { } shellVm) // optional service
            shellVm.Customization = newCustomization;

        UpdateThemeToggleLabel(newThemeId);
    }

    private void UpdateThemeToggleLabel(string? themeId)
    {
        _themeToggleMenuItem ??= FindThemeToggleMenuItem();
        if (_themeToggleMenuItem is null) return;

        bool isLight = string.Equals(themeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);
        _themeToggleMenuItem.Header = isLight
            ? LocalizationService.Instance["Tray_SwitchDarkMode"]
            : LocalizationService.Instance["Tray_SwitchLightMode"];
    }

    private NativeMenuItem? FindThemeToggleMenuItem()
    {
        var icons = TrayIcon.GetIcons(this);
        var menu = icons?.FirstOrDefault()?.Menu;
        if (menu == null || menu.Items.Count < 3) return null;
        return menu.Items[2] as NativeMenuItem;
    }

    private void UpdateTrayMenuHeaders()
    {
        var icons = TrayIcon.GetIcons(this);
        var menu = icons?.FirstOrDefault()?.Menu;
        if (menu == null || menu.Items.Count < 5) return;

        if (menu.Items[0] is NativeMenuItem showItem)
            showItem.Header = LocalizationService.Instance["Tray_ShowMainWindow"];

        if (menu.Items[1] is NativeMenuItem glanceItem)
            glanceItem.Header = LocalizationService.Instance["Tray_LiveGlance"];

        if (menu.Items[2] is NativeMenuItem themeItem)
        {
            var layoutService = Services.GetRequiredService<DashboardLayoutService>();
            var currentThemeId = layoutService.CurrentProfile?.Customization.ThemeId ?? "BaseDarkGlass";
            bool isCurrentlyLight = string.Equals(currentThemeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);
            themeItem.Header = isCurrentlyLight
                ? LocalizationService.Instance["Tray_SwitchDarkMode"]
                : LocalizationService.Instance["Tray_SwitchLightMode"];
        }

        if (menu.Items[4] is NativeMenuItem exitItem)
            exitItem.Header = LocalizationService.Instance["Tray_Exit"];
    }
}
