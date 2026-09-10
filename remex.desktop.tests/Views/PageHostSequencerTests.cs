using FluentAssertions;
using Remex.Desktop.Views;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Covers RemEx-lma2o: the shell must never start a page transition on top of one already running.
/// </summary>
/// <remarks>
/// Repairing a cancelled transition after the fact (RemEx-yj3x2) narrowed the blank-page window but
/// could not close it, because Avalonia's <c>ArrangeOverride</c> starts the successor transition in
/// the same call that cancels the old one — so the old one's cleanup lands on top of a transition
/// that is already animating. These tests pin the rule that replaced it: one transition at a time,
/// and the newest requested page wins.
/// </remarks>
public class PageHostSequencerTests
{
    private static readonly object Home = new();
    private static readonly object Launcher = new();
    private static readonly object Files = new();

    [Fact]
    public void TheFirstNavigationGoesStraightToTheHost()
    {
        var sequencer = new PageHostSequencer();

        sequencer.RequestShow(Home).Should().BeTrue();
        sequencer.IsBusy.Should().BeTrue();
        sequencer.HasPendingView.Should().BeFalse();
    }

    [Fact]
    public void ANavigationDuringATransitionIsHeldBack()
    {
        var sequencer = new PageHostSequencer();
        sequencer.RequestShow(Home);

        // This is the click that used to strand a page: it arrived while Home was still animating.
        sequencer.RequestShow(Launcher).Should().BeFalse();
        sequencer.HasPendingView.Should().BeTrue();
        sequencer.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void TheHeldBackNavigationIsReleasedWhenTheTransitionFinishes()
    {
        var sequencer = new PageHostSequencer();
        sequencer.RequestShow(Home);
        sequencer.RequestShow(Launcher);

        sequencer.RequestFlush(out var released).Should().BeTrue();
        released.Should().BeSameAs(Launcher);

        // Releasing it starts a transition of its own, so the host is busy again rather than free.
        sequencer.IsBusy.Should().BeTrue();
        sequencer.HasPendingView.Should().BeFalse();
    }

    [Fact]
    public void ABurstOfClicksCostsOneExtraTransitionNotAQueueOfThem()
    {
        var sequencer = new PageHostSequencer();
        sequencer.RequestShow(Home);

        sequencer.RequestShow(Launcher).Should().BeFalse();
        sequencer.RequestShow(Files).Should().BeFalse();

        // Home → App Launcher → File Transfer, clicked straight through: the user wants the page they
        // stopped on, so App Launcher is dropped rather than replayed.
        sequencer.RequestFlush(out var released).Should().BeTrue();
        released.Should().BeSameAs(Files);

        sequencer.RequestFlush(out var nothingLeft).Should().BeFalse();
        nothingLeft.Should().BeNull();
        sequencer.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void FinishingWithNothingHeldBackSimplyFreesTheHost()
    {
        var sequencer = new PageHostSequencer();
        sequencer.RequestShow(Home);

        sequencer.RequestFlush(out var released).Should().BeFalse();
        released.Should().BeNull();
        sequencer.IsBusy.Should().BeFalse();

        // And the next navigation is free to go straight through again.
        sequencer.RequestShow(Launcher).Should().BeTrue();
    }

    [Fact]
    public void AFlushWithNoTransitionRunningIsHarmless()
    {
        var sequencer = new PageHostSequencer();

        // The watchdog fires on a host that was never arranged, so it can flush a sequencer that is
        // already idle. That must not wedge it.
        sequencer.RequestFlush(out var released).Should().BeFalse();
        released.Should().BeNull();
        sequencer.IsBusy.Should().BeFalse();

        sequencer.RequestShow(Home).Should().BeTrue();
    }

    [Fact]
    public void AFlushFromAnySourceReleasesTheHeldBackNavigation()
    {
        var sequencer = new PageHostSequencer();
        sequencer.RequestShow(Home);
        sequencer.RequestShow(Launcher);

        // The sequencer cannot tell a watchdog flush from a completion flush, and deliberately so:
        // both mean "the host is free again". That is why this cannot stand in for a test of the
        // watchdog itself. The timer wiring - arming before the content assignment, stopping at the
        // top of every flush - lives in ShellView.axaml.cs and is verified by hand, because this
        // assembly has no headless harness to run a dispatcher timer in.
        sequencer.RequestFlush(out var released).Should().BeTrue();
        released.Should().BeSameAs(Launcher);
    }

    [Fact]
    public void NavigatingToNothingIsSequencedLikeAnyOtherPage()
    {
        var sequencer = new PageHostSequencer();
        sequencer.RequestShow(Home);

        sequencer.RequestShow(null).Should().BeFalse();
        sequencer.HasPendingView.Should().BeTrue();

        sequencer.RequestFlush(out var released).Should().BeTrue();
        released.Should().BeNull();
    }
}
