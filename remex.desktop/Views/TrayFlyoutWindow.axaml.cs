using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;

        _saveTimer = new DispatcherTimer { Interval = SaveDebounce };
        _saveTimer.Tick += OnSaveTimerTick;

        // Not in the constructor body: the platform handle does not exist until the window opens.
        Opened += (_, _) => TrayWindowCorners.ApplyRounded(this);
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

        if (ViewModel is { } vm)
            vm.IsPinned = isPinned;
    }

    private void OnTogglePin(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var pinning = ViewModel?.IsPinned != true;
        ApplyMode(pinning);
        ScheduleSave();
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

        // async void is correct for an event handler, and safe here only because SaveAsync swallows
        // its own I/O failures (Task 2) — nothing can escape to the dispatcher.
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
    /// Raises the main window for the tiles that navigate it, and only those.
    /// </summary>
    /// <remarks>
    /// The tile's own <c>Command</c> does the navigating; this is the other half the old flyout had
    /// and the tile grid initially lost. RemEx closes to the tray by default, so telling
    /// <c>ShellViewModel</c> to change page without showing the window changes a page nobody can
    /// see. Gated on <see cref="TrayTile.OpensMainWindow"/> because Lock and Sleep must not pop a
    /// window up as the screen goes away.
    /// </remarks>
    private void OnTileClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TrayTile { OpensMainWindow: true })
            return;

        Hide();
        App.BringMainWindowToFront();
    }

    private void OnOpenMainApp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
        App.BringMainWindowToFront();
    }

    private void OnCloseFlyout(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Hide();
}
