namespace Remex.Core.Services;

/// <summary>
/// Holds at most one demand lease and takes or drops it as a condition flips (perf audit P0-10).
/// </summary>
/// <remarks>
/// <para>
/// The samplers run only while a lease is held (<see cref="ITelemetryBroadcaster.AcquireDemand"/>),
/// and most consumers are not "on for my whole life" but "on while some condition holds": a phone's
/// stream while it is not paused, the PC's own UI while a window is showing. Getting that wrong in
/// either direction is silent - a leaked lease keeps the sampler burning forever, a dropped one
/// freezes a dashboard with no error - so the take/drop bookkeeping lives here once rather than as
/// a nullable field and two branches at every call site.
/// </para>
/// <para>
/// NOT THREAD-SAFE, deliberately: each owner drives it from one flow (a connection's stream loop,
/// the UI thread). <see cref="Set"/> is idempotent, so calling it on every pass with the current
/// answer is the intended use. After <see cref="Dispose"/> it never acquires again.
/// </para>
/// </remarks>
public sealed class DemandHold(Func<IDisposable?> acquire) : IDisposable
{
    private IDisposable? _lease;
    private bool _held;
    private bool _disposed;

    /// <summary>Whether a lease is currently held.</summary>
    public bool IsHeld => _held;

    /// <summary>Takes a lease if <paramref name="wanted"/> and none is held; drops it if not.</summary>
    public void Set(bool wanted)
    {
        if (_disposed || wanted == _held) return;

        if (wanted)
        {
            // _held is tracked apart from the lease because acquire may legitimately hand back null
            // (a test double with no gate behind it); that must still count as "held" so the next
            // Set(true) does not ask again on every pass.
            _lease = acquire();
            _held = true;
        }
        else
        {
            var lease = _lease;
            _lease = null;
            _held = false;
            lease?.Dispose();
        }
    }

    /// <summary>Releases any lease held and stops this hold from ever taking another.</summary>
    public void Dispose()
    {
        Set(false);
        _disposed = true;
    }
}
