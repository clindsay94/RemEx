using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core.Guards;
using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

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
    private readonly PropertyChangedEventHandler _onPresenceChanged;
    private bool _welcomeSplashStarted;

    /// <summary>All tutorial pages in order; each declares which platforms display it.</summary>
    private static readonly IReadOnlyList<TutorialPage> _tutorialPages = new[]
    {
        new TutorialPage(0, "Welcome",          "Welcome to Remex.",                       PlatformFlags.All),
        new TutorialPage(1, "Connect",          "Connect to your host.",                   PlatformFlags.All),
        new TutorialPage(2, "HWiNFO",          "Monitor hardware sensors.",               PlatformFlags.Windows),
        new TutorialPage(3, "Dashboard",        "Customize your dashboard.",               PlatformFlags.All),
        new TutorialPage(4, "Remote Control",   "Control your remote machine.",            PlatformFlags.All),
        new TutorialPage(5, "Remote Desktop",   "Stream your remote desktop.",             PlatformFlags.All),
        new TutorialPage(6, "Customization",    "Personalize the app look and feel.",      PlatformFlags.All),
        new TutorialPage(7, "File Transfer",    "Share folders between phone and PC.",     PlatformFlags.All),
        // Glossary Pages
        new TutorialPage(8, "Glossary: Command Palette", "Press Ctrl+K to search for commands, screens, and quick actions instantly. Use it to quickly disconnect, lock your PC, or jump to Settings.", PlatformFlags.All),
        new TutorialPage(9, "Glossary: App Launcher", "Launch predefined apps and scripts directly on the host machine. You can configure custom paths and arguments in the Settings menu.", PlatformFlags.All),
        new TutorialPage(10, "Glossary: Task Manager", "View and terminate running processes on your host PC remotely. Includes CPU and Memory usage statistics to identify resource hogs.", PlatformFlags.All),
        new TutorialPage(11, "Glossary: Sensor Canvas", "A draggable workspace to monitor your PC's telemetry in real-time. Pin sensors (CPU, RAM, Temps), resize cards, and arrange them to build your ideal dashboard.", PlatformFlags.All),
        new TutorialPage(12, "Glossary: File Transfer", "Securely browse, upload, and download files between your client and host PC. Host machines control access by defining 'Shared Folders' in their settings.", PlatformFlags.All),
        new TutorialPage(13, "Glossary: Remote Desktop", "Stream your host PC's screen directly to your client. Adjust the quality, scaling, and FPS in the settings to optimize performance over your network.", PlatformFlags.All),
        new TutorialPage(14, "Glossary: Quick Settings", "On Android, add RemEx tiles directly to your notification shade's Quick Settings. Easily Lock or Sleep your PC without even opening the app.", PlatformFlags.Android),
        new TutorialPage(15, "Glossary: Customization", "Personalize RemEx! Change themes, toggle dark/light mode, adjust card border radii, and customize the primary accent color via the Settings menu.", PlatformFlags.All),
        new TutorialPage(16, "Finish",          "You're all set — let's go!",             PlatformFlags.All),
    };

    /// <summary>Exposed for child VMs that need to read persisted settings (e.g. stream quality/FPS).</summary>
    public DashboardLayoutService LayoutService => _layoutService;

    // IsAndroid / IsDesktop removed with the dead Android chrome (RemEx-f167): remex.desktop
    // targets net10.0, not net10.0-android, so OperatingSystem.IsAndroid() is never true here.
    // The pane widths lost their unreachable zero-width Android branch for the same reason.
    // CompactPaneLength went with the compact rail when the shell moved to an overlay drawer
    // (RemEx-q3mle): an overlay drawer is either over the content or gone, never a 64px stub.
    public double OpenPaneLength => 220;

    /// <summary>
    /// The window width past which the navigation drawer would pin itself permanently open.
    /// Infinite, so it never does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Material's <c>NavigationDrawer</c> has no overlay/inline switch. It derives the mode from
    /// <c>LeftDrawerExpandThresholdWidth</c>: <c>UpdateDesktopExpand</c> computes
    /// <c>status = width &gt; threshold</c>, and while that is false the drawer floats over the content
    /// behind a scrim, which is the mode this shell wants at every size.
    /// </para>
    /// <para>
    /// **LEAVING IT UNSET DOES THE OPPOSITE OF WHAT IT LOOKS LIKE.** A null threshold takes the
    /// <c>else</c> branch, which sets <c>_isLeftDrawerDesktopExpanded = true</c> unconditionally —
    /// permanently expanded. <c>UpdateContentMargin</c> then indents the content by the drawer's width
    /// whenever it opens, so the page visibly jumps sideways. Infinity is what actually asks for
    /// "never expand"; any finite value only postpones the jump to a wide enough monitor.
    /// </para>
    /// </remarks>
    public const double NeverExpandThresholdWidth = double.PositiveInfinity;

    /// <summary>Bound to <c>NavigationDrawer.LeftDrawerExpandThresholdWidth</c>, which is nullable.</summary>
    public double? DrawerExpandThresholdWidth => NeverExpandThresholdWidth;


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

    /// <summary>
    /// Mutual exclusion between the drawer (RemEx-q3mle) and the settings side sheet (RemEx-zrlze).
    /// Both are full-height overlays with their own scrim, and both can be triggered from several
    /// places (the drawer toggle, nav-item activation, the gear FAB, <c>NavigateToCustomization</c>,
    /// <c>DismissOverlays</c>) - putting the rule here instead of in each call site means every one
    /// of them gets it for free, and a new caller added later cannot forget it. Closing the OTHER
    /// side is a plain assignment rather than a toggle, so setting either property false never
    /// re-opens the one that was just closed: only the "opening" transition (value == true) cascades.
    /// This is also what keeps the scrim from ever doubling up (ShellView's SideSheet and
    /// NavigationDrawer each paint their own) and what makes Esc's "close the topmost surface"
    /// unambiguous in ShellView.OnKeyDown - at most one of these two is ever true at once, so there
    /// is no real stack to order, only these two mutually-exclusive flags.
    /// </summary>
    partial void OnIsDrawerOpenChanged(bool value)
    {
        if (value)
            IsSettingsPanelOpen = false;
    }

    /// <summary>See <see cref="OnIsDrawerOpenChanged(bool)"/>.</summary>
    partial void OnIsSettingsPanelOpenChanged(bool value)
    {
        if (value)
            IsDrawerOpen = false;
    }

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

    /// <summary>How often the tray tooltip is rebuilt, however often telemetry arrives.</summary>
    /// <remarks>
    /// Five seconds because the tooltip is only ever READ ON HOVER, and reaching it takes longer than
    /// that. A tooltip cannot be stale to someone who is not looking at it.
    /// </remarks>
    internal static readonly TimeSpan TrayTooltipInterval = TimeSpan.FromSeconds(5);

    private DateTime _lastTrayUpdateUtc = DateTime.MinValue;
    private bool _lastTrayConnected;

    /// <summary>
    /// Whether the tray tooltip is due a rebuild (RemEx-zcos item 3).
    /// </summary>
    /// <remarks>
    /// **CONNECTION CHANGES BYPASS THE THROTTLE, AND THAT IS THE WHOLE CARE IN THIS CHANGE.** The
    /// readings in the tooltip are worth five seconds of staleness; "Disconnected" is not. Without the
    /// second clause a user who just lost their PC could hover and be told everything is fine.
    /// </remarks>
    internal static bool ShouldRebuildTray(
        DateTime nowUtc, DateTime lastUtc, bool connected, bool lastConnected, TimeSpan interval) =>
        connected != lastConnected || nowUtc - lastUtc >= interval;

    /// <summary>Recomputes <see cref="TrayStatusSummary"/> from the latest telemetry snapshot.</summary>
    /// <remarks>
    /// Throttled: this ran on every telemetry tick, once a second, forever, rebuilding a string for a
    /// tooltip nobody was looking at - and it kept doing it while the window was minimised to the
    /// tray, which is exactly when the UI thread should be idlest. The assignment itself was already
    /// cheap when the text was unchanged, because the generated <c>[ObservableProperty]</c> setter
    /// skips equal values; the waste was building the string to discover that.
    /// </remarks>
    public void UpdateTrayStatus(Remex.Core.Messages.TelemetryPayload? telemetry)
    {
        var connected = Connection.IsConnected;
        if (!ShouldRebuildTray(DateTime.UtcNow, _lastTrayUpdateUtc, connected, _lastTrayConnected, TrayTooltipInterval))
        {
            return;
        }

        _lastTrayUpdateUtc = DateTime.UtcNow;
        _lastTrayConnected = connected;

        var connectionLabel = connected ? LocalizationService.Instance["Status_Connected"] : LocalizationService.Instance["Status_Disconnected"];

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

    /// <summary>
    /// Which way the shared-axis page transition travels. 1 = forward (further down the sidebar),
    /// -1 = backward.
    /// </summary>
    [ObservableProperty]
    private int _transitionDirection = 1;

    /// <summary>Controls the startup welcome splash overlay visibility.</summary>
    [ObservableProperty]
    private bool _showWelcomeSplash = true;

    /// <summary>
    /// Keeps BootSplash mounted through its opacity crossfade into the shell (RemEx-72s7l).
    /// ShowWelcomeSplash drives the fade-out itself; this stays true until the fade finishes
    /// so IsVisible does not cut the control away mid-transition.
    /// </summary>
    [ObservableProperty]
    private bool _isWelcomeSplashMounted = true;

    /// <summary>Controls the first-run tutorial overlay visibility.</summary>
    [ObservableProperty]
    private bool _showTutorialOverlay;

    /// <summary>Current page index of the tutorial (0-based).</summary>
    [ObservableProperty]
    private int _tutorialPageIndex;

    /// <summary>Total number of tutorial pages.</summary>
    public int TutorialPageCount => _tutorialPages.Count;

    /// <summary>Per-dot opacity (1.0 = active, 0.3 = inactive) for the tutorial page-indicator row.</summary>
    public IReadOnlyList<double> TutorialPageDots =>
        Enumerable.Range(0, TutorialPageCount)
                  .Select(i => i == TutorialPageIndex ? 1.0 : 0.3)
                  .ToList();

    partial void OnTutorialPageIndexChanged(int value) =>
        OnPropertyChanged(nameof(TutorialPageDots));

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
        OnPropertyChanged(nameof(SuppressPaletteTransitions));
        OnPropertyChanged(nameof(ShowPresencePulse));
    }

    /// <summary>
    /// True while the Palette Studio's seed wheel has a pointer captured (RemEx-zgtn1). Set by
    /// <see cref="CustomizationViewModel.IsSeedDragging"/>, which mirrors
    /// <see cref="Remex.Desktop.Controls.HctColorWheel"/>'s own <c>IsDragging</c> — every frame of a drag repaints the whole
    /// palette, and a crossfade that restarts on every frame would lag a full <c>Duration</c> behind
    /// the pointer instead of tracking it. NOT persisted: this is transient interaction state, not a
    /// setting.
    /// </summary>
    [ObservableProperty]
    private bool _isPaletteDragging;

    partial void OnIsPaletteDraggingChanged(bool value) => OnPropertyChanged(nameof(SuppressPaletteTransitions));

    /// <summary>
    /// Whether the window/backdrop crossfade that follows a palette change should be skipped in
    /// favour of an instant snap — either because the user asked for reduced motion, or because a
    /// wheel drag is live and the preview needs to track the pointer with no easing. Bound onto the
    /// suppression <c>Classes</c> on <see cref="Remex.Desktop.MainWindow"/> and
    /// <see cref="Remex.Desktop.Controls.DashboardBackgroundControl"/> (App.axaml's
    /// <c>Window.palette-transition-suppressed</c> / <c>Grid.palette-transition-suppressed</c>
    /// selectors null out their <c>Transitions</c> while this is true).
    /// </summary>
    public bool SuppressPaletteTransitions => IsReducedMotion || IsPaletteDragging;

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

        // The presence dot's pulse (RemEx-d7xj8) depends on the phone-attached flag from the
        // process-wide PhonePresenceMonitor singleton, not on anything this VM owns - re-raise
        // ShowPresencePulse whenever it flips so the badge's .pulse class tracks it live.
        _onPresenceChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(PhonePresenceMonitor.IsPhoneAttached))
                OnPropertyChanged(nameof(ShowPresencePulse));
        };
        Presence.PropertyChanged += _onPresenceChanged;

        // Initialize background/shared VMs
        _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this);
        _ = _canvasViewModel.InitializeAsync();
        _canvasViewModel.SensorAlertFired += OnSensorAlertFired;
    }

    /// <summary>
    /// Whether a phone is attached, shared with every other indicator (RemEx-7zzw).
    /// </summary>
    /// <remarks>
    /// MOVED OUT OF THIS CLASS. RemEx-0z7w put the state here and rebound only the shell's dot,
    /// which left three other indicators showing the loopback link — so the app disagreed with
    /// itself screen to screen. <see cref="PhonePresenceMonitor"/> is process-wide and every
    /// surface exposes it the same way, which is the only arrangement where they cannot drift.
    /// </remarks>
    public PhonePresenceMonitor Presence => PhonePresenceMonitor.Instance;

    /// <summary>
    /// True when the drawer-footer connection button's presence badge should pulse (RemEx-d7xj8):
    /// only while a phone is actually attached, and never when the user prefers reduced motion.
    /// </summary>
    public bool ShowPresencePulse => Presence.IsPhoneAttached && !IsReducedMotion;

    /// <summary>
    /// This PC's host name, for the drawer header identity block (RemEx-dnqws). The same value
    /// <c>PairingHandler</c> and <c>MdnsAdvertisingService</c> (both in <c>remex.agent</c>, not
    /// referenceable from here) already surface as host identity elsewhere - not new data, just this
    /// view's first use of it. It never
    /// changes for a running process, so it is a plain get-only property rather than an
    /// <c>[ObservableProperty]</c>: there is nothing for <see cref="INotifyPropertyChanged"/> to
    /// announce.
    /// </summary>
    public string MachineName => Environment.MachineName;

    public void Dispose()
    {
        _themeService.CustomizationApplied -= _onCustomizationApplied;
        Connection.PropertyChanged -= _onConnectionChanged;
        Presence.PropertyChanged -= _onPresenceChanged;

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
        _ = UnmountWelcomeSplashAsync();
    }

    /// <summary>
    /// Keeps BootSplash mounted for the length of its 0.4s opacity crossfade (RemEx-72s7l),
    /// then flips IsVisible off so the fade never gets cut short by the control disappearing.
    /// </summary>
    private async Task UnmountWelcomeSplashAsync()
    {
        await Task.Delay(450);
        Dispatcher.UIThread.Post(() => IsWelcomeSplashMounted = false);
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

        // The only thing the view needs to know: which way along the sidebar the user moved. The
        // shell used to pick one of four transitions at random per navigation, which meant the same
        // journey animated differently each time and told the user nothing. Material's shared axis
        // is one transition whose direction carries the meaning instead (RemEx-yzu5m).
        TransitionDirection = targetIndex >= ActiveNavIndex ? 1 : -1;

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

        // OPENING SENSORS ACKNOWLEDGES THE ALERTS (RemEx-rjnbo). A badge that never clears is a
        // badge people stop reading. The alerts are sensor alerts and this is the sensor page, so
        // arriving here IS having seen them.
        //
        // The COUNT is reset and AlertNotifications is deliberately left alone: the count means
        // "unacknowledged", the list is the history, and nothing displays the history yet. Clearing
        // both here would throw away the only record the moment a future flyout wants to show it.
        AlertBadgeCount = 0;

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
        // Hand-constructed rather than DI-resolved, so the logger has to be passed explicitly —
        // the constructor's optional-logger default would silently degrade to NullLogger and
        // discard every frame-decode diagnostic.
        _remoteDesktopViewModel ??= new RemoteDesktopViewModel(
            Connection,
            this,
            _immersiveMode,
            _services.GetRequiredService<ILogger<RemoteDesktopViewModel>>());
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
        // Hand-constructed rather than DI-resolved, so the logger has to be passed explicitly —
        // same reasoning as NavigateToRemoteDesktop above: the constructor default would
        // degrade to no logger and discard every file-transfer failure diagnostic.
        _fileTransferViewModel ??= new FileTransferViewModel(
            Connection,
            _services.GetRequiredService<ILogger<FileTransferViewModel>>(),
            _services.GetRequiredService<ILogger<FileTransferQueue>>());
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
        var window = new Remex.Desktop.Views.CommandPaletteWindow(vm);

        // Find the main window to use as owner for centering
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } mainWindow)
        {
            // Destructive palette entries confirm against the MAIN window, not the palette window:
            // the palette closes before the dialog is shown, so parenting to it would leave the
            // dialog owner-less mid-flight (RemEx-eifi).
            vm.OnConfirmationRequested = Remex.Desktop.Views.ConfirmationDialogHost.For(mainWindow);

            // Show, not ShowDialog (Connor, live 2026-08-31): ShowDialog disables mainWindow for as
            // long as the palette is open, so a click meant to dismiss it lands on a disabled
            // window and Windows just beeps — the palette never sees the click, never loses focus,
            // and Esc was the only way out. Show(owner) keeps the same owner (WindowStartupLocation
            // CenterOwner still centers on it) and the same Topmost="True" from the .axaml keeps it
            // above mainWindow, but leaves mainWindow clickable, so a click outside genuinely moves
            // OS focus away and CommandPaletteWindow's Deactivated handler fires — the same light
            // dismiss that already had to work for the "no main window" Show() branch below, and
            // the same one ExecuteEntryAsync's confirmation-dialog comment already assumed.
            window.Show(mainWindow);
        }
        else
        {
            // No main window means no owner to host a confirmation, so OnConfirmationRequested stays
            // null and the destructive entries decline — the same fail-closed outcome every other
            // confirmed action has.
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
        SetTransitionAndNavigate(9, _settingsViewModel!);
    }

    /// <summary>
    /// Opens the live Personalization popup. Kept for the command palette / Home shortcut that
    /// previously navigated to the full-screen Customization page (now retired — its content
    /// lives in the FAB popup so changes preview live against the current screen).
    /// </summary>
    [RelayCommand]
    public void NavigateToCustomization()
    {
        EnsureSettingsVm();
        EnsureCustomizationVm();
        IsSettingsPanelOpen = true;
    }

    private void EnsureSettingsVm()
    {
        if (_settingsViewModel is null)
        {
            _settingsViewModel = new SettingsViewModel(
                _layoutService,
                Connection,
                this,
                new FileTransferRootSettingsService(),
                _services.GetRequiredService<Remex.Desktop.Services.Backup.RemexSavefileService>());
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
