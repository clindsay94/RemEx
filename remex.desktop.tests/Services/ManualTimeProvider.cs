using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Fires timer callbacks only when the test advances it. Synchronous, single-threaded.
/// </summary>
/// <remarks>
/// SHARED, NOT PRIVATE (RemEx-8twk0.3 review, HIGH finding follow-up). This started as a private
/// nested class inside <see cref="WindowsAccentWatcherTests"/> — moved here once
/// <c>ColorSourceCoordinatorTests</c> also needed a fake <see cref="TimeProvider"/> to build a real
/// <see cref="Remex.Desktop.Services.WindowsAccentWatcher"/> for <c>ColorSourceCoordinator.Apply</c>
/// tests, rather than keeping two copies of the same fake clock in sync by hand.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
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
