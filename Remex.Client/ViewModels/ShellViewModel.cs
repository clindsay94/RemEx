using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.Models;
using Remex.Client.Services;
using Remex.Client.Services.FileTransfer;
using Remex.Core.Guards;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

/// <summary>
/// Top-level ViewModel that owns navigation between Home, Canvas, and Settings.
/// Shared resources (ConnectionViewModel, DashboardLayoutService) live here and
/// are injected into child ViewModels.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ThemeService _themeService;
    private readonly HardwareThemeService _hardwareThemeService;
    private readonly IImmersiveModeService? _immersiveMode;
    private readonly IServiceProvider _services;
    private readonly Action<Remex.Core.Models.CustomizationSettings> _onCustomizationApplied;
    private readonly PropertyChangedEventHandler _onConnectionChanged;
    private static readonly Random _rng = new();
    private bool _welcomeSplashStarted;

    /// <summary>All tutorial pages in order; each declares which platforms display it.</summary>
    private static readonly IReadOnlyList<TutorialPage> _tutorialPages = new[]
    {
        new TutorialPage(0, "Welcome",          "Welcome to Remex.",                       PlatformFlags.All),
        new TutorialPage(1, "Connect",          "Connect to your host.",                   PlatformFlags.All),
        new TutorialPage(2, "Windows Service",  "Install the Windows background service.", PlatformFlags.Windows),
        new TutorialPage(3, "Linux Service",    "Install the Linux systemd service.",      PlatformFlags.Linux),
        new TutorialPage(4, "HWiNFO",          "Monitor hardware sensors.",               PlatformFlags.Windows),
        new TutorialPage(5, "Dashboard",        "Customize your dashboard.",               PlatformFlags.All),
        new TutorialPage(6, "Remote Control",   "Control your remote machine.",            PlatformFlags.All),
        new TutorialPage(7, "Remote Desktop",   "Stream your remote desktop.",             PlatformFlags.All),
        new TutorialPage(8, "Customization",    "Personalize the app look and feel.",      PlatformFlags.All),
        new TutorialPage(9, "File Transfer",    "Share folders between phone and PC.",     PlatformFlags.All),
        // Glossary Pages
        new TutorialPage(10, "Glossary: Command Palette", "Press Ctrl+Shift+P to search for commands, screens, and quick actions instantly. Use it to quickly disconnect, lock your PC, or jump to Settings.", PlatformFlags.All),
        new TutorialPage(11, "Glossary: App Launcher", "Launch predefined apps and scripts directly on the host machine. You can configure custom paths and arguments in the Settings menu.", PlatformFlags.All),
        new TutorialPage(12, "Glossary: Task Manager", "View and terminate running processes on your host PC remotely. Includes CPU and Memory usage statistics to identify resource hogs.", PlatformFlags.All),
        new TutorialPage(13, "Glossary: Sensor Canvas", "A draggable workspace to monitor your PC's telemetry in real-time. Pin sensors (CPU, RAM, Temps), resize cards, and arrange them to build your ideal dashboard.", PlatformFlags.All),
        new TutorialPage(14, "Glossary: File Transfer", "Securely browse, upload, and download files between your client and host PC. Host machines control access by defining 'Shared Folders' in their settings.", PlatformFlags.All),
        new TutorialPage(15, "Glossary: Remote Desktop", "Stream your host PC's screen directly to your client. Adjust the quality, scaling, and FPS in the settings to optimize performance over your network.", PlatformFlags.All),
        new TutorialPage(16, "Glossary: Quick Settings", "On Android, add RemEx tiles directly to your notification shade's Quick Settings. Easily Lock or Sleep your PC without even opening the app.", PlatformFlags.Android),
        new TutorialPage(17, "Glossary: Customization", "Personalize RemEx! Change themes, toggle dark/light mode, adjust card border radii, and customize the primary accent color via the Settings menu.", PlatformFlags.All),
        new TutorialPage(18, "Finish",          "You're all set — let's go!",             PlatformFlags.All),
    };

    /// <summary>Exposed for child VMs that need to read persisted settings (e.g. stream quality/FPS).</summary>
    public DashboardLayoutService LayoutService => _layoutService;

    /// <summary>Helper property for Android-specific UI logic.</summary>
    public bool IsAndroid => OperatingSystem.IsAndroid();

    /// <summary>Helper property for Desktop-specific UI logic.</summary>
    public bool IsDesktop => !OperatingSystem.IsAndroid();
    public double CompactPaneLength => OperatingSystem.IsAndroid() ? 0 : 64;
    public double OpenPaneLength => OperatingSystem.IsAndroid() ? 0 : 220;


    /// <summary>Shared connection logic — injected into child VMs that need it.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>
    /// The currently active child ViewModel, bound to a TransitioningContentControl.
    /// </summary>
    [ObservableProperty]
    private ObservableObject? _currentView;

    /// <summary>
    /// When true, the shell title bar / navigation chrome is hidden for immersive fullscreen.
    /// </summary>
    [ObservableProperty]
    private bool _isShellChromeHidden;

    /// <summary>Whether the side navigation drawer is expanded.</summary>
    [ObservableProperty]
    private bool _isDrawerOpen;

    /// <summary>Whether the settings overlay panel is open.</summary>
    [ObservableProperty]
    private bool _isSettingsPanelOpen;

    // ═══════════════ Sensor Alert Notifications ═══════════════

    /// <summary>Number of sensor alerts fired in this session (badge count).</summary>
    [ObservableProperty]
    private int _alertBadgeCount;

    /// <summary>Whether there are any unacknowledged sensor alerts.</summary>
    public bool HasAlerts => AlertBadgeCount > 0;

    partial void OnAlertBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasAlerts));

    /// <summary>Recent sensor alert notifications (most recent first).</summary>
    public ObservableCollection<SensorAlertNotification> AlertNotifications { get; } = new();

    [RelayCommand]
    private void DismissAlerts()
    {
        AlertBadgeCount = 0;
        AlertNotifications.Clear();
    }

    // ═══════════════ Tray Tooltip Summary ═══════════════

    /// <summary>
    /// Formatted one-liner shown as the tray icon tooltip.
    /// Example: "Remex — CPU: 54°C · RAM: 67% · Connected"
    /// </summary>
    [ObservableProperty]
    private string _trayStatusSummary = "Remex";

    /// <summary>Recomputes <see cref="TrayStatusSummary"/> from the latest telemetry snapshot.</summary>
    public void UpdateTrayStatus(Remex.Core.Messages.TelemetryPayload? telemetry)
    {
        var connectionLabel = Connection.IsConnected ? "Connected" : "Disconnected";

        if (telemetry?.Sensors is not { Count: > 0 })
        {
            TrayStatusSummary = $"Remex — {connectionLabel}";
            return;
        }

        var pinned = _layoutService.CurrentProfile?.PinnedSensorIds
                     ?? Enumerable.Empty<string>();

        var parts = new System.Collections.Generic.List<string>();
        foreach (var id in pinned)
        {
            if (parts.Count >= 2) break;
            var r = telemetry.Sensors.FirstOrDefault(s => s.Name == id);
            if (r != null)
                parts.Add($"{r.Name.Split(' ')[0]}: {r.Value:F0}{r.Unit}");
        }

        if (parts.Count == 0)
        {
            // Fall back to the first two sensors in the payload
            foreach (var r in telemetry.Sensors.Take(2))
                parts.Add($"{r.Name.Split(' ')[0]}: {r.Value:F0}{r.Unit}");
        }

        TrayStatusSummary = parts.Count > 0
            ? $"Remex — {string.Join(" · ", parts)} · {connectionLabel}"
            : $"Remex — {connectionLabel}";
    }

    /// <summary>Index of the active navigation item (for highlight).</summary>
    [ObservableProperty]
    private int _activeNavIndex;

    /// <summary>Direction of the page slide transition. 1 = forward, -1 = backward.</summary>
    [ObservableProperty]
    private int _transitionDirection = 1;

    /// <summary>Random transition type index (0-3) for variety.</summary>
    [ObservableProperty]
    private int _transitionType;

    /// <summary>Controls the startup welcome splash overlay visibility.</summary>
    [ObservableProperty]
    private bool _showWelcomeSplash = true;

    /// <summary>Controls the first-run tutorial overlay visibility.</summary>
    [ObservableProperty]
    private bool _showTutorialOverlay;

    /// <summary>Current page index of the tutorial (0-based).</summary>
    [ObservableProperty]
    private int _tutorialPageIndex;

    /// <summary>Total number of tutorial pages.</summary>
    public int TutorialPageCount => _tutorialPages.Count;

    /// <summary>User preference to not show tutorial again.</summary>
    [ObservableProperty]
    private bool _dontShowTutorialAgain;

    /// <summary>
    /// When true, a dismissible banner is shown at the top of the content area informing
    /// the user that a host connection is required for the current feature.
    /// </summary>
    [ObservableProperty]
    private bool _showConnectionBanner;

    /// <summary>Message shown in the connection banner.</summary>
    [ObservableProperty]
    private string _connectionBannerMessage = string.Empty;

    /// <summary>
    /// When true, a dismissible banner is shown informing the user that the
    /// layout profile could not be loaded and defaults were applied.
    /// </summary>
    [ObservableProperty]
    private bool _showLayoutLoadWarning;

    /// <summary>Message shown in the layout load warning banner.</summary>
    [ObservableProperty]
    private string _layoutLoadWarningMessage = string.Empty;

    /// <summary>
    /// When true, all infinite/decorative animations are suppressed for users
    /// who prefer reduced motion.  Persisted in the layout profile.
    /// </summary>
    [ObservableProperty]
    private bool _isReducedMotion;

    partial void OnIsReducedMotionChanged(bool value)
    {
        var current = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
        var updated = current with { IsReducedMotion = value };
        _layoutService.RequestSave(updated);
    }

    // ═══════════════ Child VMs (lazy-created, cached) ═══════════════

    private int _lastSensorCardCount = -1;
    private HomeViewModel? _homeViewModel;
    private CanvasDashboardViewModel? _canvasViewModel;
    private SettingsViewModel? _settingsViewModel;
    private RemoteViewModel? _remoteViewModel;
    private AppLauncherViewModel? _appLauncherViewModel;
    private CustomizationViewModel? _customizationViewModel;
    private RemoteDesktopViewModel? _remoteDesktopViewModel;
    private TaskManagerViewModel? _taskManagerViewModel;
    private AboutViewModel? _aboutViewModel;
    private FileTransferViewModel? _fileTransferViewModel;
    private DiagnosticLogsViewModel? _diagnosticLogsViewModel;

    [ObservableProperty]
    private Remex.Core.Models.CustomizationSettings _customization = new();

    public ShellViewModel(DashboardLayoutService layoutService, ThemeService themeService, HardwareThemeService hardwareThemeService, ConnectionViewModel connectionViewModel, IServiceProvider services, IImmersiveModeService? immersiveMode = null)
    {
        _layoutService = Guard.NotNull(layoutService);
        _themeService = Guard.NotNull(themeService);
        _hardwareThemeService = Guard.NotNull(hardwareThemeService);
        Connection = Guard.NotNull(connectionViewModel);
        _services = Guard.NotNull(services);
        _immersiveMode = immersiveMode; // Intentionally optional

        _onCustomizationApplied = settings =>
        {
            Customization = settings;
            _hardwareThemeService.SetEnabled(settings.SyncWithHardware);
        };
        _themeService.CustomizationApplied += _onCustomizationApplied;
        if (_layoutService.CurrentProfile?.Customization != null)
        {
            Customization = _layoutService.CurrentProfile.Customization;
            _hardwareThemeService.SetEnabled(Customization.SyncWithHardware);
        }

        // Load reduced-motion preference
        if (_layoutService.CurrentProfile is { } profile)
            _isReducedMotion = profile.IsReducedMotion;

        // Surface any layout load failure to the user via a dismissible banner.
        if (!string.IsNullOrEmpty(_layoutService.LoadFailureWarning))
        {
            _layoutLoadWarningMessage = _layoutService.LoadFailureWarning;
            _showLayoutLoadWarning = true;
        }

        // Auto-hide the connection banner when the host connects
        _onConnectionChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsConnected) && Connection.IsConnected)
                ShowConnectionBanner = false;
        };
        Connection.PropertyChanged += _onConnectionChanged;

        // Initialize background/shared VMs
        _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this);
        _ = _canvasViewModel.InitializeAsync();
        _canvasViewModel.SensorAlertFired += OnSensorAlertFired;
    }

    public void Dispose()
    {
        _themeService.CustomizationApplied -= _onCustomizationApplied;
        Connection.PropertyChanged -= _onConnectionChanged;

        // Dispose child ViewModels
        _homeViewModel?.Dispose();
        if (_canvasViewModel != null)
        {
            _canvasViewModel.SensorAlertFired -= OnSensorAlertFired;
            _canvasViewModel.Dispose();
        }
        _settingsViewModel?.Dispose();
        _remoteViewModel?.Dispose();
        _appLauncherViewModel?.Dispose();
        _customizationViewModel?.Dispose();
        _remoteDesktopViewModel?.Dispose();
        _taskManagerViewModel?.Dispose();
        _aboutViewModel?.Dispose();
        _fileTransferViewModel?.Dispose();

        // Dispose shared connection ViewModel
        Connection.Dispose();
    }

    public void BeginWelcomeSplash()
    {
        if (_welcomeSplashStarted)
            return;

        _welcomeSplashStarted = true;
        _ = DismissWelcomeSplashAsync();
    }

    private async Task DismissWelcomeSplashAsync()
    {
        // Safety fallback — normally BootSequenceControl fires SequenceCompleted
        await Task.Delay(6000);
        Dispatcher.UIThread.Post(() =>
        {
            if (ShowWelcomeSplash)
                OnBootSequenceCompleted();
        });
    }

    public void OnBootSequenceCompleted()
    {
        ShowWelcomeSplash = false;
        // Show tutorial on first run after the splash fades
        if (!(_layoutService.CurrentProfile?.HasCompletedTutorial ?? false))
        {
            TutorialPageIndex = 0;
            ShowTutorialOverlay = true;
        }
    }

    [RelayCommand]
    public void TutorialNext()
    {
        var platform = OperatingSystem.IsWindows() ? PlatformFlags.Windows
                     : OperatingSystem.IsLinux() ? PlatformFlags.Linux
                     : PlatformFlags.Android;

        int nextIndex = TutorialPageIndex + 1;
        while (nextIndex < _tutorialPages.Count && (_tutorialPages[nextIndex].SupportedPlatforms & platform) == 0)
            nextIndex++;

        if (nextIndex < _tutorialPages.Count)
            TutorialPageIndex = nextIndex;
    }

    [RelayCommand]
    public void TutorialPrevious()
    {
        var platform = OperatingSystem.IsWindows() ? PlatformFlags.Windows
                     : OperatingSystem.IsLinux() ? PlatformFlags.Linux
                     : PlatformFlags.Android;

        int prevIndex = TutorialPageIndex - 1;
        while (prevIndex >= 0 && (_tutorialPages[prevIndex].SupportedPlatforms & platform) == 0)
            prevIndex--;

        if (prevIndex >= 0)
            TutorialPageIndex = prevIndex;
    }

    [RelayCommand]
    public void TutorialSkip() => CompleteTutorial();

    [RelayCommand]
    public void TutorialFinish() => CompleteTutorial();

    [RelayCommand]
    public void ReplayTutorial()
    {
        TutorialPageIndex = 0;
        ShowTutorialOverlay = true;
    }

    [RelayCommand]
    public void DismissConnectionBanner() => ShowConnectionBanner = false;

    // ═══════════════ Canvas Undo / Redo (routed from MainWindow key bindings) ═══════════════

    [RelayCommand]
    private void CanvasUndo() => _canvasViewModel?.UndoCommand.Execute(null);

    [RelayCommand]
    private void CanvasRedo() => _canvasViewModel?.RedoCommand.Execute(null);

    // ═══════════════ Sensor Alert Notifications ═══════════════

    private void OnSensorAlertFired(SensorAlert alert)
    {
        AlertBadgeCount++;
        AlertNotifications.Insert(0, new SensorAlertNotification(
            alert.SensorName, alert.Severity, DateTime.Now));
        // Cap the list at 20 entries
        while (AlertNotifications.Count > 20)
            AlertNotifications.RemoveAt(AlertNotifications.Count - 1);
    }

    [RelayCommand]
    public void DismissLayoutLoadWarning() => ShowLayoutLoadWarning = false;

    /// <summary>
    /// Shows the connection banner if the host is not connected.
    /// Returns true if disconnected (caller may still navigate for preview).
    /// </summary>
    private void NotifyIfDisconnected(string featureName)
    {
        if (!Connection.IsConnected)
        {
            ConnectionBannerMessage = string.Format(LocalizationService.Instance["Status_FeatureRequiresConnection"], featureName);
            ShowConnectionBanner = true;
        }
    }

    private void CompleteTutorial()
    {
        ShowTutorialOverlay = false;
        // Persist that the user has completed the tutorial (or chosen not to see it again)
        var current = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
        var updated = current with { HasCompletedTutorial = true };
        _layoutService.RequestSave(updated);
        // Reset the checkbox for next time (if user manually replays tutorial)
        DontShowTutorialAgain = false;
    }

    private void SetTransitionAndNavigate(int targetIndex, ObservableObject viewModel)
    {
        // Clear app launcher search when navigating away
        if (CurrentView is AppLauncherViewModel alvm && viewModel != alvm)
            alvm.SearchText = string.Empty;

        TransitionDirection = targetIndex >= ActiveNavIndex ? 1 : -1;

        // Material 3 style:
        // On Android, we use a consistent, professional transition.
        // On Desktop, we can keep the variety.
        if (OperatingSystem.IsAndroid())
        {
            // We'll use CrossFade (index 2) as it's the closest to M3 FadeThrough
            // without complex shared-axis custom code.
            TransitionType = 2;
        }
        else
        {
            TransitionType = _rng.Next(4);
        }

        ActiveNavIndex = targetIndex;
        CurrentView = viewModel;
        // Auto-close drawer on mobile/narrow after navigation
        IsDrawerOpen = false;
    }

    // ═══════════════ Navigation Commands ═══════════════

    [RelayCommand]
    public void NavigateToHome()
    {
        _homeViewModel ??= _services.GetRequiredService<HomeViewModel>();
        _homeViewModel.RefreshPinnedSensors();
        SetTransitionAndNavigate(0, _homeViewModel);
    }

    [RelayCommand]
    public void NavigateToCanvas()
    {
        NotifyIfDisconnected("Sensor Workspace");
        SetTransitionAndNavigate(1, _canvasViewModel!);
    }

    [RelayCommand]
    public void NavigateToRemote()
    {
        NotifyIfDisconnected("Remote Control");
        _remoteViewModel ??= new RemoteViewModel(
            Connection, this,
            _services.GetRequiredService<Remex.Core.Services.Network.IWakeOnLanService>(),
            _layoutService);
        SetTransitionAndNavigate(2, _remoteViewModel);
    }

    [RelayCommand]
    public void NavigateToAppLauncher()
    {
        NotifyIfDisconnected("App Launcher");
        if (_appLauncherViewModel is null)
        {
            _appLauncherViewModel = _services.GetRequiredService<AppLauncherViewModel>();
        }
        SetTransitionAndNavigate(3, _appLauncherViewModel);
    }

    [RelayCommand]
    public void NavigateToTaskManager()
    {
        NotifyIfDisconnected("Task Manager");
        _taskManagerViewModel ??= new TaskManagerViewModel(Connection);
        SetTransitionAndNavigate(4, _taskManagerViewModel);
    }

    [RelayCommand]
    public void NavigateToRemoteDesktop()
    {
        _remoteDesktopViewModel ??= new RemoteDesktopViewModel(Connection, this, _immersiveMode);
        SetTransitionAndNavigate(5, _remoteDesktopViewModel);
    }

    [RelayCommand]
    public void NavigateToAbout()
    {
        _aboutViewModel ??= new AboutViewModel(Connection, this);
        SetTransitionAndNavigate(6, _aboutViewModel);
    }

    [RelayCommand]
    public void NavigateToFileTransfer()
    {
        NotifyIfDisconnected(LocalizationService.Instance["Nav_Files"]);
        _fileTransferViewModel ??= new FileTransferViewModel(Connection);
        SetTransitionAndNavigate(7, _fileTransferViewModel);
    }

    [RelayCommand]
    public void NavigateToDiagnosticLogs()
    {
        _diagnosticLogsViewModel ??= new DiagnosticLogsViewModel(this);
        SetTransitionAndNavigate(8, _diagnosticLogsViewModel);
    }

    [RelayCommand]
    public void ToggleSettingsPanel()
    {
        IsSettingsPanelOpen = !IsSettingsPanelOpen;

        // Lazily create the settings/customization VMs
        if (IsSettingsPanelOpen)
        {
            EnsureSettingsVm();
            EnsureCustomizationVm();
        }
    }

    [RelayCommand]
    public void CloseSettingsPanel()
    {
        IsSettingsPanelOpen = false;
    }

    [RelayCommand]
    public void DismissOverlays()
    {
        IsSettingsPanelOpen = false;
        IsDrawerOpen = false;
        ShowTutorialOverlay = false;
        ShowConnectionBanner = false;
    }

    [RelayCommand]
    public void OpenCommandPalette()
    {
        var vm = new CommandPaletteViewModel(this);
        var window = new Remex.Client.Views.CommandPaletteWindow(vm);

        // Find the main window to use as owner for centering
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } mainWindow)
        {
            window.ShowDialog(mainWindow);
        }
        else
        {
            window.Show();
        }
    }

    [RelayCommand]
    public void ToggleDrawer()
    {
        IsDrawerOpen = !IsDrawerOpen;
    }

    // ═══════════════ Legacy navigation kept for backward compat ═══════════════

    [RelayCommand]
    public void NavigateToSettings()
    {
        IsSettingsPanelOpen = false;
        EnsureSettingsVm();
        CurrentView = _settingsViewModel;
    }

    [RelayCommand]
    public void NavigateToCustomization()
    {
        IsSettingsPanelOpen = false;
        EnsureCustomizationVm();
        CurrentView = _customizationViewModel;
    }

    private void EnsureSettingsVm()
    {
        if (_settingsViewModel is null)
        {
            _settingsViewModel = new SettingsViewModel(_layoutService, Connection, this, new FileTransferRootSettingsService());
            _ = _settingsViewModel.InitializeAsync(); // InitializeAsync calls RefreshSensors itself
            _lastSensorCardCount = _canvasViewModel?.Cards.Count(c => c.CardType == "Sensor") ?? -1;
            return;
        }

        // Only rebuild the sensor list when the canvas sensor card count changes,
        // avoiding 50+ CollectionChanged events on every Settings panel open.
        var currentCount = _canvasViewModel?.Cards.Count(c => c.CardType == "Sensor") ?? -1;
        if (currentCount != _lastSensorCardCount)
        {
            _lastSensorCardCount = currentCount;
            _settingsViewModel.RefreshSensors();
        }
    }

    private void EnsureCustomizationVm()
    {
        _customizationViewModel ??= new CustomizationViewModel(this, _layoutService, _themeService);
    }

    /// <summary>
    /// Provides access to the canvas VM for cross-view coordination
    /// (e.g. Home reading pinned sensors from the canvas data).
    /// </summary>
    public CanvasDashboardViewModel? CanvasViewModel => _canvasViewModel;

    /// <summary>Exposed for the settings overlay to bind against.</summary>
    public SettingsViewModel? SettingsVm
    {
        get
        {
            EnsureSettingsVm();
            return _settingsViewModel;
        }
    }

    /// <summary>Exposed for the settings overlay to bind against.</summary>
    public CustomizationViewModel? CustomizationVm
    {
        get
        {
            EnsureCustomizationVm();
            return _customizationViewModel;
        }
    }
}

/// <summary>A single entry in the shell's sensor-alert notification feed.</summary>
public sealed record SensorAlertNotification(string SensorName, AlertSeverity Severity, DateTime Time);
