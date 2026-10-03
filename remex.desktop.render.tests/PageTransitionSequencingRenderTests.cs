using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Desktop.Controls;
using Remex.Desktop.Styles;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The shell's page host under a burst of sidebar clicks, on a real (headless) UI thread with real
/// animation time (review of RemEx-pp4cm.1): a navigation is held back only for the outgoing page's
/// exit, never for the incoming page's 400 ms arrival, and however the clicks land the last page ends
/// fully opaque at rest. Also the tray flyout's replayed open animation.
/// </summary>
/// <remarks>
/// <para>
/// The host is wired exactly the way <see cref="ShellView"/> wires <c>PageHost</c>: the same
/// transition factory, a <see cref="PageHostSequencer"/>, and the completion flush posted one
/// dispatcher turn later. The real shell is not used because its pages pull in the whole service
/// graph; the part under test is the transition and the sequencer, which are the production types.
/// </para>
/// <para>
/// Timing assertions have wide margins on purpose: the decisive check is ORDER — the second
/// navigation starts while the first page is still arriving — which sequencing that holds for the
/// whole transition cannot satisfy at any machine speed.
/// </para>
/// <para>
/// Every pump runs a real dispatcher loop for a few milliseconds and then renders a frame. Avalonia
/// paces animation frames with a <c>DispatcherTimer</c> once a frame has been committed, and
/// <c>RunJobs</c> plus a forced render tick alone was measured to leave these transitions frozen
/// within their first few frames.
/// </para>
/// </remarks>
public sealed class PageTransitionSequencingRenderTests
{
    private static readonly SharedAxisPageTransition Timing = new();

    [AvaloniaFact]
    public void ASecondClickWaitsOnlyForTheExitAndAClickDuringTheArrivalTakesOverCleanly()
    {
        Motion.SetReducedMotion(false);
        var harness = new PageHostHarness();
        try
        {
            var pageA = Page("A");
            var pageB = Page("B");
            var pageC = Page("C");
            var pageD = Page("D");

            harness.Navigate(pageA);
            PumpUntil(harness.Window, () => !harness.Sequencer.IsBusy && !IsArriving(harness.Host, pageA), Timing.TotalDuration * 6);

            // Click B, then C straight away - inside B's exit, so C is held back.
            harness.Navigate(pageB);
            PumpFor(harness.Window, TimeSpan.FromMilliseconds(30));
            harness.Navigate(pageC);
            harness.Host.Content.Should().BeSameAs(pageB, "a click during the exit is held back until the exit is over");

            PumpUntil(harness.Window, () => ReferenceEquals(harness.Host.Content, pageC), Timing.TotalDuration * 6);

            var b = harness.Assignments.Single(a => ReferenceEquals(a.View, pageB));
            var c = harness.Assignments.Single(a => ReferenceEquals(a.View, pageC));
            c.PreviousStillArriving.Should().BeTrue(
                "the held click is released when B's exit ends, while B is still arriving - not after B has settled");
            (c.At - b.At).Should().BeLessThan(
                Timing.TotalDuration,
                "the second navigation waits for the {0} exit, not the {1} whole transition",
                Timing.HoldDuration,
                Timing.TotalDuration);

            // Wait for C's exit-half to finish, so C is mid-arrival, then click D: nothing is holding
            // the host, so D starts at once and takes C out from wherever it had got to.
            PumpUntil(harness.Window, () => !harness.Sequencer.IsBusy, Timing.TotalDuration * 6);
            IsArriving(harness.Host, pageC).Should().BeTrue("C should still be arriving when D is clicked");

            harness.Navigate(pageD);
            Pump(harness.Window);
            harness.Host.Content.Should().BeSameAs(pageD, "a click during an arrival is not held back");
            harness.Assignments.Single(a => ReferenceEquals(a.View, pageD)).PreviousStillArriving.Should().BeTrue();

            PumpUntil(harness.Window, () => !harness.Sequencer.IsBusy && !IsArriving(harness.Host, pageD), Timing.TotalDuration * 6);
            Pump(harness.Window);

            var dPresenter = PresenterOf(harness.Host, pageD);
            dPresenter.IsVisible.Should().BeTrue();
            dPresenter.Opacity.Should().Be(1d, "the last page ends fully opaque");
            dPresenter.RenderTransform.Should().BeNull("the last page ends at rest, with no offset left on it");

            Presenters(harness.Host)
                .Where(p => p != dPresenter)
                .Should().NotBeEmpty()
                .And.OnlyContain(p => !p.IsVisible && p.Content == null, "the page it replaced is hidden, not left part-way out");
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void AReplayedEntranceKeepsItsSlideWhenTheRunItReplacedFinishesLate()
    {
        // The tray flyout reopened within its own open animation (review of RemEx-pp4cm.1): the
        // replaced run's cleanup, inline in Cancel or on a later turn, must not pull the transform
        // out from under the replay.
        var target = new Border { Width = 100, Height = 100 };
        var window = new Window { Width = 200, Height = 200, Content = target };
        var runner = new EntranceRunner(target);

        try
        {
            Motion.SetReducedMotion(false);
            window.Show();
            Pump(window);

            var longRun = TimeSpan.FromSeconds(30);
            _ = runner.Play(longRun, TimeSpan.Zero, Motion.ListItemOffset);
            Pump(window);
            target.RenderTransform.Should().NotBeNull("the first open is sliding");
            var firstTransform = target.RenderTransform;

            _ = runner.Play(longRun, TimeSpan.Zero, Motion.ListItemOffset);
            Pump(window);
            Pump(window);

            runner.IsRunning.Should().BeTrue();
            target.RenderTransform.Should().BeSameAs(
                firstTransform,
                "the replaced run must not tear down the transform the replay is now sliding on");
            target.Opacity.Should().BeLessThan(1d, "the replay is still fading in");
        }
        finally
        {
            // A reduced-motion replay stops the long run and clears what it left.
            Motion.SetReducedMotion(true);
            _ = runner.Play(TimeSpan.Zero, TimeSpan.Zero, 0d);
            Pump(window);
            Motion.SetReducedMotion(false);
            window.Close();
        }

        target.RenderTransform.Should().BeNull();
        runner.IsRunning.Should().BeFalse();
    }

    private static Border Page(string name) => new() { Name = name, Background = Avalonia.Media.Brushes.SteelBlue };

    private static ContentPresenter[] Presenters(TransitioningContentControl host) =>
        host.GetVisualDescendants().OfType<ContentPresenter>().Where(p => p.TemplatedParent == host).ToArray();

    private static ContentPresenter PresenterOf(TransitioningContentControl host, Control page) =>
        Presenters(host).Single(p => ReferenceEquals(p.Content, page));

    private static bool IsArriving(TransitioningContentControl host, Control page) =>
        SharedAxisPageTransition.IsArriving(PresenterOf(host, page));

    private static void PumpFor(Window window, TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            Pump(window);
            Thread.Sleep(5);
        }
    }

    private static void PumpUntil(Window window, Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            clock.Elapsed.Should().BeLessThan(timeout, "the condition should have been met by now");
            Pump(window);
            Thread.Sleep(2);
        }
    }

    private static void Pump(Window window)
    {
        // A real dispatcher loop for a few milliseconds, so DispatcherTimers fire as they would in
        // the app (MediaContext paces animation frames with one).
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(8));
        Dispatcher.UIThread.PushFrame(frame);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>One page handed to the host.</summary>
    /// <param name="View">The page.</param>
    /// <param name="At">When, on <see cref="PageHostHarness.Elapsed"/>.</param>
    /// <param name="PreviousStillArriving">Whether the page it replaces was still arriving.</param>
    private sealed record Assignment(object? View, TimeSpan At, bool PreviousStillArriving);

    /// <summary>A content host wired the way <see cref="ShellView"/> wires <c>PageHost</c>.</summary>
    private sealed class PageHostHarness
    {
        public PageHostHarness()
        {
            Host = new TransitioningContentControl { PageTransition = ShellView.NewPageTransition(reducedMotion: false) };

            // Posted, as in ShellView.OnLoaded: TransitioningContentControl hides the old presenter
            // only after raising TransitionCompleted.
            Host.TransitionCompleted += (_, _) => Dispatcher.UIThread.Post(Flush);

            Window = new Window { Width = 400, Height = 300, Content = Host };
            Window.Show();
            Pump(Window);
        }

        public Window Window { get; }

        public TransitioningContentControl Host { get; }

        public PageHostSequencer Sequencer { get; } = new();

        /// <summary>Every page handed to the host, in order.</summary>
        public System.Collections.Generic.List<Assignment> Assignments { get; } = new();

        /// <summary>Runs from construction; the clock <see cref="Assignment.At"/> is read from.</summary>
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();

        public void Navigate(object view)
        {
            if (Sequencer.RequestShow(view))
            {
                Assign(view);
            }
        }

        private void Assign(object? view)
        {
            if (ReferenceEquals(Host.Content, view))
            {
                Flush();
                return;
            }

            // Recorded at the moment of assignment: once layout runs, the new transition takes over
            // (and so stops) whatever was still arriving.
            Assignments.Add(new Assignment(
                view,
                Elapsed.Elapsed,
                Presenters(Host).Any(SharedAxisPageTransition.IsArriving)));
            Host.Content = view;
        }

        private void Flush()
        {
            if (Sequencer.RequestFlush(out var queued))
            {
                Assign(queued);
            }
        }
    }
}
