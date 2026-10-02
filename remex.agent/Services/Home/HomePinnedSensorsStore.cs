using Remex.Core.Models;
using Remex.Core.Services.Home;
using Remex.Core.Validation;

namespace Remex.Agent.Services.Home;

/// <summary>
/// The host's live <see cref="IHomePinnedSensorsStore"/> (RemEx-wqo7a.5). One instance per running
/// agent, registered as a singleton so <c>PingPongHandler</c> (phone side) and the desktop's
/// <c>HomePinsBridge</c> (PC side, via <c>App.EmbeddedHostServices</c>) share it.
/// </summary>
/// <remarks>
/// <para>
/// IN MEMORY ONLY, ON PURPOSE. The durable list is <c>DashboardProfile.PinnedSensorIds</c> in the
/// desktop's own profile file; this holds the latest snapshot the desktop published so a phone that
/// connects later can be told it. The desktop republishes on its own startup, so nothing is lost by
/// a restart, and keeping a second copy on disk would be a second owner waiting to disagree with the
/// first.
/// </para>
/// <para>
/// A PHONE REQUEST IS ALWAYS ANSWERED. The phone toggles optimistically, so a request the desktop
/// refuses (a sensor with no card on the canvas) has to be answered with the unchanged list, or the
/// phone keeps showing a pin the PC never made. The desktop answers every request by publishing
/// after it, accepted or not; the first publish after a request therefore broadcasts even when the
/// lists are unchanged. Every OTHER unchanged publish is still a no-op, which is what keeps a re-save
/// that changed nothing off the wire.
/// </para>
/// <para>
/// Events are raised outside the lock, on whatever thread called in. The revision only ever goes up
/// for the life of the process, which is what lets a phone drop a sync that arrives out of order.
/// </para>
/// </remarks>
public sealed class HomePinnedSensorsStore(TimeProvider? timeProvider = null) : IHomePinnedSensorsStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private HomePinnedSensors _current = new();
    private bool _answerOwed;

    public HomePinnedSensors Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<HomePinnedSensors>? Changed;

    public event Action<HomePinChange, string>? PhoneChangeRequested;

    public bool PublishFromPc(IReadOnlyList<string> pinned, IReadOnlyList<string> pinnable)
    {
        var normalizedPinned = HomePinsValidation.NormalizeNames(pinned);
        var normalizedPinnable = HomePinsValidation.NormalizeNames(pinnable);

        HomePinnedSensors next;
        lock (_gate)
        {
            var unchanged = _current.Revision > 0
                && normalizedPinned.SequenceEqual(_current.SensorNames, StringComparer.Ordinal)
                && normalizedPinnable.SequenceEqual(_current.PinnableSensorNames, StringComparer.Ordinal);
            if (unchanged && !_answerOwed)
            {
                return false;
            }

            _answerOwed = false;
            next = new HomePinnedSensors
            {
                SensorNames = normalizedPinned,
                PinnableSensorNames = normalizedPinnable,
                Revision = _current.Revision + 1,
                UpdatedUtc = _time.GetUtcNow(),
            };
            _current = next;
        }

        Changed?.Invoke(next);
        return true;
    }

    public void RequestFromPhone(HomePinChange change, string clientId)
    {
        // The handler validates before calling; this is the belt to that brace, so a future caller
        // that forgets cannot hand the desktop a blank or control-character name.
        if (!HomePinsValidation.IsValidChange(change))
        {
            return;
        }

        lock (_gate)
        {
            _answerOwed = true;
        }

        PhoneChangeRequested?.Invoke(change, clientId ?? string.Empty);
    }
}
