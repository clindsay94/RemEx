using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    /// <summary>For the XAML previewer and the loader. Production uses the view-model overload.</summary>
    public RoutineCountdownWindow()
    {
        InitializeComponent();
        _tick.Tick += OnTick;
        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Creates the window for one countdown.</summary>
    public RoutineCountdownWindow(RoutineCountdownViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private RoutineCountdownViewModel? ViewModel => DataContext as RoutineCountdownViewModel;

    private void OnOpened(object? sender, EventArgs e)
    {
        CenterOnPrimaryWorkArea();
        CancelButton.Focus();
        _tick.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _tick.Stop();

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

    private void CenterOnPrimaryWorkArea()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }
}
