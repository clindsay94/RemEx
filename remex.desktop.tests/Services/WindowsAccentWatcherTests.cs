using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>Fires timer callbacks only when the test advances it. Synchronous, single-threaded.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            foreach (var timer in _timers.ToArray()) timer.Advance(by);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _due;
            private TimeSpan _period;
            private TimeSpan _elapsed;

            public ManualTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            {
                _callback = callback; _state = state; _due = due; _period = period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime; _period = period; _elapsed = TimeSpan.Zero;
                return true;
            }

            public void Advance(TimeSpan by)
            {
                if (_due == Timeout.InfiniteTimeSpan) return;
                _elapsed += by;
                while (_due != Timeout.InfiniteTimeSpan && _elapsed >= _due)
                {
                    _elapsed -= _due;
                    _callback(_state);
                    if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                    {
                        _due = Timeout.InfiniteTimeSpan;
                        break;
                    }
                    _due = _period;
                }
            }

            public void Dispose() => _due = Timeout.InfiniteTimeSpan;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

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
}
