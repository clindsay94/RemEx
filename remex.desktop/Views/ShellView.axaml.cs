using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using Material.Styles.Controls;
using Material.Styles.Models;
using Remex.Desktop.Controls;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class ShellView : UserControl
{
    /// <summary>
    /// Name of the shell's <see cref="SnackbarHost"/> (RemEx-uedna). ShellView.axaml's HostName
    /// attribute is a plain literal that must match this - not <c>x:Static</c>, which XamlIl cannot
    /// resolve against a static member of the very class its own code-behind partial is compiling
    /// (AVLN2000, measured). <c>ShellSnackbarHostTests</c> pins both ends against drift instead.
    /// </summary>
    internal const string ShellSnackbarHostName = "ShellSnackbar";

    /// <summary>
    /// How long a page transition runs. Material's figure for a shared-axis transition, and the
    /// upper end of what the shell can afford: this fires on every navigation, so a slow one makes
    /// the whole app feel slow.
    /// </summary>
    /// <remarks>
    /// It used to be shorter for a defensive reason — the window in which a second navigation could
    /// interrupt the first was exactly this duration, and an interrupted transition left the content
    /// area blank (RemEx-yj3x2). <see cref="PageHostSequencer"/> removed interruption rather than
    /// shortening the window it happens in, so the duration is free to be chosen for how it looks
    /// again (RemEx-yzu5m).
    /// </remarks>
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long to wait for a transition to report itself finished before assuming it never will.
    /// </summary>
    /// <remarks>
    /// <c>TransitionCompleted</c> is raised from the transition's continuation, which only exists if
    /// <c>ArrangeOverride</c> ran — and a collapsed host is never arranged. That is not hypothetical:
    /// the whole nav rail and its content host collapse whenever the shell switches to the fullscreen
    /// remote desktop. Without this the sequencer would stay busy forever and every later navigation
    /// would be swallowed. Four times the transition's own duration leaves plenty of headroom on a
    /// loaded machine while still recovering well inside a human pause between clicks.
    /// </remarks>
    private static readonly TimeSpan TransitionWatchdog = TransitionDuration * 4;

    private readonly PageHostSequencer _pageSequencer = new();

    private TransitioningContentControl? _pageHost;
    private TransitioningContentControl? _immersiveHost;
    private DispatcherTimer? _pageHostWatchdog;
    private ListBox? _navList;

    // RemEx-ddk6b: overlay focus management. _drawerContentRoot/_settingsSideSheet are the two
    // overlays' focus scopes (KeyboardNavigation.TabNavigation="Cycle" traps Tab inside each in
    // XAML); _drawerToggle/_gearFab/_settingsSheetCloseButton are focus targets - the fallback
    // restore target and, for the sheet, the move-in target. _drawerInvoker/_sheetInvoker hold
    // whatever had focus right before each overlay opened, so OnOverlayToggled can give it back.
    private Border? _drawerContentRoot;
    private Button? _drawerToggle;
    private FloatingButton? _gearFab;
    private SideSheet? _settingsSideSheet;
    private Button? _settingsSheetCloseButton;
    private IInputElement? _drawerInvoker;
    private IInputElement? _sheetInvoker;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Arms the drawer nav's entrance (RemEx-alwfa.2 slice 2) the first time the drawer opens,
    /// same gate as HomeView's dashboard (RemEx-dnfq0).
    /// </summary>
    /// <remarks>
    /// Armed on the first OPEN rather than in <c>OnAttachedToVisualTree</c>: the overlay drawer is
    /// closed at launch (RemEx-q3mle), so an entrance started at attach would play inside the
    /// closed drawer where nobody sees it and consume the once-per-process slot for nothing
    /// (gate review of 77ba309). Called from the <c>IsDrawerOpen</c> branch of
    /// <see cref="OnViewModelPropertyChanged"/>; the gate itself makes every later open a no-op.
    /// Known edge (review of 77ba309, LOW): a view model whose drawer is ALREADY open when it is
    /// assigned raises no IsDrawerOpen change, so that first open never arms. Unreachable today -
    /// IsDrawerOpen defaults to false and nothing opens it before the view is up - and the
    /// entrance simply plays on the next open. The stagger runs alongside the pane's own 300 ms
    /// slide rather than after it: delaying the class would let the items paint at full opacity
    /// and then snap to 0 when FillMode="Backward" lands, which reads far worse than overlap.
    /// The gate key is the literal "ShellNav", not <c>nameof(ShellView)</c> - ShellView has no
    /// section stack of its own to animate, and a distinct literal key keeps this slot from ever
    /// being shared with a future per-view gate that happens to use the class name.
    /// </remarks>
    private void ArmNavEntranceOnFirstOpen(ShellViewModel vm)
    {
        if (vm.IsDrawerOpen && StaggeredEntrance.ShouldPlay("ShellNav", vm.IsReducedMotion))
        {
            this.FindControl<ListBox>("NavList")?.Classes.Add(StaggeredEntrance.Class);
        }
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _pageHost = this.FindControl<TransitioningContentControl>("PageHost");
        _immersiveHost = this.FindControl<TransitioningContentControl>("ImmersiveHost");
        _navList = this.FindControl<ListBox>("NavList");
        _drawerContentRoot = this.FindControl<Border>("DrawerContentRoot");
        _drawerToggle = this.FindControl<Button>("DrawerToggle");
        _gearFab = this.FindControl<FloatingButton>("GearFab");
        _settingsSideSheet = this.FindControl<SideSheet>("SettingsSideSheet");
        _settingsSheetCloseButton = this.FindControl<Button>("SettingsSheetCloseButton");

        // The XAML sets a plain CrossFade to keep the designer honest; the guarded equivalent is
        // installed here before the first navigation can reach either host. Sequencing keeps the main
        // host from being interrupted at all, so this is now a backstop for the paths that can still
        // cancel - the watchdog flush, and the immersive host, which is still bound straight to
        // CurrentView and animates on every navigation whether or not it is on screen.
        if (_pageHost != null)
            _pageHost.PageTransition = NewPageTransition(reducedMotion: false);

        if (_immersiveHost != null)
            _immersiveHost.PageTransition = new InterruptSafePageTransition(new CrossFade(TransitionDuration));

        // Guarded because OnLoaded runs again on every reattach to the visual tree, the same reason
        // the toast host and boot splash below are guarded.
        if (!_pageHostSequenced && _pageHost != null)
        {
            _pageHostSequenced = true;

            // Normal priority, not the DispatcherTimer default of Background: recovery from a
            // transition that never reports itself finished must not be starved behind whatever
            // else the UI thread is doing, or the interval stops being a bound on how long a
            // navigation can be swallowed.
            _pageHostWatchdog = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TransitionWatchdog };
            _pageHostWatchdog.Tick += (_, _) => FlushPageHost();

            // POSTED, NOT CALLED. TransitioningContentControl raises TransitionCompleted from its
            // continuation and only then calls HideOldPresenter, which resolves which presenter to
            // hide from a flag that assigning Content flips. Flushing synchronously from this
            // handler therefore assigns the next page first and makes HideOldPresenter hide and
            // blank the presenter that is the outgoing half of the transition just starting: on a
            // burst of clicks the old page vanishes on frame one instead of sliding out, and the
            // content area is empty until the incoming half fades up. One dispatcher turn puts the
            // hide back in front of the assignment.
            _pageHost.TransitionCompleted += (_, _) => Dispatcher.UIThread.Post(FlushPageHost);

            // A running DispatcherTimer is rooted by the dispatcher and its Tick handler holds this
            // control alive, so it has to be stopped on final teardown - the same hook and the same
            // reason as the boot splash below (RemEx-wcte). Without it a tick can land after the
            // window has closed and materialise a view during shutdown.
            if (TopLevel.GetTopLevel(this) is Window pageHostOwner)
                pageHostOwner.Closed += (_, _) => _pageHostWatchdog?.Stop();
        }

        if (DataContext is ShellViewModel vm)
        {
            ApplyPageTransition(vm);

            // PageHost.Content is deliberately unbound in XAML - see PageHostSequencer.
            RequestPageView(vm.CurrentView);
            vm.BeginWelcomeSplash();

            // Seeds the nav list's highlight to match ActiveNavIndex on first load, same reasoning
            // as ResyncNavListSelection's IsDrawerOpen hook below - belt and braces for whatever
            // order attach and DataContext assignment happen to land in.
            ResyncNavListSelection();
        }

        // The in-app toast host. Guarded for the same reason the boot splash below is: OnLoaded runs
        // again on every reattach to the visual tree, and installing a second sink would just be
        // redundant work, not a second overlay layer - the SnackbarHost itself lives once in this
        // view's XAML and registers itself by name in Material's own static dictionary.
        if (!_toastHostInstalled)
        {
            _toastHostInstalled = true;
            NotificationService.Instance.InApp = new SnackbarToastSink();
        }

        _bootSplash = this.FindControl<Controls.Splash.SkiaSplashControl>("BootSplash");
        if (_bootSplash != null && !_bootSplashHooked)
        {
            // Guarded because OnLoaded runs again on every reattach to the visual tree. Unguarded,
            // each reattach added another SequenceCompleted handler and OnBootSequenceCompleted then
            // ran once per attach - the same subscribe-without-unsubscribe shape RemEx-wcte fixed
            // elsewhere in this file's neighbours.
            _bootSplashHooked = true;

            _bootSplash.SequenceCompleted += () =>
            {
                if (DataContext is ShellViewModel vm2)
                    vm2.OnBootSequenceCompleted();
            };

            // Covers the common case where DataContext is already assigned by the time _bootSplash is
            // found. RemEx-8twk0.8 fix round, MEDIUM: this used to be the ONLY place this subscription
            // was attempted, gated behind the same _bootSplashHooked latch as everything above - so a
            // DataContext that arrived after this OnLoaded pass left Preview a permanent silent no-op,
            // exactly like RequestPageView's dual-hook comment below (OnDataContextChanged) describes
            // for PageHost.Content. OnDataContextChanged now mirrors that: it (re)subscribes for every
            // VM change from here on, so a DataContext arriving later is no longer missed.
            if (DataContext is ShellViewModel splashOwner)
                splashOwner.SplashReplayRequested += _bootSplash.Restart;

            // SkiaSplashControl.Dispose detaches the DispatcherTimer's Tick handler, without which the
            // timer keeps a strong reference back into the control forever. It must be called on FINAL
            // teardown only: OnDetachedFromVisualTree deliberately calls Stop() and not Dispose(),
            // because the control can be reattached and OnAttachedToVisualTree resumes ticking through
            // that same subscription. Disposing on unload would leave a reattached splash frozen, so the
            // owning window's Closed event is the correct hook - it fires once, and only when the
            // control genuinely will not come back. (RemEx-wcte: the Dispose existed but nothing called
            // it, which made the fix dead code.)
            if (TopLevel.GetTopLevel(this) is Window owner)
                owner.Closed += (_, _) =>
                {
                    // RemEx-8twk0.8 fix round, LOW: unsubscribed on final teardown rather than left for
                    // the VM's own Dispose() to null out alone - the VM may outlive this view briefly,
                    // or may never be Disposed in some hosting path, so both ends let go independently.
                    if (DataContext is ShellViewModel currentVm)
                        currentVm.SplashReplayRequested -= _bootSplash.Restart;
                    _bootSplash.Dispose();
                };
        }
    }

    private bool _bootSplashHooked;
    private Controls.Splash.SkiaSplashControl? _bootSplash;
    private bool _toastHostInstalled;
    private bool _pageHostSequenced;

    /// <summary>
    /// Re-seeds the imperative half of this view after a XAML hot reload (RemEx-1us2w).
    /// </summary>
    /// <remarks>
    /// <para>
    /// DEVELOPMENT-ONLY, and dead code in a Release build — nothing in this repo calls it. HotAvalonia
    /// discovers it BY NAME: any parameterless instance method called <c>InitializeComponentState</c>
    /// is re-run on a reload. Named rather than attributed on purpose, so this file takes no
    /// compile-time dependency on the <c>HotAvalonia</c> package, which is
    /// <c>PrivateAssets="All"</c> and gated to Debug — a <c>[AvaloniaHotReload]</c> attribute here
    /// would be a Release compile error the day someone builds without it.
    /// </para>
    /// <para>
    /// WHY IT IS NEEDED AT ALL. A hot reload rebuilds this control's visual tree without re-running
    /// the constructor, so <see cref="_pageHost"/>, <see cref="_immersiveHost"/> and
    /// <see cref="_navList"/> are left pointing at the OLD controls, which are no longer in the tree.
    /// <c>PageHost.Content</c> is deliberately unbound in XAML (see <see cref="PageHostSequencer"/>),
    /// so the fresh host starts empty and nothing ever fills it: every reload of ShellView.axaml
    /// produced a shell with working chrome and a blank content area. That is a hot-reload artifact,
    /// not the RemEx-b8dxy covered-shell bug, but it looks identical from the outside — which is
    /// exactly why it is worth a named method and this comment instead of a silent re-resolve.
    /// </para>
    /// <para>
    /// The <c>_pageHostSequenced</c> / <c>_toastHostInstalled</c> / <c>_bootSplashHooked</c> guards
    /// stay SET. They exist to keep reattach from installing a second watchdog timer or a second
    /// toast sink, and a reload is a reattach as far as those are concerned. What does have to be
    /// redone is the part bound to specific control instances: the transitions and the
    /// TransitionCompleted subscription live on controls that just got replaced.
    /// </para>
    /// </remarks>
    private void InitializeComponentState()
    {
        _pageHost = this.FindControl<TransitioningContentControl>("PageHost");
        _immersiveHost = this.FindControl<TransitioningContentControl>("ImmersiveHost");
        _navList = this.FindControl<ListBox>("NavList");
        _drawerContentRoot = this.FindControl<Border>("DrawerContentRoot");
        _drawerToggle = this.FindControl<Button>("DrawerToggle");
        _gearFab = this.FindControl<FloatingButton>("GearFab");
        _settingsSideSheet = this.FindControl<SideSheet>("SettingsSideSheet");
        _settingsSheetCloseButton = this.FindControl<Button>("SettingsSheetCloseButton");

        if (_pageHost != null)
        {
            _pageHost.PageTransition = NewPageTransition(reducedMotion: false);

            // Re-subscribed, not guarded: the previous subscription is on the control this one
            // replaced, so it can never fire again. Posted for the same HideOldPresenter ordering
            // reason OnLoaded documents.
            _pageHost.TransitionCompleted += (_, _) => Dispatcher.UIThread.Post(FlushPageHost);
        }

        if (_immersiveHost != null)
            _immersiveHost.PageTransition = new InterruptSafePageTransition(new CrossFade(TransitionDuration));

        if (DataContext is ShellViewModel vm)
        {
            ApplyPageTransition(vm);
            RequestPageView(vm.CurrentView);
            ResyncNavListSelection();
        }
    }

    /// <summary>
    /// Routes a navigation through <see cref="PageHostSequencer"/> instead of straight at the host.
    /// </summary>
    private void RequestPageView(object? view)
    {
        if (_pageHost != null && _pageSequencer.RequestShow(view))
        {
            AssignPageView(view);
        }
    }

    /// <summary>Hands the host the page the sequencer has released, and arms the watchdog.</summary>
    private void AssignPageView(object? view)
    {
        if (_pageHost == null)
        {
            return;
        }

        if (ReferenceEquals(_pageHost.Content, view))
        {
            // Assigning the same instance raises no change notification, so no transition starts and
            // no completion is ever reported. Settle it here rather than making the watchdog untangle
            // a navigation that had nothing to do.
            FlushPageHost();
            return;
        }

        _pageHostWatchdog?.Stop();
        _pageHostWatchdog?.Start();
        _pageHost.Content = view;
    }

    /// <summary>Releases whatever navigation was held back while the last transition ran.</summary>
    private void FlushPageHost()
    {
        _pageHostWatchdog?.Stop();

        if (_pageSequencer.RequestFlush(out var queued))
        {
            AssignPageView(queued);
        }
    }

    /// <summary>
    /// Adapts the shell's Material <see cref="SnackbarHost"/> to the notification service's sink
    /// (RemEx-uedna), replacing the Avalonia <c>WindowNotificationManager</c> /
    /// <c>NotificationCard</c> toast it used to wrap.
    /// </summary>
    /// <remarks>
    /// A snackbar's default content template is a bare string with no notion of severity, so
    /// <see cref="NotificationImportance"/> is rendered here as an icon + theme colour instead of
    /// relying on Material's own template - losing that distinction would undo exactly what the
    /// retired <c>NotificationCard</c> style existed to add. Built fresh per call rather than cached,
    /// per <see cref="ThemeResources"/>'s own rule: each toast is short-lived, so there is no live
    /// control sitting around to go stale across a theme switch.
    /// </remarks>
    private sealed class SnackbarToastSink : IInAppNotificationSink
    {
        /// <summary>How long a toast stays up before it times out on its own.</summary>
        private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(5);

        public void Show(NotificationImportance importance, string title, string message)
        {
            var (icon, brushKey) = SnackbarSeverityMapping.For(importance);
            var iconBrush = ThemeResources.Brush(brushKey, FallbackBrush(importance));
            var textBrush = ThemeResources.Brush("TextPrimaryBrush", Brushes.White);

            // Title and message are two TextBlocks, not one flattened "$title — $message" string.
            // Flattening them meant TextWrapping.Wrap + MaxLines=2 could truncate mid-message with no
            // visual sign anything was cut - a real transfer-failure toast ("Transfer failed" /
            // "<long path>: access denied") lost exactly the ": access denied" half, leaving a red
            // icon and a bare filename. Each line now gets its own TextTrimming.CharacterEllipsis, so
            // an overflow is visibly "…" rather than silently absent, and the title stays legible even
            // when the message alone would have exceeded two lines.
            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = textBrush,
                        FontWeight = FontWeight.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            };

            if (!string.IsNullOrEmpty(message))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = message,
                    Foreground = textBrush,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new MaterialIcon
                    {
                        Kind = icon,
                        Width = 20,
                        Height = 20,
                        Foreground = iconBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    textStack,
                },
            };

            SnackbarHost.Post(new SnackbarModel(content, ToastDuration), ShellSnackbarHostName, DispatcherPriority.Normal);
        }

        /// <summary>
        /// A fixed colour to fall back to if the theme key is somehow absent, so a lookup miss still
        /// distinguishes severity by hue rather than rendering every toast identically grey.
        /// </summary>
        private static IBrush FallbackBrush(NotificationImportance importance) => importance switch
        {
            NotificationImportance.Problem => Brushes.IndianRed,
            NotificationImportance.Outcome => Brushes.MediumSeaGreen,
            _ => Brushes.Gray,
        };
    }

    private ShellViewModel? _previousVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_previousVm != null)
        {
            _previousVm.PropertyChanged -= OnViewModelPropertyChanged;

            // RemEx-8twk0.8 fix round, MEDIUM/LOW: mirrors the RequestPageView dual-hook below so a
            // DataContext that arrives (or changes) after OnLoaded's own hook attempt is still wired
            // up, and so the previous VM's SplashReplayRequested is let go on every VM change - not
            // just at final teardown.
            if (_bootSplash != null)
                _previousVm.SplashReplayRequested -= _bootSplash.Restart;
        }

        if (DataContext is ShellViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            _previousVm = vm;

            if (_bootSplash != null)
                vm.SplashReplayRequested += _bootSplash.Restart;

            // Seeded here as well as in OnLoaded because PageHost.Content is no longer bound. The
            // binding used to make the order of "attach" and "assign the DataContext" irrelevant;
            // without it, a view model that arrives after OnLoaded would leave the content area
            // blank until the user happened to navigate. AssignPageView's ReferenceEquals guard
            // makes the redundant call a no-op when the order is the usual one.
            RequestPageView(vm.CurrentView);
        }
        else
        {
            _previousVm = null;
        }
    }

    /// <summary>
    /// Moves focus into the drawer or the Personalize side sheet when it opens, and hands it back
    /// when it closes (RemEx-ddk6b). Shared by both overlays from <see cref="OnViewModelPropertyChanged"/>
    /// - <paramref name="overlayRoot"/>, <paramref name="invoker"/>, <paramref name="firstTarget"/> and
    /// <paramref name="fallback"/> are the only things that differ between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The move-in is POSTED, not called synchronously: <paramref name="overlayRoot"/> is not laid
    /// out the instant the bound property flips - the slide animation and its content are still
    /// coming together - so focusing <paramref name="firstTarget"/> synchronously would often find
    /// nothing focusable there yet.
    /// </para>
    /// <para>
    /// The restore path never steals focus from a page. It only reassigns focus when the currently
    /// focused element is null or sits inside the overlay that just closed
    /// (<c>Visual.IsVisualAncestorOf</c>) - <c>RemoteDesktopView</c> calls
    /// <c>this.Focus()</c> on itself, and that has to survive some OTHER overlay closing untouched.
    /// The restored target is <paramref name="invoker"/> only while it is still effectively visible
    /// (a nav destination can navigate the invoker off screen while the overlay is up); otherwise
    /// <paramref name="fallback"/>.
    /// </para>
    /// <para>
    /// Ordering note: <c>ShellViewModel.OnIsDrawerOpenChanged</c>/<c>OnIsSettingsPanelOpenChanged</c>
    /// close whichever overlay is not the one just opened, and they do it from inside the OPENING
    /// property's own generated setter - before that setter raises its own <c>PropertyChanged</c>.
    /// So opening the drawer while the sheet is open runs the closing sheet's synchronous restore
    /// FIRST (nested inside the drawer's PropertyChanged.Invoke), and only then reaches the drawer's
    /// own branch below, which captures whatever the restore just focused as the new invoker. The
    /// drawer's posted move-in still wins once it runs, so what lands on screen is correct either
    /// way; this only affects what the two invoker fields see in between.
    /// </para>
    /// </remarks>
    private void OnOverlayToggled(
        bool opened,
        Visual overlayRoot,
        ref IInputElement? invoker,
        Func<IInputElement?> firstTarget,
        IInputElement? fallback)
    {
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;

        if (opened)
        {
            invoker = focusManager?.GetFocusedElement();
            // Posted because the overlay's content is not laid out synchronously on open. The
            // callback re-checks the overlay is still showing: an open-then-close inside one
            // dispatcher turn would otherwise pull focus into a closed drawer (review, MEDIUM).
            Dispatcher.UIThread.Post(() =>
            {
                if (!overlayRoot.IsEffectivelyVisible) return;
                (firstTarget() as InputElement)?.Focus(NavigationMethod.Directional);
            });
            return;
        }

        var focused = focusManager?.GetFocusedElement();
        var focusIsInsideClosingOverlay = focused is Visual focusedVisual && overlayRoot.IsVisualAncestorOf(focusedVisual);

        if (focused == null || focusIsInsideClosingOverlay)
        {
            var restoreTarget = invoker is Visual { IsEffectivelyVisible: true } ? invoker : fallback;
            (restoreTarget as InputElement)?.Focus(NavigationMethod.Unspecified);
        }

        invoker = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsSettingsPanelOpen needs no handler here for the slide/scrim itself (RemEx-zrlze) - that
        // is bound straight to material:SideSheet's own SideSheetOpened instead of a hand-toggled
        // "open" class. It does need the focus-management branch further down (RemEx-ddk6b).

        if (e.PropertyName == nameof(ShellViewModel.CurrentView) && sender is ShellViewModel navVm)
        {
            RequestPageView(navVm.CurrentView);
        }

        // RemEx-zi3ua (review round 2, MEDIUM): the nav list's :selected highlight now does two
        // jobs - keyboard cursor AND "you are here" - and arrow keys move only the first of them
        // (see the comment on OnNavItemTapped). Nothing else resyncs the two, so a user who arrows
        // to a different item and then leaves without committing (Escape, or just closing the
        // drawer another way) strands the highlight on the wrong destination, and it stays wrong
        // even after navigating BACK to the truly-active page - ActiveNavIndex's setter is
        // equality-gated, so returning to where you logically already are raises no
        // PropertyChanged and the one-way IsSelected binding never re-fires. Re-asserting the
        // selection on every IsDrawerOpen flip (both open and close) is cheap - a no-op if nothing
        // drifted - and covers the failure whichever way the user leaves the list uncommitted.
        if (e.PropertyName == nameof(ShellViewModel.IsDrawerOpen))
        {
            ResyncNavListSelection();
            if (sender is ShellViewModel drawerVm)
            {
                ArmNavEntranceOnFirstOpen(drawerVm);

                if (_drawerContentRoot != null)
                {
                    OnOverlayToggled(
                        drawerVm.IsDrawerOpen,
                        _drawerContentRoot,
                        ref _drawerInvoker,
                        () => _navList?.SelectedItem as ListBoxItem ?? _navList?.Items.OfType<ListBoxItem>().FirstOrDefault(),
                        _drawerToggle);
                }
            }
        }

        // RemEx-ddk6b: the Personalize side sheet gets the same open/trap/restore focus handling as
        // the drawer above - see OnOverlayToggled's remarks for why this and the IsDrawerOpen branch
        // can each observe the OTHER overlay's synchronous restore before their own runs.
        if (e.PropertyName == nameof(ShellViewModel.IsSettingsPanelOpen) &&
            sender is ShellViewModel sheetVm && _settingsSideSheet != null)
        {
            OnOverlayToggled(
                sheetVm.IsSettingsPanelOpen,
                _settingsSideSheet,
                ref _sheetInvoker,
                () => _settingsSheetCloseButton,
                _gearFab);
        }

        // The direction only raises a notification when it actually changes, which is correct here:
        // two navigations the same way down the sidebar want the same transition, and the one
        // already installed is it.
        if ((e.PropertyName == nameof(ShellViewModel.TransitionDirection) ||
             e.PropertyName == nameof(ShellViewModel.IsReducedMotion)) &&
            sender is ShellViewModel vm)
        {
            ApplyPageTransition(vm);
        }
    }

    /// <summary>
    /// Installs the transition for the navigation about to happen, and points it the right way.
    /// </summary>
    /// <remarks>
    /// Every transition is wrapped in <see cref="InterruptSafePageTransition"/>. Unwrapped, a
    /// navigation that lands while the previous one is still animating freezes a content presenter
    /// part-way through its animation and the incoming page never becomes visible (RemEx-yj3x2).
    /// </remarks>
    private void ApplyPageTransition(ShellViewModel vm)
    {
        if (_pageHost == null)
        {
            return;
        }

        // The former IsAndroid early-return was unreachable here (RemEx-f167).
        _pageHost.IsTransitionReversed = vm.TransitionDirection < 0;
        _pageHost.PageTransition = NewPageTransition(vm.IsReducedMotion);
    }

    /// <summary>
    /// Builds the shell's page transition: Material's shared axis, or a plain cross-fade for anyone
    /// who has asked for reduced motion.
    /// </summary>
    /// <remarks>
    /// Avalonia exposes no system reduced-motion setting, so this follows the app's own preference,
    /// which now has a switch alongside the other personalisation toggles. Reduced motion means no
    /// travel at all rather than less of it — a shortened slide is still a slide — so the fade is
    /// what is left, at half the duration since there is nothing to follow across the screen.
    /// </remarks>
    internal static IPageTransition NewPageTransition(bool reducedMotion) =>
        new InterruptSafePageTransition(reducedMotion
            ? new CrossFade(TransitionDuration / 2)
            : new SharedAxisPageTransition(TransitionDuration));

    /// <summary>
    /// Commits navigation for a nav-list destination on pointer/touch activation (RemEx-zi3ua).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT wired to <c>ListBox.SelectionChanged</c>. Avalonia moves SELECTION on arrow
    /// keys too (<c>ListBox.OnKeyDown</c> calls <c>MoveSelection</c> for any directional key,
    /// confirmed against Avalonia 12.1.1's own source), so a <c>SelectionChanged</c> handler cannot
    /// tell an arrow-key highlight move from a genuine activation — treating both as "navigate" ran
    /// a real navigation (alert-badge clear, disconnected toast, lazy VM construction) on every arrow
    /// press and closed the drawer out from under a user who had only pressed Down once. Tapped and
    /// Enter/Space (<see cref="OnNavItemKeyDown"/>) are what Avalonia itself reserves for "commit" on
    /// a <c>ListBoxItem</c>, so each gets its own explicit handler instead, and arrow keys are left to
    /// move only the highlight.
    ///
    /// Runs on every activation, INCLUDING re-activating the already-active destination — unlike the
    /// retired <c>SelectionChanged</c>-driven design, there is no "did the index change" guard here,
    /// matching the nine Buttons this replaced: clicking "Home" while already on Home still dismisses
    /// the drawer (<c>IsDrawerOpen = false</c> lives inside <c>NavigateToHome</c>), and re-clicking
    /// "Sensors" still clears the alert badge.
    /// </remarks>
    private void OnNavItemTapped(object? sender, TappedEventArgs e) => ActivateNavItem(sender);

    /// <summary>
    /// Commits navigation for a nav-list destination on Enter/Space (RemEx-zi3ua).
    /// </summary>
    /// <remarks>
    /// Arrow keys are deliberately NOT handled here — <c>ListBox.OnKeyDown</c> already moves the
    /// highlight for those (see <see cref="OnNavItemTapped"/>), and this handler only reacts to the
    /// two keys Avalonia's own <c>ListBoxItem.OnKeyDown</c>/<c>ItemSelectionEventTriggers
    /// .ShouldTriggerSelection</c> reserve for "activate the focused item" — confirmed against
    /// Avalonia 12.1.1's own source, not assumed.
    /// </remarks>
    private void OnNavItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space))
            return;

        ActivateNavItem(sender);
        e.Handled = true;
    }

    /// <summary>
    /// Maps a nav <see cref="ListBoxItem"/>'s <c>Tag</c> back to the one <c>NavigateToX</c> command
    /// that does the whole job — clearing the sensor alert badge, the disconnected-feature toast,
    /// lazily constructing the target view model, closing the drawer, and letting
    /// <c>SetTransitionAndNavigate</c> read the OLD <c>ActiveNavIndex</c> to pick the shared-axis
    /// transition direction — all of which a bare index assignment would have skipped.
    /// </summary>
    private void ActivateNavItem(object? sender)
    {
        if (DataContext is not ShellViewModel vm)
            return;

        if (sender is not ListBoxItem { Tag: string tag } || !int.TryParse(tag, out var index))
            return;

        switch (index)
        {
            case 0: vm.NavigateToHomeCommand.Execute(null); break;
            case 1: vm.NavigateToCanvasCommand.Execute(null); break;
            case 2: vm.NavigateToRemoteCommand.Execute(null); break;
            case 3: vm.NavigateToAppLauncherCommand.Execute(null); break;
            case 4: vm.NavigateToTaskManagerCommand.Execute(null); break;
            case 6: vm.NavigateToAboutCommand.Execute(null); break;
            case 7: vm.NavigateToFileTransferCommand.Execute(null); break;
            case 8: vm.NavigateToDiagnosticLogsCommand.Execute(null); break;
            case 9: vm.NavigateToSettingsCommand.Execute(null); break;
            default:
                // Silent otherwise: the activated item just never navigates, and its still-true
                // IsSelected binding (nothing changed ActiveNavIndex) leaves the PREVIOUS
                // destination looking active while the page shows neither. ShellNavListTests
                // asserts the set of Tag values in ShellView.axaml equals the set of case labels
                // here, so this should be unreachable outside a broken build; Debug.Fail is the
                // local-dev signal for the gap between "the test runs" and "the test ran".
                Debug.Fail($"ShellView nav list: no NavigateToX command mapped for Tag \"{tag}\".");
                break;
        }
    }

    /// <summary>
    /// Re-asserts <c>NavList</c>'s selection from <see cref="ShellViewModel.ActiveNavIndex"/>
    /// (RemEx-zi3ua, review round 2). See the remark on the <c>IsDrawerOpen</c> branch in
    /// <see cref="OnViewModelPropertyChanged"/> for why this exists at all.
    /// </summary>
    /// <remarks>
    /// Walks <c>NavList.Items</c> rather than computing a positional <c>SelectedIndex</c> from
    /// <c>ActiveNavIndex</c>. The items are declared directly in XAML with no <c>ItemsSource</c>,
    /// so each entry in <c>Items</c> IS its own container - the <see cref="ListBoxItem"/> whose
    /// <c>Tag</c> already carries the destination index <see cref="ActivateNavItem"/> reads.
    /// Setting <c>SelectedItem</c> to that exact container sidesteps the Tag values being sparse
    /// and out of visual order (About is <c>Tag="6"</c> but sits last, after Settings'
    /// <c>Tag="9"</c>) - a positional index would have to duplicate that mapping a second time to
    /// get it right, and getting it wrong here would highlight a different wrong destination
    /// instead of fixing the bug.
    /// </remarks>
    private void ResyncNavListSelection()
    {
        if (_navList == null || DataContext is not ShellViewModel vm)
            return;

        var target = _navList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag && tag == vm.ActiveNavIndex.ToString());

        if (target != null)
            _navList.SelectedItem = target;
    }

    /// <summary>
    /// Escape closes whichever of the drawer (RemEx-q3mle) or the settings side sheet
    /// (RemEx-zrlze) is the topmost surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither Material control binds Escape itself. <c>NavigationDrawer</c>'s only dismiss gesture is
    /// a pointer press on its scrim, and <c>SideSheet</c>'s is the same plus its own close button - so
    /// without this, both are mouse-only, and each now covers the content rather than sitting beside
    /// it, which makes "get this out of my way" the common case.
    /// </para>
    /// <para>
    /// The precedence is explicit rather than left to which branch happens to run first: settings is
    /// checked before the drawer. <c>ShellViewModel.OnIsDrawerOpenChanged</c> /
    /// <c>OnIsSettingsPanelOpenChanged</c> make the two mutually exclusive - opening either closes the
    /// other - so in practice at most one of these two <c>if</c>s is ever true, and this ordering is
    /// what makes "closes the topmost surface" a real guarantee rather than an accident of whichever
    /// property happened to be checked first: settings renders on top of the drawer in z-order (it is
    /// declared, and therefore composited, after <c>ShellDrawer</c> in the visual tree), so if that
    /// invariant were ever violated by a future change, Escape still closes the one actually on top
    /// instead of silently doing the wrong thing.
    /// </para>
    /// <para>
    /// This does not have to account for the command palette (<c>CommandPaletteWindow</c>): that is a
    /// separate top-level <c>Window</c> with its own <c>Escape</c> <c>KeyBinding</c>
    /// (<c>DismissCommand</c>), so Escape is routed to it by ordinary keyboard-focus scoping whenever
    /// it is the active window - it never reaches this method at all.
    /// </para>
    /// <para>
    /// Bubbling rather than tunnelling, deliberately. <c>OnKeyDown</c> runs only once no child has
    /// handled the key, so a dialog, a text box or a page that wants Escape for itself still wins;
    /// the shell takes it last. Tunnelling would invert that and quietly break every one of them.
    /// </para>
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel vm)
        {
            if (vm.IsSettingsPanelOpen)
            {
                vm.IsSettingsPanelOpen = false;
                e.Handled = true;
                return;
            }

            if (vm.IsDrawerOpen)
            {
                vm.IsDrawerOpen = false;
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }
}

/// <summary>
/// Maps a notification's <see cref="NotificationImportance"/> onto the icon and theme brush key
/// <see cref="ShellView"/>'s snackbar sink renders it with (RemEx-uedna).
/// </summary>
/// <remarks>
/// Pulled out of <c>SnackbarToastSink</c> so the mapping itself - which importance gets which icon
/// and which brush key - is testable without an Avalonia application (resolving the brush from the
/// key still needs one, via <see cref="ThemeResources"/>, and stays in the sink).
/// </remarks>
internal static class SnackbarSeverityMapping
{
    internal static (MaterialIconKind Icon, string BrushKey) For(NotificationImportance importance) => importance switch
    {
        NotificationImportance.Problem => (MaterialIconKind.AlertCircleOutline, "SystemErrorBrush"),
        NotificationImportance.Outcome => (MaterialIconKind.CheckCircleOutline, "SystemSuccessBrush"),
        _ => (MaterialIconKind.InformationOutline, "TextSecondaryBrush"),
    };
}
