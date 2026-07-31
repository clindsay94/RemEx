using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Remex.Agent.Services.RemoteDesktop;

/// <summary>
/// High-accuracy tick pacer that beats the Windows OS timer floor (~15.6 ms).
///
/// REGRESSION GUARD — single source of truth for remote-desktop stream/cursor pacing. A bare
/// <c>Task.Delay</c> rounds UP to the OS timer resolution (~15.6 ms on Windows): an 8.33 ms wait
/// (120 FPS) oversleeps to ~15.6 ms (capping near 60 FPS), and an 11 ms wait (90 Hz cursor)
/// oversleeps to ~15.6 ms (capping near 64 Hz). To hit the real target we coarse-sleep the bulk of
/// the interval, then busy-spin the final ~2 ms with <c>Thread.SpinWait</c> for sub-millisecond
/// accuracy. No global <c>timeBeginPeriod</c> — the raised resolution is scoped to this object's
/// own timer, so it does not change the timer resolution of the whole machine. If you reintroduce a
/// plain <c>Task.Delay</c> in a stream/cursor loop, the frame rate / cursor Hz will SILENTLY
/// regress; keep the timing here. (The durable record is the PrecisionPacer entry in AGENTS.md;
/// docs/REMOTE_DESKTOP_PERFORMANCE.md was deleted and should not be searched for.)
///
/// Uses an absolute timeline so a per-tick overrun shortens the FOLLOWING wait instead of
/// accumulating drift. Call <see cref="Reset"/> after a pause/backoff so the loop doesn't burst
/// through a backlog of "missed" ticks on recovery.
/// </summary>
/// <remarks>
/// THE SPIN MARGIN USED TO BE 16 ms, AND THAT WAS THE WHOLE INTERVAL. At 90 Hz (11.1 ms) and 120 Hz
/// (8.3 ms) the coarse-sleep branch could never run, so the pacer spun <c>Thread.SpinWait</c> for the
/// entire interval on a thread-pool thread. The cursor loop alone therefore burned ~100% of a core
/// for as long as a stream was open — costing boost budget on the same machine that is running NVENC,
/// and holding a pool thread hostage (RemEx-ccen).
/// <para>
/// The margin can only shrink if the coarse sleep is actually accurate to a couple of milliseconds,
/// which on Windows means a high-resolution waitable timer
/// (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Windows 10 1803+). Where that is unavailable the
/// pacer falls back to the old 16 ms margin and <c>Task.Delay</c> — the previous behaviour exactly,
/// because a smaller margin with a 15.6 ms-granular sleep would OVERSHOOT the target and drop the
/// frame rate, which is worse than burning the core.
/// </para>
/// </remarks>
/// <remarks>
/// NOT thread-safe, and single-waiter by construction: one timer and one event are shared per pacer,
/// so two concurrent <see cref="WaitForNextTickAsync"/> calls on the same instance would have one
/// consume the other's signal. Both call sites own their own pacer and wait from one loop. This was
/// already true of the pure-spin version; the consequence is just worse now (a stalled wait rather
/// than merely wrong pacing), which is why it is written down.
/// </remarks>
internal sealed class PrecisionPacer : IDisposable
{
    /// <summary>How a coarse sleep ended.</summary>
    private enum CoarseWaitResult
    {
        /// <summary>The wait elapsed (or timed out into a late tick); carry on to the spin tail.</summary>
        Completed,

        /// <summary>Cancellation was requested; the caller should stop.</summary>
        Cancelled,

        /// <summary>No high-resolution timer, or arming one failed: fall back to <c>Task.Delay</c>.</summary>
        Unavailable,
    }

    /// <summary>Margin when the coarse sleep is only accurate to the OS timer floor (~15.6 ms).</summary>
    private const double FallbackSpinMarginMs = 16.0;

    /// <summary>
    /// Margin when the coarse sleep is accurate to ~1 ms. Still non-zero: the last stretch is spun
    /// because that is what buys sub-millisecond tick accuracy, and 2 ms of spin per tick is ~18% of
    /// a core at 90 Hz rather than 100%.
    /// </summary>
    private const double HighResolutionSpinMarginMs = 2.0;

    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private readonly HighResolutionWaitableTimer? _waitableTimer;
    private readonly double _spinMarginMs;
    private double _nextTickMs;

    public PrecisionPacer()
    {
        _waitableTimer = HighResolutionWaitableTimer.TryCreate();

        // Linux's Task.Delay is already ~1 ms granular, so it earns the small margin without needing
        // a special timer; Windows only earns it with one.
        _spinMarginMs = _waitableTimer is not null || !OperatingSystem.IsWindows()
            ? HighResolutionSpinMarginMs
            : FallbackSpinMarginMs;
    }

    /// <summary>The spin margin actually in force. Exposed for tests and diagnostics.</summary>
    internal double SpinMarginMs => _spinMarginMs;

    /// <summary>Whether a high-resolution waitable timer backs the coarse sleep.</summary>
    internal bool UsesHighResolutionTimer => _waitableTimer is not null;

    /// <summary>
    /// Total milliseconds spent in the busy-spin tail since construction.
    /// </summary>
    /// <remarks>
    /// This is the quantity RemEx-ccen is about, and it is accumulated rather than inferred because
    /// the obvious alternative does not work: <c>Process.TotalProcessorTime</c> is process-WIDE, so a
    /// test built on it measures whatever else the test host is doing and reports ~100% no matter how
    /// the pacer behaves. Two extra <c>Stopwatch</c> reads per tick, against a loop that already
    /// reads the same stopwatch on every spin iteration.
    /// </remarks>
    internal double SpinMsAccumulated { get; private set; }

    /// <summary>
    /// Waits until the next tick, <paramref name="intervalMs"/> after the previous target. Returns
    /// <c>false</c> if cancellation was requested during the wait (caller should break), <c>true</c>
    /// otherwise. Never throws on cancellation.
    /// </summary>
    public async Task<bool> WaitForNextTickAsync(double intervalMs, CancellationToken ct)
    {
        _nextTickMs += intervalMs;

        double remainingMs = _nextTickMs - _timer.Elapsed.TotalMilliseconds;
        if (remainingMs > _spinMarginMs)
        {
            double coarseMs = remainingMs - _spinMarginMs;

            var outcome = _waitableTimer is { } timer
                ? await timer.WaitAsync(coarseMs, ct)
                : CoarseWaitResult.Unavailable;

            if (outcome == CoarseWaitResult.Cancelled) return false;

            if (outcome == CoarseWaitResult.Unavailable)
            {
                try { await Task.Delay((int)coarseMs, ct); }
                catch (OperationCanceledException) { return false; }
            }
        }

        // SpinWait avoids both the OS sleep floor and a hot 100% loop. If the loop already overran
        // (remaining <= 0), the condition is false and we return immediately without spinning.
        double spinStartMs = _timer.Elapsed.TotalMilliseconds;
        while (!ct.IsCancellationRequested && _timer.Elapsed.TotalMilliseconds < _nextTickMs)
        {
            Thread.SpinWait(64);
        }

        SpinMsAccumulated += _timer.Elapsed.TotalMilliseconds - spinStartMs;

        return !ct.IsCancellationRequested;
    }

    /// <summary>
    /// Re-anchors the timeline to "now" so the next tick is a full interval away. Call after a
    /// backoff/pause to avoid bursting through missed ticks on recovery.
    /// </summary>
    public void Reset() => _nextTickMs = _timer.Elapsed.TotalMilliseconds;

    public void Dispose() => _waitableTimer?.Dispose();

    /// <summary>
    /// A Windows high-resolution waitable timer, awaited without blocking a thread.
    /// </summary>
    /// <remarks>
    /// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> gives this ONE timer ~0.5 ms accuracy without
    /// calling <c>timeBeginPeriod</c>, which would raise the timer resolution for the entire system
    /// and increase idle power draw for every other process on the machine. The flag needs Windows 10
    /// 1803; on anything older <see cref="TryCreate"/> returns null and the pacer keeps its old
    /// behaviour rather than degrading the frame rate.
    /// </remarks>
    private sealed class HighResolutionWaitableTimer : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x1F0003;

        private readonly SafeWaitHandle _handle;
        private readonly ManualResetEvent _waitHandle;

        private HighResolutionWaitableTimer(SafeWaitHandle handle)
        {
            _handle = handle;

            // Wrapping the timer in a WaitHandle is what lets ThreadPool.RegisterWaitForSingleObject
            // observe it, so the wait costs no thread of its own.
            _waitHandle = new ManualResetEvent(false);

            // The SafeWaitHandle SETTER DOES NOT CLOSE the handle it replaces, so the event's own
            // kernel handle has to be released explicitly — otherwise every pacer orphans one until
            // the finalizer happens to run. Measured before this line existed: 500 disposed pacers
            // left 504 handles outstanding.
            _waitHandle.SafeWaitHandle.Dispose();
            _waitHandle.SafeWaitHandle = handle;
        }

        public static HighResolutionWaitableTimer? TryCreate()
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                var handle = CreateWaitableTimerExW(
                    IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                return new HighResolutionWaitableTimer(handle);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>
        /// Waits <paramref name="milliseconds"/>, reporting whether the wait completed, was
        /// cancelled, or could not be armed at all.
        /// </summary>
        /// <remarks>
        /// The three outcomes are distinguished deliberately. Folding "could not arm" into "cancelled"
        /// would make a transient Win32 failure end the stream silently, which the user experiences as
        /// a freeze with nothing logged — so an arm failure degrades to <c>Task.Delay</c> for that tick
        /// instead.
        /// </remarks>
        public Task<CoarseWaitResult> WaitAsync(double milliseconds, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromResult(CoarseWaitResult.Cancelled);

            // Negative due time means relative, in 100 ns units.
            long dueTime = -(long)(milliseconds * 10_000.0);
            if (dueTime >= 0) return Task.FromResult(CoarseWaitResult.Completed);

            if (!SetWaitableTimer(_handle, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                ArmFailures++;
                Debug.WriteLine(
                    $"[PrecisionPacer] SetWaitableTimer failed (Win32 {Marshal.GetLastWin32Error()}); " +
                    "falling back to Task.Delay for this tick.");
                return Task.FromResult(CoarseWaitResult.Unavailable);
            }

            return WaitOneAsync(_waitHandle, milliseconds, ct);
        }

        /// <summary>Count of failed arm attempts. Diagnostic; a healthy stream never increments it.</summary>
        public int ArmFailures { get; private set; }

        private static Task<CoarseWaitResult> WaitOneAsync(WaitHandle handle, double milliseconds, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<CoarseWaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            // BOUNDED, not Timeout.Infinite: if a timer signal were ever missed, an infinite wait
            // would freeze the stream outright, whereas a timeout degrades it to one late tick that
            // the absolute timeline then reclaims. The slack is generous because being early here
            // would defeat the pacing; only a genuinely lost signal should hit it.
            int timeoutMs = (int)Math.Min(int.MaxValue - 1, milliseconds + 250);

            var registration = ThreadPool.RegisterWaitForSingleObject(
                handle,
                static (state, _) =>
                    ((TaskCompletionSource<CoarseWaitResult>)state!).TrySetResult(CoarseWaitResult.Completed),
                completion,
                timeoutMs,
                executeOnlyOnce: true);

            CancellationTokenRegistration cancellation = ct.CanBeCanceled
                ? ct.Register(
                    static state =>
                        ((TaskCompletionSource<CoarseWaitResult>)state!).TrySetResult(CoarseWaitResult.Cancelled),
                    completion)
                : default;

            return AwaitAndUnregister(completion, registration, cancellation);

            static async Task<CoarseWaitResult> AwaitAndUnregister(
                TaskCompletionSource<CoarseWaitResult> completion,
                RegisteredWaitHandle registration,
                CancellationTokenRegistration cancellation)
            {
                try
                {
                    return await completion.Task;
                }
                finally
                {
                    // Unregister BEFORE the handle can be disposed, or the pool would signal a dead
                    // handle. Both are cheap and must happen on every path, including cancellation.
                    registration.Unregister(null);
                    cancellation.Dispose();
                }
            }
        }

        public void Dispose()
        {
            _waitHandle.Dispose();   // owns and closes _handle
        }

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(
            IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(
            SafeWaitHandle hTimer,
            in long pDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            [MarshalAs(UnmanagedType.Bool)] bool fResume);
    }
}
