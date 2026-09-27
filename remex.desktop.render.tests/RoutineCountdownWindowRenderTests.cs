using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    public void ClosingTheWindowWhileTheCountdownRunsCancelsExactlyOnce()
    {
        var (window, cancels) = Open();

        // THE REAL CLOSE PATH, and a PROGRAMMATIC one on purpose: the caption buttons are
        // Avalonia-drawn (ExtendClientAreaToDecorationsHint), so a real click on the X is exactly this
        // call - Window.Close() - and arrives with IsProgrammatic = true. The old rule ("cancel when
        // !IsProgrammatic") let that close through while the 15 s ran on, and SHUTDOWN was issued
        // (RemEx-pp0rt.16).
        window.Close();
        Dispatcher.UIThread.RunJobs();

        cancels().Should().Be(1, "any close before the countdown ends is a cancel, or the PC shuts down anyway");
        window.IsVisible.Should().BeFalse();
        window.CountdownEnded.Should().BeTrue("a cancelled countdown has ended, so nothing after it cancels again");
    }

    [AvaloniaFact]
    public void CancelThenTheCoordinatorsCloseCancelsExactlyOnce()
    {
        var (window, cancels) = Open();

        // Esc/Enter/Space: the cancel runs, then the coordinator closes the window as ended.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.CloseAfterCountdownEnded();
        Dispatcher.UIThread.RunJobs();

        cancels().Should().Be(1);
    }

    /// <summary>Runs the dispatcher until <paramref name="task"/> completes, then awaits it. Never
    /// blocks on it: ShowCountdownAsync posts to the UI thread this test is running on, and by the
    /// time of the await the task has already completed (RemEx-7cq0 bans blocking waits).</summary>
    private static async Task<T> PumpAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        task.IsCompletedSuccessfully.Should().BeTrue();
        return await task;
    }

    [AvaloniaFact]
    public void TheCoordinatorsCloseAfterTheCountdownEndedDoesNotCancel()
    {
        var (window, cancels) = Open();

        window.CloseAfterCountdownEnded();
        Dispatcher.UIThread.RunJobs();

        cancels().Should().Be(0, "the coordinator closes the window only once the countdown is over");
        window.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ThroughTheRoutineUiTheCoordinatorsCloseDoesNotCancelButAnyEarlierCloseDoes()
    {
        var ui = new AvaloniaRoutineUi();
        var cancels = 0;
        var prompt = new RoutineCountdownPrompt("run-ui", "Evening", "SHUTDOWN", "Pixel", "manual.app", 15, false, false);

        // The coordinator's own path: ShowCountdownAsync, then CloseCountdown when the 15 s end.
        (await PumpAsync(ui.ShowCountdownAsync(prompt, () => cancels++))).Should().BeTrue();
        var first = ui.CurrentWindow!;
        ui.CloseCountdown("run-ui");
        Dispatcher.UIThread.RunJobs();
        cancels.Should().Be(0, "the countdown elapsed; the coordinator's close is not a cancel");
        first.IsVisible.Should().BeFalse();

        // The person's path: the window closes itself (the drawn X) while the countdown is live.
        await PumpAsync(ui.ShowCountdownAsync(prompt with { RunId = "run-ui-2" }, () => cancels++));
        var second = ui.CurrentWindow!;
        second.Close();
        Dispatcher.UIThread.RunJobs();
        cancels.Should().Be(1);
        ui.CurrentWindow.Should().BeNull("a window the person closed is dropped from the slot");

        // The coordinator's close that follows that cancel must not throw on the dead window.
        ui.CloseCountdown("run-ui-2");
        Dispatcher.UIThread.RunJobs();
        cancels.Should().Be(1);
    }

    [AvaloniaFact]
    public void ItCannotBeMinimizedOrMaximized()
    {
        var (window, _) = Open();

        window.CanMinimize.Should().BeFalse("a minimized countdown hides the only warning");
        window.CanMaximize.Should().BeFalse();
        window.CanResize.Should().BeFalse();
        window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_RestoreButton")
            .Where(c => c.IsEffectivelyVisible)
            .Should().BeEmpty("no drawn caption button may minimize or maximize the countdown");

        window.CloseAfterCountdownEnded();
    }

    [AvaloniaFact]
    public void ItOpensCentredOnThePrimaryWorkArea()
    {
        var (window, _) = Open();

        var screen = window.Screens?.Primary;
        if (screen is null)
        {
            // The headless platform exposes no screen here; TrayPlacementTests pins the arithmetic.
            window.CloseAfterCountdownEnded();
            return;
        }

        var expected = Remex.Desktop.Services.TrayPlacement.Center(
            screen.WorkingArea, window.Bounds.Width, window.Bounds.Height, screen.Scaling);
        window.Position.Should().Be(expected);
        window.CloseAfterCountdownEnded();
    }

    [AvaloniaFact]
    public async Task TheCountdownRaisesATrayBalloonEvenWhileTheMainWindowIsVisible()
    {
        var service = Remex.Desktop.Services.NotificationService.Instance;
        var (balloon, probe, toast) = (service.TrayBalloon, service.WindowVisibleProbe, service.InApp);
        var recorder = new RecordingBalloon();
        try
        {
            service.TrayBalloon = recorder;
            service.WindowVisibleProbe = () => true;
            service.InApp = null;

            var ui = new AvaloniaRoutineUi();
            await PumpAsync(ui.ShowCountdownAsync(
                new RoutineCountdownPrompt("run-balloon", "Evening", "SHUTDOWN", "Pixel", "manual.app", 15, false, false),
                () => { }));
            Dispatcher.UIThread.RunJobs();

            recorder.Titles.Should().Equal(["Evening"], "the balloon is the guaranteed countdown surface (§8.6)");
            ui.CloseCountdown("run-balloon");
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            service.TrayBalloon = balloon;
            service.WindowVisibleProbe = probe;
            service.InApp = toast;
        }
    }

    private sealed class RecordingBalloon : Remex.Desktop.Services.ITrayBalloonSink
    {
        public List<string> Titles { get; } = new();

        public bool TryShow(Remex.Desktop.Services.NotificationImportance importance, string title, string message)
        {
            Titles.Add(title);
            return true;
        }
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
