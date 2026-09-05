using System;
using System.Diagnostics;
using System.Threading;
using Remex.Core.Guards;

namespace Remex.Desktop.Services;

/// <summary>
/// Follows the Windows accent colour while the window is visible (RemEx-ddynd): a two-second poll
/// of the DWM registry key, paused while hidden, re-read immediately on becoming visible again.
/// </summary>
/// <remarks>
/// A POLL, NOT A REGISTRY CHANGE NOTIFICATION, on purpose. <c>RegNotifyChangeKeyValue</c> needs a
/// dedicated thread per watched key and its own resume-from-sleep handling; a two-second poll of
/// one dword is cheaper than the thread, meets the spec's latency budget, and stops costing
/// anything at all while the window is hidden. Resume from sleep needs no special case: the timer
/// resumes with the process, and the window's <c>Activated</c> hook calls <see cref="PollNow"/>.
/// <para>
/// THE TIMER COMES FROM A <see cref="TimeProvider"/> so the two-second budget and the stop-while-
/// hidden rule are pinned by tests on a fake clock rather than a wall clock. Callbacks arrive on a
/// thread-pool thread; the consumer marshals to the UI thread.
/// </para>
/// </remarks>
public sealed class WindowsAccentWatcher : IDisposable
{
    /// <summary>The spec's budget: a Settings change shows within two seconds.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly Func<string?> _readAccent;
    private readonly ITimer _timer;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private string? _last;
    private bool _visible;
    private bool _running;

    /// <summary>Raised with the new "#RRGGBB" when the accent differs from the last one seen.</summary>
    public event Action<string>? AccentChanged;

    /// <summary>The last accent successfully read, or null before <see cref="Start"/>.</summary>
    public string? Current { get { lock (_gate) return _last; } }

    public WindowsAccentWatcher(Func<string?> readAccent, TimeProvider timeProvider, TimeSpan? interval = null)
    {
        _readAccent = Guard.NotNull(readAccent);
        _interval = interval ?? DefaultInterval;
        _timer = Guard.NotNull(timeProvider).CreateTimer(
            _ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Reads the accent once to seed <see cref="Current"/> and arms the poll (if visible).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _running = true;
            _last = TryRead() ?? _last;
        }
        ApplySchedule();
    }

    /// <summary>Tell the watcher whether the window can be seen. Hidden stops the poll; visible re-reads at once.</summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            if (_visible == visible) return;
            _visible = visible;
        }
        ApplySchedule();
        if (visible) Poll();
    }

    /// <summary>An out-of-band read — the window's Activated hook uses it after a resume.</summary>
    public void PollNow() => Poll();

    private void ApplySchedule()
    {
        bool on;
        lock (_gate) on = _running && _visible;
        var period = on ? _interval : Timeout.InfiniteTimeSpan;
        _timer.Change(period, period);
    }

    private void Poll()
    {
        // THE REGISTRY READ HAPPENS OUTSIDE THE GATE (RemEx-8twk0.3 review, LOW). SetVisible(true)
        // and PollNow() call this on the UI thread, so a pool-thread poll holding _gate across
        // TryRead() could block the UI thread behind a registry read. The gate is only ever held
        // long enough to check _running or to compare-and-update _last.
        lock (_gate)
        {
            if (!_running) return;
        }

        var hex = TryRead();

        lock (_gate)
        {
            if (!_running) return;
            if (hex is null || string.Equals(hex, _last, StringComparison.OrdinalIgnoreCase)) return;
            _last = hex;
        }
        AccentChanged?.Invoke(hex);
    }

    /// <summary>A failed read is "no answer": the last seed stands (spec section 9).</summary>
    private string? TryRead()
    {
        try
        {
            return _readAccent();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WindowsAccentWatcher: accent read failed — {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        // STOP RAISING BEFORE DISPOSING THE TIMER (RemEx-8twk0.3 review, LOW). An in-flight
        // thread-pool callback can still be inside Poll() when Dispose() runs on the UI thread;
        // without this, it could still see _running true and raise AccentChanged after disposal.
        lock (_gate) { _running = false; }
        _timer.Dispose();
    }
}
