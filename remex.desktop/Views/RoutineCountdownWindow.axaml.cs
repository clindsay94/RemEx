using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

/// <summary>
/// The routine countdown (routines spec §8.6): small, topmost, ownerless, centred on the primary
/// screen's work area, with one button, Cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>NO OWNER, AND NOTHING HERE MAY REACH FOR MainWindow.</b> A <c>--minimized</c> logon start never
/// constructs the main window (P1-29), and that is the state the PC is most often in when a routine
/// fires. <c>Show()</c> on an ownerless window works in every one of those states; <c>ShowDialog</c>
/// against a hidden owner throws. See docs/REGRESSION-GUARDS.md.
/// </para>
/// <para>
/// The displayed seconds tick here, from a <see cref="DispatcherTimer"/>, but they are display only:
/// the agent's coordinator owns the real 15 s and closes this window when it ends.
/// </para>
/// </remarks>
public partial class RoutineCountdownWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _countdownEnded;

    /// <summary>For the XAML previewer and the loader. Production uses the view-model overload.</summary>
    public RoutineCountdownWindow()
    {
        InitializeComponent();
        _tick.Tick += OnTick;
        Opened += OnOpened;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Creates the window for one countdown.</summary>
    public RoutineCountdownWindow(RoutineCountdownViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private RoutineCountdownViewModel? ViewModel => DataContext as RoutineCountdownViewModel;

    /// <summary>
    /// Whether the countdown this window shows has ended (elapsed or cancelled). Only then may the
    /// window close without that close being a Cancel.
    /// </summary>
    internal bool CountdownEnded => _countdownEnded;

    /// <summary>
    /// The coordinator's close: the countdown has already elapsed or been cancelled, so closing is not
    /// a Cancel. The ONLY close that is exempt; see <see cref="OnClosing"/>.
    /// </summary>
    internal void CloseAfterCountdownEnded()
    {
        _countdownEnded = true;
        Close();
    }

    /// <summary>
    /// Shows the window centred on the primary screen's work area. Positions it BEFORE it is shown as
    /// well as after, so the platform never gets to pick a cascaded default position for it (the
    /// window has <c>WindowStartupLocation="Manual"</c>, and on Win32 "Manual" with no position is
    /// the OS's cascade - which is what stepped successive countdowns across a second monitor).
    /// </summary>
    internal void ShowCentred()
    {
        CenterOnPrimaryWorkArea(EstimatedLogicalHeight());
        Show();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        CenterOnPrimaryWorkArea(Bounds.Height);
        CancelButton.Focus();
        _tick.Start();
    }

    // SizeToContent="Height" settles the real height after the first layout pass, which can land
    // after Opened; re-centre then, so the vertical centre is the real one and not the pre-layout guess.
    // The window is not resizable, so this only fires for that layout change (and a DPI move), never
    // for the person dragging it.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && IsVisible && !_countdownEnded)
        {
            CenterOnPrimaryWorkArea(e.NewSize.Height);
        }
    }

    // ANY CLOSE BEFORE THE COUNTDOWN ENDS IS A CANCEL, whoever called Close(). IsProgrammatic is NOT a
    // safe signal: the title row's X (and, before RemEx-pp0rt.16 removed it, the Avalonia-drawn caption)
    // calls Window.Close() itself, so a real mouse click on it arrives as IsProgrammatic = true.
    // Keying on it let the X close the window while the agent's 15 s ran on, and the PC shut down
    // right after the person dismissed the warning (RemEx-pp0rt.16). The only exempt close is the
    // coordinator's own, after the countdown has ended (CloseAfterCountdownEnded); Cancel then is also
    // a no-op on the agent side, which ignores a closed countdown.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_countdownEnded)
        {
            _countdownEnded = true;
            ViewModel?.CancelCommand.Execute(null);
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _tick.Stop();

    // The window has no OS caption (WindowDecorations="None", RemEx-pp0rt.16), so its title row is the
    // drag handle. A press on the Close button is the button's, not a drag.
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
        {
            BeginMoveDrag(e);
        }
    }

    // The title row's one caption button. A plain Close: OnClosing turns it into the Cancel.
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTick(object? sender, EventArgs e) => ViewModel?.Tick();

    // IsDefault and IsCancel cover Enter and Esc wherever focus is. Space only activates the FOCUSED
    // button, and focus can move (a click on the text), so Space is handled here for the whole
    // window: the spec promises all three keys cancel.
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            ViewModel?.CancelCommand.Execute(null);
        }
    }

    // Always the PRIMARY screen's work area and its own scaling (spec §8.6, §2.3 P4): never the screen
    // the window happens to be on, never the previous countdown's position. WorkingArea is physical
    // pixels and the size is logical, so the size is scaled into the screen's space (TrayPlacement).
    private void CenterOnPrimaryWorkArea(double heightLogical)
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        var widthLogical = Bounds.Width > 0 ? Bounds.Width : Width;
        if (double.IsNaN(widthLogical) || widthLogical <= 0 || double.IsNaN(heightLogical) || heightLogical <= 0)
        {
            return;
        }

        Position = TrayPlacement.Center(screen.WorkingArea, widthLogical, heightLogical, screen.Scaling);
    }

    // Before the first layout pass the window has no height yet (SizeToContent="Height"), so measure
    // the content at the window's fixed width. Good enough to land on the right screen near the centre;
    // Opened and SizeChanged correct it to the exact centre.
    private double EstimatedLogicalHeight()
    {
        if (Content is not Control content || double.IsNaN(Width))
        {
            return 0;
        }

        content.Measure(new Size(Width, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }
}
