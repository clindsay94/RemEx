using System.Text.Json.Serialization;
using Remex.Core.Routines;

namespace Remex.Core.Messages.Routines;

// The nine routines wire payloads (routines spec §7.1-§7.3, RemEx-pp0rt.3), one per RemexMessage slot.
// camelCase JSON via the serializer context; null members are omitted on the wire.
//
// NO MEMBER IS `required` (spec T18). A missing field defaults; the handler and RoutineValidator decide
// what is acceptable. A field of the wrong JSON type does not throw out of RemexMessage deserialization
// either: every slot carries LenientRoutinePayloadConverter, which turns an unreadable payload into a
// null slot on a still-valid envelope.

/// <summary><c>routines_sync</c>, phone → host (§7.3.1).</summary>
public sealed record RoutinesSyncPayload
{
    /// <summary>The phone's <see cref="RoutineSchema.CurrentVersion"/>.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>The phone's monotonic revision for THIS host (starts at 1).</summary>
    public long Revision { get; init; }

    /// <summary>Phone-side Pause all (D4).</summary>
    public bool Paused { get; init; }

    /// <summary>The FULL PC-triggered subset for this host (0-16). Full state, not a delta.</summary>
    [JsonConverter(typeof(LenientRoutineListConverter))]
    public List<Routine>? Routines { get; init; }

    /// <summary>Highest host run <c>seq</c> the phone has stored for this host (0 = none).</summary>
    public long RunCursor { get; init; }

    /// <summary>True only in the forget-PC flush: the host deletes this owner's state.</summary>
    public bool Forget { get; init; }

    /// <summary>Display only.</summary>
    public long SentAtUnixMs { get; init; }
}

/// <summary><c>routine_sync_result</c>, host → phone (§7.3.2).</summary>
public sealed record RoutineSyncResultPayload
{
    /// <summary>Echo of the request revision; equals <see cref="StoredRevision"/> when unsolicited.</summary>
    public long Revision { get; init; }

    /// <summary>The revision the host now holds for this owner.</summary>
    public long StoredRevision { get; init; }

    /// <summary><see cref="RoutineSyncStatuses"/>.</summary>
    public string? Status { get; init; }

    /// <summary>One per routine in the request; empty unless <c>ok</c>/<c>partial</c>.</summary>
    [JsonConverter(typeof(NoNullElementsListConverter<RoutineSyncItemResult>))]
    public List<RoutineSyncItemResult>? Results { get; init; }

    /// <summary>PC-side Pause all (PC-wide, all owners).</summary>
    public bool HostPaused { get; init; }

    /// <summary>This phone's <c>paused</c> as the PC applied it.</summary>
    public bool OwnerPaused { get; init; }

    /// <summary>Sent without a request because PC-side state changed.</summary>
    public bool Unsolicited { get; init; }

    /// <summary>Routine ids the PC user switched off on the PC.</summary>
    [JsonConverter(typeof(NoNullElementsListConverter<string>))]
    public List<string>? PcDisabled { get; init; }

    /// <summary><c>owner_absent</c> when the set was auto-suspended, else null.</summary>
    public string? OwnerSuspended { get; init; }

    /// <summary>Current idle source id, or null = unavailable.</summary>
    public string? IdleSource { get; init; }

    /// <summary>Current session source id, or null = unavailable.</summary>
    public string? SessionSource { get; init; }

    /// <summary>Telemetry is available on this host.</summary>
    public bool SensorTrigger { get; init; }
}

/// <summary>One routine's verdict inside <see cref="RoutineSyncResultPayload.Results"/>.</summary>
public sealed record RoutineSyncItemResult
{
    public string? RoutineId { get; init; }

    public bool Accepted { get; init; }

    /// <summary><see cref="RoutineReasonCodes"/>; <c>ok</c> when accepted.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>English diagnostic, already redacted. The phone shows the localized reason.</summary>
    public string? Detail { get; init; }
}

/// <summary><c>routine_step_request</c>, phone → host (§7.3.3).</summary>
public sealed record RoutineStepRequestPayload
{
    /// <summary>UUID of the phone run. <c>(clientId, runId, stepIndex)</c> is the idempotency key.</summary>
    public string? RunId { get; init; }

    public string? RoutineId { get; init; }

    /// <summary>≤ 40, for the PC countdown and notification text.</summary>
    public string? RoutineName { get; init; }

    /// <summary>Trigger id of the run (<c>manual</c> for test runs).</summary>
    public string? TriggerType { get; init; }

    /// <summary>0-11.</summary>
    public int StepIndex { get; init; }

    /// <summary><c>power</c>, <c>launchApp</c>, <c>media</c>, or <c>notify</c> with <c>target = pc</c>.</summary>
    [JsonConverter(typeof(LenientRoutineStepConverter))]
    public RoutineStep? Step { get; init; }

    /// <summary>
    /// D7 test run. The host ENFORCES it: a destructive verb is never executed under it; the countdown
    /// runs and the result is <c>simulated</c>. It can only make the host do less (T22).
    /// </summary>
    public bool TestRun { get; init; }

    /// <summary><see cref="RoutineRunSources"/>; history and display only.</summary>
    public string? Source { get; init; }
}

/// <summary><c>routine_step_result</c>, host → phone (§7.3.4).</summary>
public sealed record RoutineStepResultPayload
{
    public string? RunId { get; init; }

    public int StepIndex { get; init; }

    /// <summary>
    /// <see cref="RoutineStepOutcomes"/>. For a destructive verb <c>succeeded</c> is sent immediately
    /// BEFORE the verb is issued, because the socket dies with the machine.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary><see cref="RoutineReasonCodes"/>; <c>ok</c> on success.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Whether the PC countdown surface was actually displayed.</summary>
    public bool CountdownShown { get; init; }

    /// <summary><see cref="RoutineCancelledBy"/>.</summary>
    public string? CancelledBy { get; init; }

    /// <summary>≤ 120 chars, English, already redacted.</summary>
    public string? Detail { get; init; }
}

/// <summary><c>routine_notify</c>, host → phone (§7.3.5).</summary>
public sealed record RoutineNotifyPayload
{
    /// <summary>UUID; the phone de-duplicates the last 200.</summary>
    public string? NotifyId { get; init; }

    /// <summary><see cref="RoutineNotifyKinds"/>.</summary>
    public string? Kind { get; init; }

    public string? RoutineId { get; init; }

    public string? RoutineName { get; init; }

    public string? RunId { get; init; }

    /// <summary>≤ 40.</summary>
    public string? Title { get; init; }

    /// <summary>≤ 160.</summary>
    public string? Body { get; init; }

    /// <summary>Only for <c>kind = countdown</c>.</summary>
    public long? CountdownEndsAtUnixMs { get; init; }

    public long QueuedAtUnixMs { get; init; }

    /// <summary><c>queuedAtUnixMs + 3600000</c>.</summary>
    public long ExpiresAtUnixMs { get; init; }
}

/// <summary><c>routine_notify_ack</c>, phone → host (§7.3.5).</summary>
public sealed record RoutineNotifyAckPayload
{
    [JsonConverter(typeof(NoNullElementsListConverter<string>))]
    public List<string>? NotifyIds { get; init; }
}

/// <summary><c>routine_run_report</c>, host → phone (§7.3.6).</summary>
public sealed record RoutineRunReportPayload
{
    /// <summary>
    /// Runs with <c>seq &gt; runCursor</c>, oldest first, ≤ 50. <c>ownerClientId</c> is cleared
    /// before a record is put here.
    /// </summary>
    [JsonConverter(typeof(NoNullElementsListConverter<RoutineRun>))]
    public List<RoutineRun>? Runs { get; init; }

    /// <summary>More pages follow.</summary>
    public bool More { get; init; }

    /// <summary>An in-progress update of one running host run; the phone does not advance its cursor.</summary>
    public bool Live { get; init; }
}

/// <summary><c>routine_cancel</c>, phone → host (§7.3.7).</summary>
public sealed record RoutineCancelPayload
{
    public string? RunId { get; init; }

    /// <summary><see cref="RoutineCancelReasons"/>.</summary>
    public string? Reason { get; init; }
}

/// <summary><c>routine_run_request</c>, phone → host (§7.3.8).</summary>
/// <remarks>
/// Names one of the sender's STORED PC-run routines; the PC runs its own copy, so the phone cannot
/// inject a definition this way (T24). There is deliberately no field that can express presence at
/// the PC: a phone request always counts down (T21).
/// </remarks>
public sealed record RoutineRunRequestPayload
{
    /// <summary>UUID chosen by the phone; deduplicated for 10 min.</summary>
    public string? RunId { get; init; }

    public string? RoutineId { get; init; }

    /// <summary>D7: non-destructive steps run for real, destructive ones are simulated.</summary>
    public bool TestRun { get; init; }

    /// <summary><c>manual.app</c>.</summary>
    public string? Source { get; init; }
}
