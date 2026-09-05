using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The Windows-accent poller (RemEx-ddynd): a change is seen within two seconds while the window
/// is visible, nothing is polled while it is hidden, and a failed read keeps the last seed. Driven
/// by a hand-rolled <see cref="TimeProvider"/> so no test waits on a wall clock.
/// </summary>
public class WindowsAccentWatcherTests
{
    // ManualTimeProvider moved to its own shared file (RemEx-8twk0.3 review, HIGH finding
    // follow-up): ColorSourceCoordinatorTests now needs the same fake clock to build a real
    // WindowsAccentWatcher for ColorSourceCoordinator.Apply tests.

    private static (WindowsAccentWatcher Watcher, ManualTimeProvider Clock, List<string> Raised, Func<string?> Source) Build(string? initial)
    {
        var current = initial;
        var clock = new ManualTimeProvider();
        string? Read() => current;
        var watcher = new WindowsAccentWatcher(Read, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;
        return (watcher, clock, raised, () => current);
    }

    [Fact]
    public void AChangeIsRaisedWithinTwoSecondsWhileVisible()
    {
        var current = "#111111";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        current = "#222222";
        clock.Advance(TimeSpan.FromSeconds(2));

        raised.Should().Equal(new[] { "#222222" }, "the spec's latency budget is two seconds");
        watcher.Current.Should().Be("#222222");
    }

    [Fact]
    public void NothingIsPolledWhileHidden()
    {
        var reads = 0;
        var current = "#111111";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => { reads++; return current; }, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();          // one read to seed Current
        watcher.SetVisible(false);
        var readsAfterStart = reads;
        current = "#333333";
        clock.Advance(TimeSpan.FromSeconds(30));

        reads.Should().Be(readsAfterStart, "the poll stops while the window is hidden");
        raised.Should().BeEmpty();
    }

    [Fact]
    public void BecomingVisibleAgainPollsImmediately()
    {
        var current = "#111111";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(false);
        current = "#444444";
        watcher.SetVisible(true);   // no clock advance: resume-from-sleep / restore-from-tray

        raised.Should().Equal("#444444");
    }

    [Fact]
    public void AnUnchangedAccentRaisesNothingHoweverLongItIsWatched()
    {
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => "#111111", clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        clock.Advance(TimeSpan.FromMinutes(5));

        raised.Should().BeEmpty();
    }

    [Fact]
    public void CaseOnlyDifferencesAreNotAChange()
    {
        var current = "#ABCDEF";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        current = "#abcdef";
        clock.Advance(TimeSpan.FromSeconds(2));

        raised.Should().BeEmpty();
    }

    [Fact]
    public void AFailedOrEmptyReadKeepsTheLastSeed()
    {
        var mode = 0;
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(
            () => mode switch { 0 => "#111111", 1 => null, _ => throw new InvalidOperationException("registry gone") },
            clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        mode = 1;
        clock.Advance(TimeSpan.FromSeconds(2));
        mode = 2;
        clock.Advance(TimeSpan.FromSeconds(2));

        raised.Should().BeEmpty("a failed registry read keeps the last seed (spec section 9)");
        watcher.Current.Should().Be("#111111");
    }

    [Fact]
    public void BeforeStartNothingIsPolledEvenWhenVisible()
    {
        var reads = 0;
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => { reads++; return "#111111"; }, clock);

        watcher.SetVisible(true);
        clock.Advance(TimeSpan.FromSeconds(10));

        reads.Should().Be(0);
    }

    [Fact]
    public void DisposeStopsAPendingPollFromRaising()
    {
        // RemEx-8twk0.3 review (LOW): an in-flight thread-pool callback must not still raise
        // AccentChanged once Dispose() has run. Disposing the timer alone does not reproduce
        // this — the fake timer simply stops firing, which would pass even without the fix —
        // so this drives Poll() directly through PollNow(), exactly the way a callback already
        // inside Poll() when Dispose() runs would still see the accent change.
        var current = "#111111";
        var clock = new ManualTimeProvider();
        var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        current = "#222222";
        watcher.Dispose();

        var act = () => watcher.PollNow();

        act.Should().NotThrow("a poll racing Dispose must not throw");
        raised.Should().BeEmpty("nothing should raise once the watcher has been disposed");
    }
}
