using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>Host-initiated sends to an owner phone (unsolicited sync results, run reports, notifies).</summary>
public interface IRoutinePhoneChannel
{
    /// <summary>
    /// True when <paramref name="clientId"/> has a live, identity-proven session that advertised
    /// <c>ClientCapabilities.supportsRoutines</c>. An older phone's router has no <c>routine_</c> forward and
    /// would drop the message in silence (docs/REGRESSION-GUARDS.md), so nothing is sent to it at all.
    /// </summary>
    bool CanReach(string clientId);

    /// <summary>Sends when <see cref="CanReach"/>; false otherwise or when the send failed. Never throws.</summary>
    Task<bool> TrySendAsync(string clientId, RemexMessage message);
}

/// <summary><see cref="IRoutinePhoneChannel"/> over <see cref="ClientSessionRegistry"/>.</summary>
public sealed class SessionRoutinePhoneChannel(ClientSessionRegistry sessions, ILogger<SessionRoutinePhoneChannel> logger)
    : IRoutinePhoneChannel
{
    /// <inheritdoc />
    public bool CanReach(string clientId) => sessions.IsConnected(clientId) && sessions.SupportsRoutines(clientId);

    /// <inheritdoc />
    public async Task<bool> TrySendAsync(string clientId, RemexMessage message)
    {
        if (!CanReach(clientId))
        {
            return false;
        }

        try
        {
            return await sessions.TrySendAsync(clientId, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A {Type} to an owner phone could not be sent.", message.Type);
            return false;
        }
    }
}

/// <summary>How a <c>notify(phone)</c> step's message fared.</summary>
public enum RoutinePhoneNotifyOutcome
{
    /// <summary>Sent live to a connected owner.</summary>
    Delivered,

    /// <summary>Held for later delivery (the S5 queue, §7.3.5): the step succeeds with <c>notify_queued</c>.</summary>
    Queued,

    /// <summary>Not delivered and not held.</summary>
    NotDelivered,
}

/// <summary>Delivers <c>routine_notify</c> to an owner phone.</summary>
public interface IRoutinePhoneNotifier
{
    Task<RoutinePhoneNotifyOutcome> NotifyAsync(string ownerClientId, RoutineNotifyPayload notify);
}

/// <summary>
/// The S4a notifier: live delivery only. The persistent one-hour queue and <c>routine_notify_ack</c> are
/// routines S5 (RemEx-pp0rt), which replaces this registration; until then an undelivered message is
/// reported as such (<c>notify_expired</c> on the step) rather than claimed as queued.
/// </summary>
public sealed class LiveOnlyRoutinePhoneNotifier(IRoutinePhoneChannel channel) : IRoutinePhoneNotifier
{
    /// <inheritdoc />
    public async Task<RoutinePhoneNotifyOutcome> NotifyAsync(string ownerClientId, RoutineNotifyPayload notify) =>
        await channel.TrySendAsync(ownerClientId, new RemexMessage { Type = MessageTypes.RoutineNotify, RoutineNotify = notify })
            ? RoutinePhoneNotifyOutcome.Delivered
            : RoutinePhoneNotifyOutcome.NotDelivered;
}

/// <summary>
/// The loop guard (routines spec §8.1, T9, Q9): an edge a routine's own step caused triggers nothing.
/// </summary>
/// <remarks>
/// A routine's <c>LOCK</c> produces a <c>pc.session locked</c> edge a moment later; without this, a
/// "lock → run X" routine and a "run X → lock" routine would feed each other. Every host-executed step (a
/// PC run's, or a phone run's <c>routine_step_request</c>) marks here while it runs and for
/// <see cref="Window"/> after; a trigger source asks at the moment an edge arrives.
/// </remarks>
public sealed class RoutineCausality(TimeProvider time)
{
    /// <summary>How long after a routine step an edge still counts as caused by it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private int _inFlight;
    private long? _lastMark;

    /// <summary>Marks a step as running; dispose the result when it ends.</summary>
    public IDisposable BeginStep()
    {
        lock (_gate)
        {
            _inFlight++;
            _lastMark = time.GetTimestamp();
        }

        return new Step(this);
    }

    /// <summary>True when an edge arriving now was caused by a routine step.</summary>
    public bool IsCausedByRunNow()
    {
        lock (_gate)
        {
            return _inFlight > 0 || (_lastMark is { } mark && time.GetElapsedTime(mark) <= Window);
        }
    }

    private void End()
    {
        lock (_gate)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            _lastMark = time.GetTimestamp();
        }
    }

    private sealed class Step(RoutineCausality owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.End();
            }
        }
    }
}

/// <summary>Builds the host's outbound routine messages.</summary>
internal static class RoutineMessages
{
    public static RemexMessage Report(List<RoutineRun> runs, bool more, bool live) => new()
    {
        Type = MessageTypes.RoutineRunReport,
        RoutineRunReport = new RoutineRunReportPayload { Runs = runs.Select(ForPhone).ToList(), More = more, Live = live },
    };

    public static RemexMessage SyncResult(RoutineSyncResultPayload result) => new()
    {
        Type = MessageTypes.RoutineSyncResult,
        RoutineSyncResult = result,
    };

    /// <summary><c>ownerClientId</c> is PC-only and never sent to the phone (§8.8).</summary>
    public static RoutineRun ForPhone(RoutineRun run) => run with { OwnerClientId = null };
}
