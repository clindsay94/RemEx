using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core.Guards;
using Remex.Core.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

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
    private readonly IImmersiveModeService? _immersiveMode;
    private readonly IServiceProvider _services;
    private readonly Action<Remex.Core.Models.CustomizationSettings> _onCustomizationApplied;
    private readonly PropertyChangedEventHandler _onConnectionChanged;
    private readonly PropertyChangedEventHandler _onPresenceChanged;
    private readonly Action _onProfileReplaced;
    private readonly Action _onTrippedChanged;
    private readonly SensorAlertTracker _alertTracker;
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

    /// <summary>
    /// Number of unacknowledged tripped sensors (badge count). Mirrors
    /// <see cref="SensorAlertTracker.TrippedCount"/> rather than counting alerts fired this session -
    /// refreshed whenever <see cref="SensorAlertTracker.TrippedChanged"/> fires (a trip, an
    /// acknowledge, or acknowledge-all), so it means "how many sensors are tripped right now", not
    /// "how many times has an alert fired" (RemEx-8wpvr.3).
    /// </summary>
    [ObservableProperty]
    private int _alertBadgeCount;

    /// <summary>Whether there are any unacknowledged sensor alerts.</summary>
    public bool HasAlerts => AlertBadgeCount > 0;

    partial void OnAlertBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasAlerts));

    /// <summary>Recent sensor alert notifications (most recent first).</summary>
    public ObservableCollection<SensorAlertNotification> AlertNotifications { get; } = new();

    /// <summary>
    /// Dismissing is the only thing that acknowledges every tripped sensor at once - navigating to
    /// the canvas no longer does (RemEx-8wpvr.3: opening the page cleared a badge whose sensors were
    /// still tripped, which just hid the signal rather than resolving it). The notification history
    /// is still cleared here, same asymmetry as before: this command discards both, navigation
    /// discards neither.
    /// </summary>
    [RelayCommand]
    private void DismissAlerts()
    {
        _alertTracker.AcknowledgeAll();
        AlertNotifications.Clear();
    }

    // ═══════════════ File Transfer Badge (RemEx-rjnbo.1) ═══════════════

    /// <summary>
    /// The one <see cref="FileTransferQueue"/> for this session, owned HERE rather than by
    /// <see cref="FileTransferViewModel"/> so its count exists before the Files page does.
    /// </summary>
    /// <remarks>
    /// <c>FileTransferViewModel</c> is built lazily, on first navigation to Files (see
    /// <see cref="NavigateToFileTransfer"/>) - so a badge bound to ITS queue would only ever appear
    /// after the user had already been to the page it is meant to draw them to, which is backwards
    /// for a badge. Constructing the queue here, unconditionally, and handing this same instance to
    /// <c>FileTransferViewModel</c> once it exists means there is exactly one queue for the session,
    /// not two that could disagree about how many transfers are active.
    /// <c>GetService</c>, not <c>GetRequiredService</c>: this runs unconditionally in the
    /// constructor, and <see cref="FileTransferQueue"/> already treats a null logger as
    /// <c>NullLogger</c>, so a test DI container with nothing registered for it degrades to no
    /// logging rather than throwing out of a constructor nothing about file transfer touches.
    /// </remarks>
    private readonly FileTransferQueue _transferQueue;

    /// <summary>
    /// Test seam (RemEx-rjnbo.1): lets a test enqueue directly against the same queue the Files
    /// badge and (once built) the Files page share, without ever calling
    /// <see cref="NavigateToFileTransfer"/> - which is the whole point being tested, that the count
    /// exists before the page does.
    /// </summary>
    internal FileTransferQueue TransferQueueForTests => _transferQueue;

    /// <summary>Number of transfers currently in flight (queued or actively transferring).</summary>
    [ObservableProperty]
    private int _activeTransferCount;

    /// <summary>Whether the Files nav badge should be visible - a zero count hides rather than shows "0".</summary>
    public bool HasActiveTransfers => ActiveTransferCount > 0;

    partial void OnActiveTransferCountChanged(int value) => OnPropertyChanged(nameof(HasActiveTransfers));

    private void OnTransferQueueChanged() =>
        ActiveTransferCount = _transferQueue.Items.Count(item => item.IsActive);

    // ═══════════════ Diagnostics Badge (RemEx-rjnbo.1) ═══════════════

    /// <summary>
    /// Log entries at Warning or above since the Logs &amp; Diagnostics page was last opened - "the
    /// smallest honest source" for an unread count, since nothing in the app tracks a richer concept
    /// of "read" than that.
    /// </summary>
    [ObservableProperty]
    private int _diagnosticsBadgeCount;

    /// <summary>Whether the Diagnostics nav badge should be visible - a zero count hides rather than shows "0".</summary>
    public bool HasUnreadDiagnostics => DiagnosticsBadgeCount > 0;

    partial void OnDiagnosticsBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasUnreadDiagnostics));

    /// <summary>
    /// Runs a diagnostics-log callback on the UI thread. A test seam, same shape as
    /// <see cref="ProfileReplacedDispatch"/> and for the identical reason:
    /// <see cref="InMemoryLogSink.LogAdded"/> can fire from any thread a logger call happens to run
    /// on, and this assembly has no <c>Avalonia.Headless</c> reference to pump a real
    /// <c>Dispatcher.UIThread.Post</c> in a test.
    /// </summary>
    internal Action<Action> DiagnosticsLogDispatch { get; set; } = run =>
    {
        if (Dispatcher.UIThread.CheckAccess())
            run();
        else
            Dispatcher.UIThread.Post(run);
    };

    private void OnDiagnosticLogAdded(LogEntry entry)
    {
        if (entry.Level < LogLevel.Warning) return;

        // Sitting on the Logs page during a warning burst must not inflate the badge with entries
        // already visible on screen (review, MEDIUM, RemEx-rjnbo.1) - only what arrives while the
        // user is elsewhere counts as unread. Checked from inside the dispatched callback rather
        // than before it, so a navigation racing the log arrival is judged as of when the increment
        // actually runs, on the UI thread, not as of whatever thread the logger call fired from.
        // NavigateToDiagnosticLogs already resets the count to 0 on arrival - that IS the "seen"
        // marker, so all this needs to do is stop moving it while the page is current.
        DiagnosticsLogDispatch(() =>
        {
            if (CurrentView is not DiagnosticLogsViewModel)
                DiagnosticsBadgeCount++;
        });
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

    /// <summary>The view restarts the mounted SkiaSplashControl when this fires (Preview on the sheet).</summary>
    public event Action? SplashReplayRequested;

    /// <summary>
    /// Set while a Preview-triggered replay is in flight (RemEx-8twk0.8 fix round, HIGH). The replay's
    /// completion goes through the same <see cref="OnBootSequenceCompleted"/> path as the first boot,
    /// so without this flag a fresh profile that dismissed the first-run tutorial without persisting
    /// (Escape -&gt; <see cref="DismissOverlays"/>) would have the full onboarding overlay raised again
    /// on top of the Personalize sheet. See <see cref="ResolveBootCompletion"/>.
    /// </summary>
    private bool _isSplashPreview;

    /// <summary>Mounts the splash again and asks the view to restart it. Completion goes through the
    /// same <see cref="OnBootSequenceCompleted"/> path as the first run; <see cref="ResolveBootCompletion"/>
    /// short-circuits the tutorial gate there while a preview is in flight.</summary>
    public void ReplayWelcomeSplash()
    {
        _isSplashPreview = true;
        IsWelcomeSplashMounted = true;
        ShowWelcomeSplash = true;
        SplashReplayRequested?.Invoke();
    }

    /// <summary>Controls the first-run tutorial overlay visibility.</summary>
    [ObservableProperty]
    private bool _showTutorialOverlay;

    /// <summary>
    /// Resets the carousel to page 0 whenever the overlay is (re)shown (RemEx-9iz00.1) - both
    /// <see cref="OnBootSequenceCompleted"/> and <see cref="ReplayTutorial"/> already set
    /// <see cref="TutorialPageIndex"/> to 0 first, but this makes it a guarantee of the flag
    /// itself rather than something every future call site has to remember to do in order.
    /// </summary>
    partial void OnShowTutorialOverlayChanged(bool value)
    {
        if (value)
            TutorialPageIndex = 0;
    }

    private int _tutorialPageIndex;

    /// <summary>
    /// Current page index of the tutorial (0-based), snapped into the platform-visible page list
    /// (RemEx-9iz00.1 fix round, HIGH) so an out-of-range or platform-hidden value - from the
    /// Carousel's own SelectedIndex binding, for instance - can never land on a page
    /// <see cref="CurrentTutorialPlatform"/> does not support. A manual property rather than
    /// [ObservableProperty] because the snap has to run before the value is ever stored, and
    /// CommunityToolkit's generated On...Changing hook cannot mutate it.
    /// </summary>
    public int TutorialPageIndex
    {
        get => _tutorialPageIndex;
        set
        {
            var snapped = SnapToVisibleTutorialPage(value);
            var changed = SetProperty(ref _tutorialPageIndex, snapped);
            if (changed)
            {
                OnPropertyChanged(nameof(TutorialVisiblePageIndex));
                OnPropertyChanged(nameof(IsTutorialFirstPage));
                OnPropertyChanged(nameof(IsTutorialLastPage));
                TutorialNextCommand.NotifyCanExecuteChanged();
                TutorialPreviousCommand.NotifyCanExecuteChanged();
                return;
            }

            if (snapped != value)
            {
                // The stored value already equals the snap, but the incoming value did not - a
                // two-way binding (Carousel.SelectedIndex, PipsPager.SelectedPageIndex) pushed an
                // out-of-range or platform-hidden index. SetProperty saw no field change and stayed
                // silent, which would leave that control showing the invalid pushed value forever.
                // Raise both notifications by hand so the binding pulls the corrected value back.
                OnPropertyChanged(nameof(TutorialPageIndex));
                OnPropertyChanged(nameof(TutorialVisiblePageIndex));
            }
        }
    }

    /// <summary>
    /// Snaps <paramref name="candidate"/> to the nearest page <see cref="CurrentTutorialPlatform"/>
    /// supports - forward (the next higher visible index) first, falling back to the nearest
    /// visible index behind it when nothing forward exists (an out-of-range-above value, or a
    /// hidden page with no visible successor). <see cref="VisibleTutorialPageIndices"/> is sorted
    /// ascending, so a single forward pass finds either an exact match or the correct forward
    /// snap; falling off the end means backward is the only option left.
    /// </summary>
    /// <remarks>
    /// Not a duplicate of <see cref="TutorialNavigator.ClampPosition"/> (RemEx-qgql review): that
    /// clamps a POSITION that already lives in the filtered space (0..count-1, no gaps) - a pure
    /// numeric bound. This snaps a raw, possibly-hidden <see cref="TutorialPage.PageIndex"/> - a
    /// space with gaps - forward to the nearest one the platform actually shows. Different domain,
    /// different job; <c>TutorialNavigator</c> has no equivalent because a raw-index snap is not a
    /// concept it needs (everything else there already operates on filtered positions).
    /// </remarks>
    private int SnapToVisibleTutorialPage(int candidate)
    {
        var visible = VisibleTutorialPageIndices;
        if (visible.Count == 0)
            return 0;

        foreach (var index in visible)
        {
            if (index == candidate)
                return candidate;
            if (index > candidate)
                return index;
        }

        return visible[^1];
    }

    /// <summary>
    /// The <see cref="TutorialPage.PageIndex"/> author values of the pages whose
    /// <c>SupportedPlatforms</c> includes <see cref="CurrentTutorialPlatform"/>
    /// (RemEx-9iz00.1 fix round, HIGH), in ascending <c>PageIndex</c> order - NOT list positions
    /// into <see cref="_tutorialPages"/>, though the two coincide today because every entry there
    /// happens to satisfy <c>PageIndex == </c>(its position in the array). Nothing enforces that;
    /// it holds only because <c>_tutorialPages</c> was authored in order and never had a page
    /// inserted or renumbered out of sequence. The PipsPager and <see cref="TutorialVisiblePageIndex"/>
    /// key off this list rather than the raw 17-slot Carousel so pagination never exposes a page
    /// the running platform does not have; the Carousel's own slots stay declaration-ordered, so
    /// that agreement is what the invariant test below guards (RemEx-qgql review, MEDIUM).
    /// Delegates to <see cref="TutorialNavigator.VisiblePages"/> (RemEx-qgql) instead of
    /// re-filtering by hand - this file used to have its own copy of exactly the rule
    /// <see cref="TutorialNavigator"/> was already shipped and mutation-tested for, and two
    /// implementations of "which pages does this platform show" drift, silently, on the platform
    /// whose pages get hidden.
    /// </summary>
    public IReadOnlyList<int> VisibleTutorialPageIndices =>
        TutorialNavigator.VisiblePages(_tutorialPages, CurrentTutorialPlatform)
                          .Select(p => p.PageIndex)
                          .ToList();

    /// <summary>Number of tutorial pages the running platform actually shows - what the PipsPager's
    /// <c>NumberOfPages</c> binds to, so its dot count never exceeds what a pip click can reach.</summary>
    public int TutorialVisiblePageCount => VisibleTutorialPageIndices.Count;

    /// <summary>
    /// <see cref="TutorialPageIndex"/> expressed as a position in <see cref="VisibleTutorialPageIndices"/>
    /// (0..<see cref="TutorialVisiblePageCount"/> - 1) - what the PipsPager's <c>SelectedPageIndex</c>
    /// binds to two-way, so a pip click or arrow key can only ever select a platform-supported page.
    /// </summary>
    public int TutorialVisiblePageIndex
    {
        get
        {
            var visible = VisibleTutorialPageIndices;
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i] == TutorialPageIndex)
                    return i;
            }
            return 0; // TutorialPageIndex is always snapped into the visible list; unreachable otherwise.
        }
        set
        {
            var visible = VisibleTutorialPageIndices;
            if (visible.Count == 0)
                return;

            var clamped = Math.Clamp(value, 0, visible.Count - 1);
            var target = visible[clamped];
            var wasNoOp = TutorialPageIndex == target;
            TutorialPageIndex = target;
            if (clamped != value && wasNoOp)
            {
                // TutorialPageIndex was already `target`, so its own setter saw no field change and
                // stayed silent - mirroring its no-op path, raise both notifications by hand so an
                // out-of-range push (e.g. PipsPager.SelectedPageIndex past the last visible page)
                // gets corrected back on the binding instead of sticking at the invalid pushed value.
                OnPropertyChanged(nameof(TutorialVisiblePageIndex));
                OnPropertyChanged(nameof(TutorialPageIndex));
            }
        }
    }

    /// <summary>Total number of tutorial pages, including ones the running platform hides.</summary>
    public int TutorialPageCount => _tutorialPages.Count;

    /// <summary>
    /// Whether the carousel is on the first platform-visible page (RemEx-qgql). The Back button's
    /// <c>IsVisible</c> binds to this instead of a raw <c>TutorialPageIndex != 0</c> check, which
    /// only worked because page 0 happens to be <see cref="PlatformFlags.All"/> and the lowest
    /// author index on every platform - true today, not guaranteed by anything that would fail
    /// loudly if it stopped being true.
    /// </summary>
    public bool IsTutorialFirstPage => TutorialVisiblePageIndex == 0;

    /// <summary>
    /// Whether the carousel is on the last platform-visible page (RemEx-qgql), via
    /// <see cref="TutorialNavigator.IsLastPage"/> - the same shipped predicate
    /// <see cref="CanTutorialNext"/> now delegates to. The Next/Finish buttons' <c>IsVisible</c>
    /// bindings use this instead of the old hardcoded <c>ConverterParameter=16</c>, which only
    /// worked because page 16 happens to be <see cref="PlatformFlags.All"/> and the highest author
    /// index on every platform.
    /// </summary>
    public bool IsTutorialLastPage => TutorialNavigator.IsLastPage(TutorialVisiblePageIndex, TutorialVisiblePageCount);

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
    /// True while the main window is shown AND not minimized (perf audit P0-2). Pushed by
    /// <see cref="Remex.Desktop.MainWindow"/> on every IsVisible / WindowState / DataContext change;
    /// defaults to true so a host with no MainWindow (single-view) animates as before. Infinite
    /// decorative animations (the Aurora mesh, the gradient breathing, the presence pulse) AND this
    /// into their existing reduced-motion gate, so a tray-hidden or minimized process stops ticking
    /// them instead of animating for its whole life. NOT persisted: transient window state.
    /// </summary>
    [ObservableProperty]
    private bool _isWindowVisible = true;

    partial void OnIsWindowVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPresencePulse));
        UpdateRemoteDesktopStreamForeground();
    }

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
    private LayoutSettingsViewModel? _layoutSettingsViewModel;
    private RemoteDesktopViewModel? _remoteDesktopViewModel;
    private TaskManagerViewModel? _taskManagerViewModel;
    private AboutViewModel? _aboutViewModel;
    private FileTransferViewModel? _fileTransferViewModel;
    private DiagnosticLogsViewModel? _diagnosticLogsViewModel;

    [ObservableProperty]
    private Remex.Core.Models.CustomizationSettings _customization = new();

    // ═══════════════ Personalize sheet width (RemEx-vkkcq) ═══════════════

    /// <summary>Width the Personalize sheet opens at when nothing is stored (or after a reset).</summary>
    public const double DefaultPersonalizeSheetWidth = 520;

    /// <summary>
    /// Narrowest the sheet can be dragged. 440 is what the sheet was fixed at before it became
    /// resizable, so it is the width every tab header and control inside was already laid out for.
    /// </summary>
    public const double MinPersonalizeSheetWidth = 440;

    /// <summary>Widest the sheet may be, as a fraction of the shell's own width.</summary>
    private const double MaxPersonalizeSheetFraction = 0.6;

    /// <summary>
    /// Live width of the Personalize side sheet. Bound by ShellView.axaml to the SideSheet's
    /// SideSheetWidth AND to the width of the resize-grip Panel beside it, so the grip tracks the
    /// sheet edge on every drag frame. Owned here, not by <see cref="CustomizationViewModel"/>:
    /// the drag happens on the shell, before the sheet's content is involved, and the shell is the
    /// only thing that knows its own width to clamp against. Persisted in
    /// <see cref="Remex.Core.Models.CustomizationSettings.PersonalizeSheetWidth"/> - written from
    /// here on drag end, and carried by CustomizationViewModel.BuildCurrentSettings so a
    /// Personalize save does not wipe it.
    /// </summary>
    [ObservableProperty]
    private double _personalizeSheetWidth = DefaultPersonalizeSheetWidth;

    /// <summary>
    /// Clamps a requested sheet width to [<see cref="MinPersonalizeSheetWidth"/>, 60% of
    /// <paramref name="shellWidth"/>]. A shell width of 0 or less (not laid out yet) or a NaN request
    /// collapses to the floor rather than throwing or letting garbage through - both happen
    /// legitimately during startup, when a stored width is applied before the window has a size.
    /// </summary>
    public static double ClampPersonalizeSheetWidth(double requested, double shellWidth)
    {
        if (double.IsNaN(requested)) return MinPersonalizeSheetWidth;
        var max = double.IsFinite(shellWidth) && shellWidth > 0
            ? Math.Max(MinPersonalizeSheetWidth, shellWidth * MaxPersonalizeSheetFraction)
            : MinPersonalizeSheetWidth;
        return Math.Clamp(requested, MinPersonalizeSheetWidth, max);
    }

    /// <summary>
    /// Applies a drag frame: sets the clamped width without saving. Persisting per frame would
    /// queue a profile write on every pointer move; <see cref="CommitPersonalizeSheetWidth"/> does
    /// that once, on drag end.
    /// </summary>
    public void ResizePersonalizeSheet(double requested, double shellWidth) =>
        PersonalizeSheetWidth = ClampPersonalizeSheetWidth(requested, shellWidth);

    /// <summary>
    /// Persists the current width through the profile's only save path (debounced, atomic
    /// temp+move). Built as <c>CurrentProfile with { Customization = ... with { ... } }</c> so every
    /// other customization field carries through untouched.
    /// </summary>
    public void CommitPersonalizeSheetWidth()
    {
        var current = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
        var updated = current with
        {
            Customization = current.Customization with { PersonalizeSheetWidth = PersonalizeSheetWidth },
        };
        _layoutService.RequestSave(updated);
    }

    /// <summary>Double-click on the grip: back to <see cref="DefaultPersonalizeSheetWidth"/>, saved.</summary>
    public void ResetPersonalizeSheetWidth()
    {
        PersonalizeSheetWidth = DefaultPersonalizeSheetWidth;
        CommitPersonalizeSheetWidth();
    }

    /// <summary>
    /// What a stored (or absent) width means at load: null is the default, anything else is floored
    /// at the minimum. The 60% ceiling is NOT applied here - the view model has no window width at
    /// construction - ShellView's SizeChanged re-clamp applies it the moment the shell has a size.
    /// </summary>
    private static double SheetWidthFromSettings(Remex.Core.Models.CustomizationSettings settings)
    {
        var stored = settings.PersonalizeSheetWidth;
        return stored is { } w && double.IsFinite(w)
            ? Math.Max(MinPersonalizeSheetWidth, w)
            : DefaultPersonalizeSheetWidth;
    }

    /// <summary>The decoded wallpaper for the Wallpaper background mode, or null. Decoded once per
    /// path change on a worker thread and cached here; the blur is an Effect on the Image, so
    /// nothing re-renders per frame (spec section 9).</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _wallpaperBitmap;

    /// <summary><c>WallpaperBlur</c> mapped through <see cref="WallpaperBackdrop.BlurRadiusFor"/>.</summary>
    [ObservableProperty]
    private double _wallpaperBlurRadius;

    /// <summary>
    /// What the background control renders THIS SESSION: the setting, except that a Wallpaper
    /// mode whose file cannot be read renders Solid while the setting stays Wallpaper so the
    /// person can pick again (spec section 6).
    /// </summary>
    [ObservableProperty]
    private string _effectiveBackgroundType = "Aurora";

    private string? _wallpaperPathLoaded;
    private string? _wallpaperPathFailed;

    /// <summary>The path a decode is currently in flight for, or null. Guards against launching a
    /// second decode of the same path (e.g. a blur-only tick while the first decode is still
    /// running) — see <see cref="_wallpaperLoadGeneration"/> for the companion staleness guard.</summary>
    private string? _wallpaperPathLoading;

    /// <summary>Bumped every time a NEW decode is launched. A completing <see cref="LoadWallpaperAsync"/>
    /// compares its captured generation against this field and discards its result if it no longer
    /// matches — the only way to tell a stale decode from the winning one when two can be in flight
    /// at once (RemEx-8twk0.5): dragging the blur slider before the first decode lands, or switching
    /// background material while a slow decode is still running.</summary>
    private int _wallpaperLoadGeneration;

    /// <summary>Re-resolves the wallpaper for <paramref name="settings"/>. UI thread.</summary>
    private void RefreshWallpaperBackdrop(Remex.Core.Models.CustomizationSettings settings)
    {
        WallpaperBlurRadius = WallpaperBackdrop.BlurRadiusFor(settings.WallpaperBlur);

        if (settings.BackgroundMaterial != "Wallpaper")
        {
            EffectiveBackgroundType = settings.BackgroundMaterial;

            // P1-27: leaving Wallpaper mode used to leave the ~15MB decoded bitmap resident for the
            // rest of the session - it was only ever released on app shutdown (Dispose) or when
            // switching to a DIFFERENT wallpaper path. Deferred, not synchronous, for the same
            // reason the load path defers disposing a superseded bitmap (RemEx-8twk0.5): the Image
            // control may still be compositing a frame that references it this tick.
            if (WallpaperBitmap is { } stale)
            {
                WallpaperBitmap = null;
                Dispatcher.UIThread.Post(() => stale.Dispose(), DispatcherPriority.Background);
            }
            _wallpaperPathLoaded = null;

            return;
        }

        var path = WallpaperBackdrop.ResolvePath(settings, SystemSeedSources.TryGetWallpaperPath);
        if (path is null)
        {
            FailWallpaper(settings, path: null);
            return;
        }

        if (string.Equals(path, _wallpaperPathLoaded, StringComparison.OrdinalIgnoreCase) && WallpaperBitmap is not null)
        {
            EffectiveBackgroundType = "Wallpaper";
            return;
        }

        if (string.Equals(path, _wallpaperPathLoading, StringComparison.OrdinalIgnoreCase))
        {
            // Already decoding this exact path (e.g. the blur slider ticking while the first
            // decode is still running) — do not launch a duplicate decode for it.
            return;
        }

        _wallpaperPathLoading = path;
        var generation = ++_wallpaperLoadGeneration;
        _ = LoadWallpaperAsync(settings, path, generation);
    }

    private async Task LoadWallpaperAsync(Remex.Core.Models.CustomizationSettings settings, string path, int generation)
    {
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var codec = SkiaSharp.SKCodec.Create(path);
                if (codec is null) return null;
                using var stream = File.OpenRead(path);
                // A 4K desktop wallpaper is decoded at most 2560 wide; a picked image is already that size.
                return codec.Info.Width > WallpaperImageStore.MaxEdge
                    ? Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, WallpaperImageStore.MaxEdge)
                    : new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Wallpaper backdrop: could not decode '{path}' — {ex.Message}");
                return null;
            }
        });

        try
        {
            if (generation != _wallpaperLoadGeneration)
            {
                // Superseded by a newer request before this decode finished — the newer load owns
                // WallpaperBitmap (or is still loading it) now, so this result is discarded
                // untouched, including the in-flight/loaded/failed path bookkeeping below.
                bitmap?.Dispose();
                return;
            }

            if (string.Equals(_wallpaperPathLoading, path, StringComparison.OrdinalIgnoreCase))
                _wallpaperPathLoading = null;

            // The setting may have moved on while decoding; only the current one is honoured.
            if (Customization.BackgroundMaterial != "Wallpaper") { bitmap?.Dispose(); return; }

            if (bitmap is null)
            {
                FailWallpaper(settings, path);
                return;
            }

            var previous = WallpaperBitmap;
            WallpaperBitmap = bitmap;
            _wallpaperPathLoaded = path;
            _wallpaperPathFailed = null;
            EffectiveBackgroundType = "Wallpaper";

            // Deferred, not synchronous: the Image control may still be compositing a frame that
            // references `previous` this tick, so disposing it inline can hand the renderer an
            // already-disposed bitmap (RemEx-8twk0.5).
            if (previous is not null)
                Dispatcher.UIThread.Post(() => previous.Dispose(), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            // Everything above the decode itself (property setters, localization lookups, the
            // notification call inside FailWallpaper) can throw; unguarded, that faults this
            // fire-and-forget task unobserved. The decode failure path above still reaches
            // FailWallpaper — this only guards what happens after.
            System.Diagnostics.Trace.TraceWarning($"Wallpaper backdrop: post-decode handling failed for '{path}' — {ex.Message}");
        }
    }

    /// <summary>Solid for the session, one snackbar per failing path, the setting untouched.</summary>
    private void FailWallpaper(Remex.Core.Models.CustomizationSettings settings, string? path)
    {
        // A failure supersedes whatever decode is still in flight. Without this, switching to an
        // Image source whose copy is missing while the desktop picture is still decoding would let
        // that decode land afterwards, publish the desktop picture, and clear the failure — the
        // silent substitution WallpaperBackdrop.ResolvePath exists to forbid.
        _wallpaperLoadGeneration++;
        _wallpaperPathLoading = null;

        EffectiveBackgroundType = "Solid";
        var key = path ?? $"{settings.WallpaperSource}|{settings.WallpaperImagePath}";
        if (string.Equals(key, _wallpaperPathFailed, StringComparison.OrdinalIgnoreCase)) return;
        _wallpaperPathFailed = key;
        NotificationService.Instance.Notify(
            NotificationImportance.Outcome,
            LocalizationService.Instance["Custom_BgType_Wallpaper"],
            LocalizationService.Instance["Custom_WallpaperUnavailable"]);
    }

    /// <param name="transferQueuePost">
    /// The shared <see cref="FileTransferQueue"/>'s UI-thread marshaller (RemEx-rjnbo.1). Defaults to
    /// null, which <see cref="FileTransferQueue"/> itself turns into a real
    /// <see cref="Dispatcher.UIThread"/>.Post - correct in production, but this assembly has no
    /// <c>Avalonia.Headless</c> reference to pump one, so a test that wants to enqueue against
    /// <see cref="TransferQueueForTests"/> and observe the result synchronously passes
    /// <c>action =&gt; action()</c>, same shape as <c>FileTransferQueueTests.NewQueue</c>.
    /// </param>
    public ShellViewModel(DashboardLayoutService layoutService, ThemeService themeService, ConnectionViewModel connectionViewModel, IServiceProvider services, IImmersiveModeService? immersiveMode = null, Action<Action>? transferQueuePost = null)
    {
        _layoutService = Guard.NotNull(layoutService);
        _themeService = Guard.NotNull(themeService);
        Connection = Guard.NotNull(connectionViewModel);
        _services = Guard.NotNull(services);
        _immersiveMode = immersiveMode; // Intentionally optional

        _onCustomizationApplied = settings =>
        {
            Customization = settings;
            RefreshWallpaperBackdrop(settings);
            // Re-synced on every apply, not just at load, so a profile import or switch brings its
            // own sheet width with it. A save from the Personalize sheet itself round-trips the
            // live value (BuildCurrentSettings carries it), so this is a no-op in that case.
            PersonalizeSheetWidth = SheetWidthFromSettings(settings);
        };
        _themeService.CustomizationApplied += _onCustomizationApplied;
        if (_layoutService.CurrentProfile?.Customization != null)
        {
            Customization = _layoutService.CurrentProfile.Customization;
            RefreshWallpaperBackdrop(Customization);
            _personalizeSheetWidth = SheetWidthFromSettings(Customization);
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

        // A REPLACED PROFILE INVALIDATES THE CACHED PERSONALIZATION VM (RemEx-waqb4).
        // CustomizationViewModel snapshots every field off CurrentProfile.Customization once, in its
        // own constructor, and EnsureCustomizationVm's ??= never rebuilds it - so a savefile import
        // (DashboardLayoutService.ProfileReplaced) has to drop the cached instance itself, the same
        // way Dispose already does, so the next EnsureCustomizationVm call builds a fresh one against
        // the imported values instead of the next slider nudge writing the stale snapshot back over
        // the import. Raising CustomizationVm's own change notification (not an ObservableProperty -
        // it is a get-only property backed by the field) is what makes a bound, already-open
        // Personalize sheet pick up the fresh instance immediately, same as OnPresenceChanged above
        // re-raises ShowPresencePulse rather than waiting for an unrelated notification.
        //
        // MARSHALLED ONTO THE UI THREAD, NOT RUN INLINE (review, HIGH). ProfileReplaced is now only
        // raised by DashboardLayoutService.ReloadAsync, but that is still reachable off the UI thread
        // — RemexSavefileService's silent autosnapshot arms a bare System.Threading.Timer with no
        // SynchronizationContext, and its manual export can run from wherever the caller awaits it
        // from. Disposing the live, bound CustomizationViewModel and re-raising CustomizationVm from a
        // thread pool thread would touch bound UI state off-thread even though it can no longer
        // happen every 30 seconds. ProfileReplacedDispatch below is that check/post, same shape as
        // CanvasDashboardViewModel.ReloadFromPersistedLayout's.
        _onProfileReplaced = () => ProfileReplacedDispatch(() =>
        {
            _customizationViewModel?.Dispose();
            _customizationViewModel = null;
            OnPropertyChanged(nameof(CustomizationVm));
            // Review fix (round 1 MEDIUM): raised unconditionally, even when the sheet is closed -
            // a closed sheet's off-screen PersonalizationPanelView still has the disposed instance
            // as its DataContext until this fires, otherwise. Null is already the panel's normal
            // pre-first-open state, so rebinding to null here is not a visible change for the user,
            // just a correctness fix for what's sitting in memory bound to a disposed VM.
            OnPropertyChanged(nameof(CustomizationVmForSheetBinding));

            // P1-30: CustomizationVmForSheetBinding is non-building, so simply re-raising it here
            // (the way CustomizationVm's own notification above does) would set the bound
            // DataContext to null and leave it null - nothing else re-triggers EnsureCustomizationVm
            // for a sheet that's already open, since ToggleSettingsPanel/NavigateToCustomization only
            // run on the NEXT explicit open. Rebuild immediately, but only when the sheet is
            // genuinely open right now - a closed sheet still gets the fully lazy, deferred-until-
            // opened treatment this whole row exists for; only the "already open, must update now"
            // case (the reason RemEx-waqb4 exists at all) pays the eager rebuild.
            if (IsSettingsPanelOpen)
            {
                EnsureCustomizationVm();
            }
        });
        _layoutService.ProfileReplaced += _onProfileReplaced;

        // Initialize background/shared VMs
        //
        // GetRequiredService, not GetService ?? new: both are registered as singletons in
        // App.axaml.cs, and a `?? new` fallback here can silently split the instance a test container
        // built for itself from the one this constructor creates unnoticed — any other component that
        // resolves SensorAlertStore/SensorAlertTracker from the same container then talks to a
        // different alert store than the canvas does (RemEx-8wpvr.2, MEDIUM). A test building
        // ShellViewModel with a bare ServiceCollection must register both, same as it already does
        // for ILogger<FileTransferQueue> below.
        var alertStore = _services.GetRequiredService<SensorAlertStore>();
        _alertTracker = _services.GetRequiredService<SensorAlertTracker>();
        _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this, alertStore, _alertTracker);
        _ = _canvasViewModel.InitializeAsync();
        _canvasViewModel.SensorAlertFired += OnSensorAlertFired;

        // AlertBadgeCount MIRRORS THE TRACKER, IT DOES NOT COUNT (RemEx-8wpvr.3). Seeded from
        // whatever is already tripped (a fresh tracker is always empty, but a test or future caller
        // handing in one that is not should not have to wait for the next trip to see it), then kept
        // live on every TrippedChanged - a trip, an acknowledge, or acknowledge-all.
        AlertBadgeCount = _alertTracker.TrippedCount;
        _onTrippedChanged = () => AlertBadgeCount = _alertTracker.TrippedCount;
        _alertTracker.TrippedChanged += _onTrippedChanged;

        // Eager, not lazy like _fileTransferViewModel below - see _transferQueue's own remarks for why.
        _transferQueue = new FileTransferQueue(post: transferQueuePost, logger: _services.GetRequiredService<ILogger<FileTransferQueue>>());
        _transferQueue.Changed += OnTransferQueueChanged;

        // Same reasoning: an unread-diagnostics badge has to exist before the Logs page does, and
        // InMemoryLogSink is a process-wide static, so subscribing here (rather than in a lazily
        // built DiagnosticLogsViewModel) is what makes that possible.
        InMemoryLogSink.LogAdded += OnDiagnosticLogAdded;
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
    /// How the <see cref="DashboardLayoutService.ProfileReplaced"/> handler reaches the UI thread.
    /// Replaceable so it can be tested without a real, pumped dispatcher (RemEx-waqb4 review),
    /// the same reason <c>CanvasDashboardViewModel.Dispatch</c> exists.
    /// </summary>
    /// <remarks>
    /// THE LAMBDA IS NOT EVALUATED AT CONSTRUCTION, so building a <see cref="ShellViewModel"/> in a
    /// test never touches <see cref="Dispatcher.UIThread"/> on its own. That matters here specifically
    /// because this assembly has no <c>Avalonia.Headless</c> reference: nothing pumps a callback
    /// <c>Post</c> actually queues, and by the time
    /// a test's own awaited <c>DashboardLayoutService.ReloadAsync</c> resumes, its continuation can
    /// land on a different pool thread than whichever one a <c>DispatcherTimer</c>
    /// bound as "the" UI thread earlier in the same test — so
    /// <see cref="Dispatcher.UIThread"/>.CheckAccess() reads false and the real default would queue
    /// work nothing ever drains. A test sets this to run its argument inline instead.
    /// </remarks>
    internal Action<Action> ProfileReplacedDispatch { get; set; } = run =>
    {
        if (Dispatcher.UIThread.CheckAccess())
            run();
        else
            Dispatcher.UIThread.Post(run);
    };

    /// <summary>
    /// True when the drawer-footer connection button's presence badge should pulse (RemEx-d7xj8):
    /// only while a phone is actually attached, never when the user prefers reduced motion, and
    /// never while the window is hidden to the tray or minimized (<see cref="IsWindowVisible"/>).
    /// </summary>
    public bool ShowPresencePulse => Presence.IsPhoneAttached && !IsReducedMotion && IsWindowVisible;

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
        _layoutService.ProfileReplaced -= _onProfileReplaced;
        _alertTracker.TrippedChanged -= _onTrippedChanged;

        // Nulled rather than left for the view to unsubscribe alone (RemEx-8twk0.8 fix round, LOW) -
        // same pattern as ThemeService.CustomizationApplied - so a view that outlives Dispose() (or a
        // subscription this VM cannot see) cannot keep firing Restart against a torn-down splash.
        SplashReplayRequested = null;

        // No frame can still be compositing against this bitmap once the shell itself is going
        // away, so — unlike the live swap in LoadWallpaperAsync — disposing it inline here is safe.
        // The generation bump makes a decode that completes after this point discard its bitmap
        // instead of republishing one nothing would ever dispose.
        _wallpaperLoadGeneration++;
        _wallpaperPathLoading = null;
        WallpaperBitmap?.Dispose();
        WallpaperBitmap = null;

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

        // Disposed AFTER _fileTransferViewModel, which unsubscribes its own handlers from this same
        // instance but (RemEx-rjnbo.1) never disposes it - this queue is ShellViewModel's, not the
        // page's, to tear down.
        _transferQueue.Changed -= OnTransferQueueChanged;
        _transferQueue.Dispose();
        InMemoryLogSink.LogAdded -= OnDiagnosticLogAdded;

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

        var action = ResolveBootCompletion(
            _isSplashPreview,
            _layoutService.CurrentProfile?.HasCompletedTutorial ?? false);
        _isSplashPreview = false;

        if (action == BootCompletionAction.UnmountOnly)
        {
            _ = UnmountWelcomeSplashAsync();
            return;
        }

        // Show tutorial on first run after the splash fades
        if (action == BootCompletionAction.ShowTutorial)
        {
            TutorialPageIndex = 0;
            ShowTutorialOverlay = true;
        }
        _ = UnmountWelcomeSplashAsync();
    }

    /// <summary>
    /// What <see cref="OnBootSequenceCompleted"/> should do once the splash finishes. Public (not
    /// internal, unlike <see cref="ResolveBootCompletion"/> itself) only because a public xUnit
    /// [Theory] method cannot take an internal-typed parameter (CS0051) even under InternalsVisibleTo -
    /// the enum carries no logic worth hiding, so this is the cheaper fix over making the test
    /// non-public.
    /// </summary>
    public enum BootCompletionAction
    {
        /// <summary>A Preview replay finished; unmount the splash and never raise onboarding.</summary>
        UnmountOnly,

        /// <summary>A genuine first run; raise the first-run tutorial overlay.</summary>
        ShowTutorial,

        /// <summary>A genuine (non-preview) boot for a profile that already completed the tutorial.</summary>
        Normal,
    }

    /// <summary>
    /// Pure decision seam for <see cref="OnBootSequenceCompleted"/> (RemEx-8twk0.8 fix round, HIGH).
    /// <see cref="ShellViewModel"/> cannot be constructed headlessly for a unit test - it needs the
    /// full DI graph and a pumped dispatcher - so the branch that actually matters is extracted here
    /// where <c>SplashPreviewCompletionTests</c> can pin it directly.
    /// </summary>
    internal static BootCompletionAction ResolveBootCompletion(bool isSplashPreview, bool hasCompletedTutorial)
    {
        if (isSplashPreview)
            return BootCompletionAction.UnmountOnly;

        return hasCompletedTutorial ? BootCompletionAction.Normal : BootCompletionAction.ShowTutorial;
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

    private PlatformFlags? _tutorialPlatformOverride;

    /// <summary>
    /// Test-only seam (visible to <c>Remex.Desktop.Tests</c> via <c>InternalsVisibleTo</c>)
    /// overriding <see cref="CurrentTutorialPlatform"/>. Null in production, where the running OS
    /// decides; a test sets this so the platform-filtered paging can be exercised for Windows,
    /// Linux and Android alike without three CI runners.
    /// </summary>
    /// <remarks>
    /// Re-snaps <see cref="TutorialPageIndex"/> on assignment (RemEx-qgql review, LOW): flipping
    /// the platform can hide the page the carousel is currently sitting on (e.g. index 2 is
    /// Windows-only; overriding to Linux mid-test leaves the Carousel showing that slot while
    /// <see cref="IsTutorialFirstPage"/>/<see cref="IsTutorialLastPage"/> already answer for the
    /// new platform). Production never hits this - <see cref="CurrentTutorialPlatform"/> is fixed
    /// for the process's lifetime there - but a test author flipping the override mid-sequence
    /// deserves the same guarantee a real platform switch would get.
    /// </remarks>
    internal PlatformFlags? TutorialPlatformOverride
    {
        get => _tutorialPlatformOverride;
        set
        {
            _tutorialPlatformOverride = value;
            TutorialPageIndex = _tutorialPageIndex;
        }
    }

    /// <summary>The running platform, as the flag <see cref="_tutorialPages"/> filters pages by.</summary>
    private PlatformFlags CurrentTutorialPlatform =>
        TutorialPlatformOverride ??
        (OperatingSystem.IsWindows() ? PlatformFlags.Windows
       : OperatingSystem.IsLinux() ? PlatformFlags.Linux
       : PlatformFlags.Android);

    /// <summary>
    /// Advances to the next platform-visible page via <see cref="TutorialNavigator.Next"/>
    /// (RemEx-qgql) instead of hand-scanning <see cref="_tutorialPages"/> for the next raw index
    /// this platform supports - the scan and <see cref="TutorialNavigator.Next"/> compute the same
    /// answer, so there is no reason for this file to keep its own copy. A no-op on the last
    /// visible page, same as before: <see cref="TutorialNavigator.ClampPosition"/> holds position
    /// there rather than falling off the end.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTutorialNext))]
    public void TutorialNext() =>
        TutorialVisiblePageIndex = TutorialNavigator.Next(TutorialVisiblePageIndex, TutorialVisiblePageCount);

    /// <summary>
    /// False on the last platform-supported page - Finish/Done takes over there instead. Delegates
    /// to <see cref="IsTutorialLastPage"/> (RemEx-qgql) so the command's enabled state and the Next
    /// button's own <c>IsVisible</c> binding can never disagree about which page is last.
    /// </summary>
    private bool CanTutorialNext() => !IsTutorialLastPage;

    /// <summary>
    /// Retreats to the previous platform-visible page via <see cref="TutorialNavigator.Previous"/>
    /// (RemEx-qgql), for the same reason <see cref="TutorialNext"/> delegates to
    /// <see cref="TutorialNavigator.Next"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTutorialPrevious))]
    public void TutorialPrevious() =>
        TutorialVisiblePageIndex = TutorialNavigator.Previous(TutorialVisiblePageIndex, TutorialVisiblePageCount);

    /// <summary>
    /// False on the first platform-supported page. Delegates to <see cref="IsTutorialFirstPage"/>
    /// (RemEx-qgql) for the same reason <see cref="CanTutorialNext"/> delegates to
    /// <see cref="IsTutorialLastPage"/>.
    /// </summary>
    private bool CanTutorialPrevious() => !IsTutorialFirstPage;

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

    /// <summary>
    /// Fires on every notify-worthy trip (the tracker's own 60s per-sensor cooldown already
    /// filtered repeats - see <see cref="SensorAlertTracker.Trip(string, double, Remex.Core.Models.SensorAlert, DateTimeOffset)"/>).
    /// Keeps the notification-history insert this bead inherited from RemEx-rjnbo, and adds the
    /// actual tray/toast announcement (RemEx-8wpvr.3): everything before this bead recorded a trip
    /// but never told anyone. <c>internal</c> rather than <c>private</c> so
    /// <c>ShellAlertNotificationTests</c> can drive the importance mapping and text directly,
    /// without needing a real telemetry tick through <see cref="CanvasDashboardViewModel"/>.
    /// </summary>
    internal void OnSensorAlertFired(SensorAlert alert, double value)
    {
        AlertNotifications.Insert(0, new SensorAlertNotification(
            alert.SensorName, alert.Severity, DateTime.Now));
        // Cap the list at 20 entries
        while (AlertNotifications.Count > 20)
            AlertNotifications.RemoveAt(AlertNotifications.Count - 1);

        var catalog = (ISensorCatalog)_canvasViewModel!;
        var resolved = catalog.TryResolve(alert.SensorName, out var info) ? info : null;
        var displayName = resolved?.DisplayName ?? alert.SensorName;
        var unit = resolved?.Unit ?? string.Empty;
        var formattedReading = SensorReadingFormat.FormatReading(value, unit);
        var formattedThreshold = SensorReadingFormat.FormatReading(alert.Threshold, unit);

        var directionKey = $"{nameof(AlertDirection)}_{alert.Direction}";
        var severityKey = $"{nameof(AlertSeverity)}_{alert.Severity}";
        var direction = LocalizationService.Instance[directionKey];
        var severityText = LocalizationService.Instance[severityKey];

        var title = string.Format(
            LocalizationService.Instance["Alert_Notify_Title"], displayName, formattedReading);
        // The body states the THRESHOLD that was crossed, not the reading that crossed it -
        // "Alert: Above 90.0 °C" reads as a rule, not a restatement of the title's "92.4 °C hit".
        var body = string.Format(
            LocalizationService.Instance["Alert_Notify_Body"], direction, formattedThreshold, severityText);

        var importance = alert.Severity == AlertSeverity.Critical
            ? NotificationImportance.Problem
            : NotificationImportance.Outcome;
        NotificationService.Instance.Notify(importance, title, body);
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

        UpdateRemoteDesktopStreamForeground();
    }

    /// <summary>
    /// Perf audit P0-3: the Remote Desktop stream only runs while its page is the current view AND
    /// the window is shown and not minimized (<see cref="IsWindowVisible"/>). Leaving the page, or
    /// hiding the window to the tray, stops it; coming back resumes it. Navigation only swaps
    /// <see cref="CurrentView"/> and the view's detach only unsubscribes, so without this the
    /// heaviest client path kept receiving and decoding with nothing on screen.
    /// </summary>
    private void UpdateRemoteDesktopStreamForeground()
    {
        if (_remoteDesktopViewModel is null)
            return;

        var isForeground = IsWindowVisible && ReferenceEquals(CurrentView, _remoteDesktopViewModel);
        // Stop and Start both catch their own failures and report through the page's status line.
        _ = _remoteDesktopViewModel.SetStreamForegroundAsync(isForeground);
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

        // OPENING SENSORS NO LONGER ACKNOWLEDGES THE ALERTS (RemEx-8wpvr.3, superseding RemEx-rjnbo's
        // "arriving here IS having seen them"). The badge now means "how many sensors are still
        // tripped", not "how many times an alert fired while away" - clearing it here would hide a
        // sensor that is STILL over/under its threshold the moment the page opens, which is the one
        // time the count is most true. DismissAlerts (an explicit action) is what acknowledges a
        // trip now; merely looking at the canvas does not.
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
        _taskManagerViewModel ??= new TaskManagerViewModel(Connection, this);
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
        // degrade to no logger and discard every file-transfer failure diagnostic. transferQueue is
        // _transferQueue (RemEx-rjnbo.1), not a fresh one — queueLogger is only for the branch where
        // nobody supplies a queue, which is never true here, so it is left unset.
        _fileTransferViewModel ??= new FileTransferViewModel(
            Connection,
            _services.GetRequiredService<ILogger<FileTransferViewModel>>(),
            transferQueue: _transferQueue);
        SetTransitionAndNavigate(7, _fileTransferViewModel);
    }

    [RelayCommand]
    public void NavigateToDiagnosticLogs()
    {
        _diagnosticLogsViewModel ??= new DiagnosticLogsViewModel(this);
        // Arriving on the page is the acknowledgement (same shape as NavigateToCanvas clearing
        // AlertBadgeCount): whatever fired while the user was elsewhere no longer counts as unread.
        DiagnosticsBadgeCount = 0;
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
                _services.GetRequiredService<Remex.Desktop.Services.Backup.RemexSavefileService>(),
                // Same registered singleton the canvas and this shell instance already share
                // (RemEx-8wpvr.2's GetRequiredService comment above applies here too), so the
                // Settings alerts card can never talk to a different store than the canvas.
                _services.GetRequiredService<SensorAlertStore>(),
                _alertTracker,
                _canvasViewModel!);
            _ = _settingsViewModel.InitializeAsync();
            _lastSensorCardCount = _canvasViewModel?.Cards.Count(c => c.CardType == "Sensor") ?? -1;
            return;
        }

        // Only rebuild the sensor list when the canvas sensor card count changes, avoiding 50+
        // CollectionChanged events on every Settings panel open. Pinned sensors moved to
        // LayoutSettingsViewModel (RemEx-4kv0g.4.2) - this VM no longer has one of its own, so the
        // hook retargets there; null when the Personalize sheet has never been opened (the legacy
        // full Settings page reaches EnsureSettingsVm alone, never EnsureCustomizationVm), in which
        // case there is nothing to refresh yet and the fresh LayoutSettingsViewModel's own
        // InitializeAsync will read current cards when it is eventually constructed.
        var currentCount = _canvasViewModel?.Cards.Count(c => c.CardType == "Sensor") ?? -1;
        if (currentCount != _lastSensorCardCount)
        {
            _lastSensorCardCount = currentCount;
            _layoutSettingsViewModel?.RefreshSensors();
        }
    }

    private void EnsureCustomizationVm()
    {
        // Lazily constructed here, not at boot (RemEx-4kv0g.4.2) - the canvas already self-loads
        // IsSnapToGridEnabled/GridSize from its own profile load at construction
        // (CanvasDashboardViewModel.FinishInitialize), independent of any Settings/Layout VM, so
        // nothing at startup depends on this VM existing yet. Matches SettingsViewModel's own lazy
        // construction above.
        if (_layoutSettingsViewModel is null)
        {
            _layoutSettingsViewModel = new LayoutSettingsViewModel(
                _layoutService, _canvasViewModel, _services.GetService<HomeViewModel>());
            _ = _layoutSettingsViewModel.InitializeAsync();
        }

        // home/launcherStorage feed the Flyout section's Cards/Apps checklists (Flyout D2 .2,
        // RemEx-4kv0g.18.6) - both are registered AddSingleton in App.axaml.cs, the same DI surface
        // TrayFlyoutViewModel's own constructor injection already resolves them through. GetService,
        // NOT GetRequiredService: several existing tests build a ShellViewModel over a minimal
        // ServiceCollection that registers neither (ProfileReplacementInvalidatesCustomizationVmTests
        // among them) - CustomizationViewModel already treats both as optional, the same tolerant
        // shape _phoneThemeStore uses, so a missing registration here should mean "no Flyout
        // collaborator", not a crash opening Personalize.
        if (_customizationViewModel is null)
        {
            _customizationViewModel = new CustomizationViewModel(
                this, _layoutService, _themeService,
                _services.GetService<HomeViewModel>(),
                _services.GetService<ILauncherStorageService>(),
                _services.GetRequiredService<ILogger<CustomizationViewModel>>(),
                _layoutSettingsViewModel);

            // P1-30: the sheet's own binding target (CustomizationVmForSheetBinding, non-building)
            // only reflects a freshly-built VM once this notification fires - raised exactly here,
            // not on every EnsureCustomizationVm call, so a toggle-open that finds the VM already
            // built doesn't spam a redundant DataContext reassignment.
            OnPropertyChanged(nameof(CustomizationVmForSheetBinding));
        }
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

    /// <summary>
    /// P1-30: the Personalize sheet's own <c>SideSheetContent</c> is ALWAYS present in ShellView's
    /// visual tree (it just slides off-screen when closed, RemEx-zrlze), so a binding straight to
    /// <see cref="CustomizationVm"/> - a BUILDING getter - forced the full CustomizationViewModel
    /// (system-font enumeration, HctColorWheel's 26 tone discs) to construct at shell load, on the
    /// UI thread, even for a session that never opens the sheet. This getter is deliberately
    /// NON-building: it returns the already-constructed field, or null before the sheet has ever
    /// been opened this session. <see cref="ToggleSettingsPanel"/>/<see cref="NavigateToCustomization"/>
    /// already call <see cref="EnsureCustomizationVm"/> explicitly on open, and that method raises
    /// this property's own change notification the moment it actually builds one - including the
    /// rebuild <see cref="_onProfileReplaced"/> triggers for an already-open sheet - so the bound
    /// DataContext picks up the real VM the instant it exists, without ever forcing its own
    /// construction through the binding itself.
    /// </summary>
    public CustomizationViewModel? CustomizationVmForSheetBinding => _customizationViewModel;

    /// <summary>
    /// The Layout VM (snap-to-grid, grid size, pinned sensors; RemEx-4kv0g.4.2), if the Personalize
    /// sheet has been opened at least once this session. Unlike <see cref="CustomizationVm"/> and
    /// <see cref="SettingsVm"/> this does NOT lazily construct one — <c>SettingsViewModel</c>'s
    /// savefile-import path reads this only to refresh an already-open sheet; a sheet that was never
    /// opened has nothing on screen to go stale, and its eventual construction reads the current
    /// profile on its own.
    /// </summary>
    public LayoutSettingsViewModel? LayoutSettingsVm => _layoutSettingsViewModel;
}

/// <summary>A single entry in the shell's sensor-alert notification feed.</summary>
public sealed record SensorAlertNotification(string SensorName, AlertSeverity Severity, DateTime Time);
