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
    /// <param name="ownerClientId">The owner phone (T17: nothing else ever receives it).</param>
    /// <param name="notify">The message.</param>
    /// <param name="stepIndex">
    /// The <c>notify(phone)</c> step it came from, so an expiry can mark that step <c>expired</c> in the
    /// run's history (§8.8). Null for a countdown heads-up, which is never queued.
    /// </param>
    Task<RoutinePhoneNotifyOutcome> NotifyAsync(string ownerClientId, RoutineNotifyPayload notify, int? stepIndex = null);
}

/// <summary>
/// Live delivery only, with no queue: an undelivered message is reported as such (<c>notify_expired</c> on
/// the step) rather than claimed as queued. Production uses <see cref="RoutineNotifyQueue"/> (routines S5);
/// this stays for tests that pin the runner's handling of an undelivered message.
/// </summary>
public sealed class LiveOnlyRoutinePhoneNotifier(IRoutinePhoneChannel channel) : IRoutinePhoneNotifier
{
    /// <inheritdoc />
    public async Task<RoutinePhoneNotifyOutcome> NotifyAsync(string ownerClientId, RoutineNotifyPayload notify, int? stepIndex = null) =>
        await channel.TrySendAsync(ownerClientId, new RemexMessage { Type = MessageTypes.RoutineNotify, RoutineNotify = notify })
            ? RoutinePhoneNotifyOutcome.Delivered
            : RoutinePhoneNotifyOutcome.NotDelivered;
}

/// <summary>
/// The loop guard (routines spec §8.1, T9, Q9): an edge a routine's own step caused triggers nothing.
/// </summary>
/// <remarks>
/// <para>
/// A routine's <c>LOCK</c> produces a <c>pc.session locked</c> edge a moment later; without this, a
/// "lock → run X" routine and a "run X → lock" routine would feed each other. Only the power VERB ISSUE is
/// marked (<see cref="CausalityMarkingPowerExecutor"/>), from every routine path (a PC run's, or a phone
/// run's <c>routine_step_request</c>), while it runs and for <see cref="Window"/> after; a trigger source
/// asks at the moment an edge arrives.
/// </para>
/// <para>
/// <b>NOT THE WHOLE STEP.</b> A destructive step spends 15 s in its countdown before the verb, and a person
/// locking the PC during those seconds is a real edge. Marking the step would swallow it; marking the verb
/// cannot. A suppressed edge is still recorded (<c>flap_suppressed</c> / <c>caused_by_run</c>).
/// </para>
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

/// <summary>
/// <see cref="IRoutinePowerExecutor"/> that marks <see cref="RoutineCausality"/> around the verb itself: the
/// only routine action that produces a lock or unlock edge, and the narrowest window that catches it.
/// </summary>
public sealed class CausalityMarkingPowerExecutor(IRoutinePowerExecutor inner, RoutineCausality causality) : IRoutinePowerExecutor
{
    /// <inheritdoc />
    public async Task<Remex.Core.Services.Command.SharedCommandVerbs.Outcome?> ExecuteAsync(string verb, int? delaySeconds)
    {
        using (causality.BeginStep())
        {
            return await inner.ExecuteAsync(verb, delaySeconds);
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

    public static RemexMessage Notify(RoutineNotifyPayload notify) => new()
    {
        Type = MessageTypes.RoutineNotify,
        RoutineNotify = notify,
    };

    /// <summary><c>ownerClientId</c> is PC-only and never sent to the phone (§8.8).</summary>
    public static RoutineRun ForPhone(RoutineRun run) => run with { OwnerClientId = null };
}
