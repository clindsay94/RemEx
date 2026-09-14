using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class TrayFlyoutWindow : Window
{
    /// <summary>
    /// How long after being shown the window ignores deactivation.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT A COSMETIC DELAY. On Windows, opening the tray icon's own context menu
    /// deactivates this window — so a naive hide-on-deactivate makes the flyout vanish the instant
    /// you right-click the icon that owns it. The previous code-behind carried a comment refusing
    /// to implement deactivate-hide for exactly this reason; the grace window plus
    /// <see cref="SuppressNextDeactivate"/> is what makes it safe to implement.
    /// </remarks>
    private static readonly TimeSpan DeactivateGrace = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private readonly TrayFlyoutLayoutStore _layoutStore = new();
    private readonly DispatcherTimer _saveTimer;

    private DateTime _shownAtUtc = DateTime.MinValue;
    private bool _suppressDeactivate;

    public TrayFlyoutWindow()
    {
        InitializeComponent();

        // TRANSPARENT ONLY — NOT MICA, NOT BLUR (RemEx-zu09j). DWM composites a Mica or acrylic
        // backdrop across the whole window rect, including the margin this window leaves around its
        // rounded card for the drop shadow, which is the grey rectangle that bug was about.
        //
        // Everything here now matches TrayBalloonWindow, minus Mica (RemEx-8pym0). That window uses
        // the same undecorated-transparent-window-around-a-rounded-card pattern and renders with no
        // stray edge, so where the two differed, the balloon wins. Two of those differences mattered:
        // Background is Transparent rather than null - a null background is not a brush that paints
        // nothing, it is no brush at all - and the hint list keeps a fallback, because a single
        // unachievable entry leaves the level to chance.
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        // Renamed in Avalonia 12 (RemEx-jcma3). The old SystemDecorations property survives as an
        // obsolete alias, but its TYPE is now WindowDecorations - so "SystemDecorations.None" binds
        // to the property rather than the enum and fails to compile. Both halves have to move.
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;

        _saveTimer = new DispatcherTimer { Interval = SaveDebounce };
        _saveTimer.Tick += OnSaveTimerTick;

        // No DWM corner-preference call. It was belt-and-braces for an edge nothing draws: the
        // visible rounding comes from the inner Border, and the window rect it would round sits 12px
        // outside that card where nothing is painted. Asking DWM to manage the frame of an
        // undecorated window is the one thing this window did that TrayBalloonWindow does not, and a
        // frame that will not go away is what it got (RemEx-8pym0).
        Deactivated += OnDeactivated;

        // ONLY WHEN PINNED. The plan saved on every PositionChanged, which fires when ShowAtTray
        // places the transient popup at the tray corner — so showing the flyout on a machine whose
        // pinned monitor is currently unplugged would overwrite the saved pinned layout with an
        // unpinned one, and reconnecting the monitor would not bring it back. It also turned every
        // show into a file write. The only unpinned save left is the explicit one in OnTogglePin.
        PositionChanged += (_, _) =>
        {
            if (ViewModel?.IsPinned == true)
                ScheduleSave();
        };

        ApplyMode(isPinned: false);
    }

    private TrayFlyoutViewModel? ViewModel => DataContext as TrayFlyoutViewModel;

    /// <summary>Tells the window to ignore the next deactivation, because we caused it.</summary>
    public void SuppressNextDeactivate() => _suppressDeactivate = true;

    public async void ShowAtTray()
    {
        var saved = await _layoutStore.LoadRawAsync();
        var screens = Screens.All.Select(screen => screen.WorkingArea).ToList();
        var valid = TrayFlyoutGeometryValidator.Validate(saved, screens);

        if (valid is { IsPinned: true })
        {
            ApplyMode(isPinned: true);
            Width = valid.Width;
            Height = valid.Height;
            Position = new PixelPoint((int)valid.X, (int)valid.Y);
        }
        else
        {
            // Either nothing saved, or the saved rect is on a screen that no longer exists. The
            // tray corner is always valid, so it is the fallback in both cases.
            ApplyMode(isPinned: false);

            if (Screens.Primary is { } primary)
            {
                Position = TrayPlacement.BottomRight(
                    primary.WorkingArea, Width, Height, primary.Scaling, marginLogical: 12);
            }
        }

        // Clear rather than carry: a suppression armed for a deactivation that never arrived must
        // not survive to swallow the next genuine click-away.
        _suppressDeactivate = false;
        _shownAtUtc = DateTime.UtcNow;
        ViewModel?.Refresh();
        Show();
    }

    /// <summary>Switches between the transient popup and the pinned, movable window.</summary>
    private void ApplyMode(bool isPinned)
    {
        // Focusable is the hinge. False gives a popup that never steals focus from what you are
        // doing; true is required for BeginMoveDrag and for resize grips to respond.
        Focusable = isPinned;
        CanResize = isPinned;
        SizeToContent = isPinned ? SizeToContent.Manual : SizeToContent.Height;

        // ENFORCES TrayFlyoutGeometry.CardsMaxHeight's own width assumption (RemEx-4kv0g.18.2 fix
        // round 1). Nothing else resets Width on unpin or in ShowAtTray's transient branch (that
        // branch only sets Position) — so a window pinned-and-resized down toward
        // TrayFlyoutGeometryValidator.MinWidth (320, reachable through the resize grips) and then
        // unpinned would otherwise STAY that narrow, wrapping the toolbar row (RemEx-4kv0g.18.5,
        // WrapPanel) onto more rows than CardsMaxHeight's budget has room for — and showing the
        // cards grid as a single scrolling column instead of the two-plus DefaultWidth (528, fix
        // round 2) promises. Setting Width here is what makes OnTogglePin's own remark below
        // ("unpin means go back to being a popup ... at the default size") actually true.
        if (!isPinned)
            Width = TrayFlyoutGeometry.DefaultWidth;

        // The cards row (ContentGrid.RowDefinitions[1]) is Auto in transient mode, so
        // SizeToContent.Height measures it like any other content and CardsScrollViewer.MaxHeight
        // caps how tall that measurement can grow (RemEx-4kv0g.18.2). Pinned, the row becomes
        // star-sized AND MaxHeight is relaxed to PositiveInfinity (fix round 2 — CardsMaxHeight is
        // a TRANSIENT-only cap; leaving it in force while pinned left ~50px of empty space below
        // the tiles on a resized-tall popup, with rows hidden behind a needless scrollbar) so the
        // row genuinely takes whatever height the user's resize leaves free above the fixed-height
        // tiles row, instead of the tiles row stretching. RowDefinition is not part of the visual
        // tree, so it cannot pick this mode up from a XAML binding to the window's DataContext —
        // ApplyMode already owns the other half of this same mode switch
        // (Focusable/CanResize/SizeToContent above).
        if (ContentGrid.RowDefinitions.Count > 1)
        {
            ContentGrid.RowDefinitions[1].Height =
                isPinned ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }

        CardsScrollViewer.MaxHeight = isPinned ? double.PositiveInfinity : TrayFlyoutGeometry.CardsMaxHeight;

        if (ViewModel is { } vm)
            vm.IsPinned = isPinned;
    }

    /// <summary>Pins or unpins, and records the result.</summary>
    /// <remarks>
    /// UNPINNING DISCARDS THE PINNED RECT, and that is the intended behaviour rather than an
    /// oversight. <c>ApplyMode(false)</c> restores <c>SizeToContent.Height</c>, so the window
    /// collapses before the debounced save reads it, and what lands on disk is the transient
    /// geometry with <c>IsPinned = false</c>. Re-pinning therefore starts from the tray corner at
    /// the default size rather than from wherever the window used to live.
    /// <para>
    /// The alternative — keeping the old rect under a second key so unpin/re-pin round-trips — was
    /// not built. Unpin means "go back to being a popup", and a popup that remembers a size the
    /// user cannot see is a surprise waiting to be rediscovered. If this turns out to be the wrong
    /// call, the place to change it is here and in <see cref="ShowAtTray"/>'s
    /// <c>IsPinned: true</c> guard.
    /// </para>
    /// </remarks>
    private void OnTogglePin(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var pinning = ViewModel?.IsPinned != true;
        ApplyMode(pinning);
        ScheduleSave();
    }

    /// <summary>
    /// Starts a resize from one of the eight hit areas in the window's transparent margin.
    /// </summary>
    /// <remarks>
    /// <c>CanResize = true</c> ALONE DOES NOTHING HERE. With <c>SystemDecorations.None</c> Windows
    /// draws no frame, so there is no resize border to grab and no grip in the corner — the window
    /// reported itself resizable and could not be resized (RemEx-2j58q). These handlers put the hit
    /// areas back by hand, in the 12px band between the window edge and the card, which is otherwise
    /// empty space carrying only the drop shadow.
    /// <para>
    /// The edge comes from <c>Tag</c> rather than from eight near-identical handlers. A typo'd tag
    /// disables that one grip instead of throwing, which is the right failure for a resize border:
    /// the other seven still work and the window stays usable.
    /// </para>
    /// </remarks>
    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.IsPinned != true)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if ((sender as Control)?.Tag is not string tag || !Enum.TryParse<WindowEdge>(tag, out var edge))
            return;

        BeginResizeDrag(edge, e);
    }

    /// <summary>Drags the whole window by its header, since it has no system title bar.</summary>
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.IsPinned != true)
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (ViewModel?.IsPinned == true)
            return;

        if (_suppressDeactivate)
        {
            _suppressDeactivate = false;
            return;
        }

        if (DateTime.UtcNow - _shownAtUtc < DeactivateGrace)
            return;

        Hide();
    }

    /// <summary>
    /// Coalesces the writes a drag or resize would otherwise produce.
    /// </summary>
    /// <remarks>
    /// PositionChanged fires per frame while dragging. Without this, one drag across a monitor is
    /// hundreds of atomic file writes, each of which stages and renames.
    /// </remarks>
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();

        // async void is correct for an event handler, and safe only because SaveAsync catches and
        // logs its own I/O failures. That was ASSERTED here before it was TRUE: the store had no
        // catch and RemexDataPaths.WriteAllTextAtomicAsync rethrows, so any write failure reached
        // the dispatcher unhandled. If you change SaveAsync's contract, change this too.
        await _layoutStore.SaveAsync(new TrayFlyoutGeometry
        {
            IsPinned = ViewModel?.IsPinned ?? false,
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
        });
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (ViewModel?.IsPinned == true)
            ScheduleSave();
    }

    /// <summary>
    /// Raises the main window for the tiles that navigate it, and closes the popup for a tile OR an
    /// app shortcut that just launched something outside RemEx.
    /// </summary>
    /// <remarks>
    /// TWO DIFFERENT REASONS TO HIDE, DELIBERATELY NOT MERGED. A <see cref="TrayTile"/> with
    /// <see cref="TrayTile.OpensMainWindow"/> also raises RemEx's own window — the tile's own
    /// <c>Command</c> navigates, and this is the other half the old flyout had. A
    /// <see cref="TrayShortcut"/> (Flyout D2 .1, RemEx-4kv0g.18.5) launches a DIFFERENT app entirely,
    /// so bringing RemEx's window forward after it would be the wrong window popping up; it only
    /// hides the popup, the same closing gesture a click-away already gives — but ONLY when the
    /// flyout is transient. A pinned flyout is a command center meant to stay open (the same
    /// <c>IsPinned</c> gate <see cref="OnDeactivated"/> already applies to click-away), so a
    /// shortcut click there must launch and leave the popup up. Lock, Sleep and the Power submenu
    /// button are excluded from both by construction - none of them reach here with
    /// <c>OpensMainWindow: true</c>, and the submenu button has no <c>Click</c> handler at all.
    /// </remarks>
    private void OnTileClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case TrayTile { OpensMainWindow: true }:
                Hide();
                App.BringMainWindowToFront();
                break;
            case TrayShortcut when ViewModel?.IsPinned != true:
                Hide();
                break;
        }
    }

    private void OnOpenMainApp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
        App.BringMainWindowToFront();
    }

    private void OnCloseFlyout(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Hide();
}
