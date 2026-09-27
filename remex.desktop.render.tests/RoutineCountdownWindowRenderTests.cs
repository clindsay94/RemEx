using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using Remex.Desktop.Services.Routines;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The routine countdown window (routines spec §8.6) against the real App resources: it opens with no
/// owner and no MainWindow, actually paints, and Enter, Space and Esc each cancel.
/// </summary>
public sealed class RoutineCountdownWindowRenderTests
{
    private static (RoutineCountdownWindow Window, Func<int> Cancels) Open()
    {
        var cancels = 0;
        var viewModel = new RoutineCountdownViewModel(
            new RoutineCountdownPrompt("run-1", "Evening", "SHUTDOWN", "Pixel", "manual.app", 15, false, false),
            () => cancels++);
        var window = new RoutineCountdownWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, () => cancels);
    }

    [AvaloniaFact]
    public void ItOpensOwnerlessAndTopmostAndPaints()
    {
        var (window, _) = Open();

        window.Owner.Should().BeNull("the countdown must not depend on any other window existing");
        window.Topmost.Should().BeTrue();
        window.IsVisible.Should().BeTrue();

        var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        var pixels = FramePixels.From(frame!);
        var whole = new PixelRect(0, 0, pixels.Size.Width, pixels.Size.Height);
        pixels.DistinctQuantisedColours(whole).Should().BeGreaterThan(1,
            "a countdown with a title, text and a button cannot be one flat colour");

        window.Close();
    }

    [AvaloniaFact]
    public void AUserCloseFromTheChromeCancelsExactlyOnce()
    {
        var (window, cancels) = Open();

        // What X, Alt+F4 and the taskbar produce: a Closing with IsProgrammatic = false. The headless
        // platform's close request (IWindowImpl.Closing) is not reachable from a test, so this drives
        // the handler's rule directly; RoutineCountdownSurfaceTests pins that Closing is wired to it.
        window.HandleClosing(isProgrammatic: false);
        Dispatcher.UIThread.RunJobs();
        cancels().Should().Be(1, "closing the countdown window is a cancel, or the PC shuts down anyway");

        window.HandleClosing(isProgrammatic: false);
        cancels().Should().Be(1, "cancel runs at most once");
        window.Close();
    }

    [AvaloniaFact]
    public void TheCoordinatorsOwnCloseDoesNotCancel()
    {
        var (window, cancels) = Open();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        cancels().Should().Be(0, "a programmatic close is the countdown ending, not the user cancelling");
    }

    [AvaloniaTheory]
    [InlineData(Key.Escape, PhysicalKey.Escape)]
    [InlineData(Key.Enter, PhysicalKey.Enter)]
    [InlineData(Key.Space, PhysicalKey.Space)]
    public void EachCancelKeyCancels(Key key, PhysicalKey physical)
    {
        var (window, cancels) = Open();

        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();

        cancels().Should().Be(1, $"{key} must cancel the countdown");
        window.Close();
    }
}
