using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Material.Styles.Controls;
using Material.Styles.Models;
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

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _pageHost = this.FindControl<TransitioningContentControl>("PageHost");
        _immersiveHost = this.FindControl<TransitioningContentControl>("ImmersiveHost");
        _navList = this.FindControl<ListBox>("NavList");

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

        var bootSplash = this.FindControl<Controls.Splash.SkiaSplashControl>("BootSplash");
        if (bootSplash != null && !_bootSplashHooked)
        {
            // Guarded because OnLoaded runs again on every reattach to the visual tree. Unguarded,
            // each reattach added another SequenceCompleted handler and OnBootSequenceCompleted then
            // ran once per attach - the same subscribe-without-unsubscribe shape RemEx-wcte fixed
            // elsewhere in this file's neighbours.
            _bootSplashHooked = true;

            bootSplash.SequenceCompleted += () =>
            {
                if (DataContext is ShellViewModel vm2)
                    vm2.OnBootSequenceCompleted();
            };

            // SkiaSplashControl.Dispose detaches the DispatcherTimer's Tick handler, without which the
            // timer keeps a strong reference back into the control forever. It must be called on FINAL
            // teardown only: OnDetachedFromVisualTree deliberately calls Stop() and not Dispose(),
            // because the control can be reattached and OnAttachedToVisualTree resumes ticking through
            // that same subscription. Disposing on unload would leave a reattached splash frozen, so the
            // owning window's Closed event is the correct hook - it fires once, and only when the
            // control genuinely will not come back. (RemEx-wcte: the Dispose existed but nothing called
            // it, which made the fix dead code.)
            if (TopLevel.GetTopLevel(this) is Window owner)
                owner.Closed += (_, _) => bootSplash.Dispose();
        }
    }

    private bool _bootSplashHooked;
    private bool _toastHostInstalled;
    private bool _pageHostSequenced;

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
        }

        if (DataContext is ShellViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            _previousVm = vm;

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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsSettingsPanelOpen no longer needs a handler here (RemEx-zrlze) - it is bound straight to
        // material:SideSheet's own SideSheetOpened, which drives the slide/scrim itself instead of a
        // hand-toggled "open" class.

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
