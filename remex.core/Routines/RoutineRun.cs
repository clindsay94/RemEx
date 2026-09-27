using System.Text.Json.Serialization;

namespace Remex.Core.Routines;

/// <summary>
/// One run of a routine, as history shows it (routines spec §8.8, RemEx-pp0rt.3).
/// </summary>
/// <remarks>
/// <para>
/// The same record on both sides, and exactly what the history UI renders. The PC adds
/// <see cref="Seq"/> and <see cref="OwnerClientId"/>; phone-run records leave both null.
/// </para>
/// <para>
/// <b><see cref="OwnerClientId"/> is never sent to the phone.</b> The host keeps it in
/// <c>routine_runs.json</c> and must clear it (<c>run with { OwnerClientId = null }</c>) before a record
/// goes into a <c>routine_run_report</c>; null is omitted on the wire.
/// </para>
/// <para>No member is <c>required</c> (spec T18).</para>
/// </remarks>
public sealed record RoutineRun
{
    public string? RunId { get; init; }

    /// <summary>PC only: last-modified sequence, the <c>runCursor</c> the phone pages by (§7.3.6).</summary>
    public long? Seq { get; init; }

    /// <summary>PC only; never sent to the phone.</summary>
    public string? OwnerClientId { get; init; }

    public string? RoutineId { get; init; }

    /// <summary>Name snapshot at run time.</summary>
    public string? RoutineName { get; init; }

    /// <summary><c>Routine.revision</c> at run time; drives "Edited since this run".</summary>
    public long RoutineRevision { get; init; }

    /// <summary><see cref="RoutineRunOrigins"/>: where the runner ran.</summary>
    public string? Origin { get; init; }

    public string? HostIdentity { get; init; }

    /// <summary><see cref="RoutineRunSources"/>.</summary>
    public string? Source { get; init; }

    /// <summary>An in-app Test (D7): destructive steps were simulated.</summary>
    public bool TestRun { get; init; }

    public RoutineRunSourceDetail? SourceDetail { get; init; }

    public long TriggeredAtUnixMs { get; init; }

    public long StartedAtUnixMs { get; init; }

    /// <summary>Null while running.</summary>
    public long? EndedAtUnixMs { get; init; }

    /// <summary><see cref="RoutineRunOutcomes"/>.</summary>
    public string? Outcome { get; init; }

    /// <summary><see cref="RoutineReasonCodes"/>; <c>ok</c> on success.</summary>
    public string? ReasonCode { get; init; }

    public RoutineReasonArgs? ReasonArgs { get; init; }

    /// <summary><see cref="RoutineCancelledBy"/>.</summary>
    public string? CancelledBy { get; init; }

    /// <summary><see cref="RoutineRunAttributes"/>. No null elements (the Kotlin reader refuses them).</summary>
    [JsonConverter(typeof(Remex.Core.Messages.Routines.NoNullElementsListConverter<string>))]
    public List<string>? Attributes { get; init; }

    [JsonConverter(typeof(Remex.Core.Messages.Routines.NoNullElementsListConverter<RoutineRunStep>))]
    public List<RoutineRunStep>? Steps { get; init; }

    /// <summary>Present for a run with a destructive step.</summary>
    public RoutineRunCountdown? Countdown { get; init; }
}

/// <summary>What fired the run, for display (§8.8). Only the keys the source uses are set.</summary>
public sealed record RoutineRunSourceDetail
{
    /// <summary><c>pc.sensor</c>.</summary>
    public string? SensorName { get; init; }

    /// <summary><c>pc.sensor</c>.</summary>
    public double? Value { get; init; }

    /// <summary><c>pc.sensor</c>.</summary>
    public string? Unit { get; init; }

    /// <summary><c>pc.idle</c>.</summary>
    public int? IdleMinutes { get; init; }

    /// <summary><c>pc.session</c>.</summary>
    public string? SessionState { get; init; }

    /// <summary><c>home.*</c>.</summary>
    public string? HomeLabel { get; init; }
}

/// <summary>
/// Placeholder values for a reason code's message (§8.8, §10.1). Only the keys the message uses are set.
/// </summary>
/// <remarks>
/// §8.8 lists <c>pc, phone, app, sensor, duration, action, detail</c>; §10.1 also uses
/// <c>{routine}</c>, <c>{date}</c> and <c>{n}</c>, so those are carried too. All are display strings,
/// already formatted by the sender.
/// </remarks>
public sealed record RoutineReasonArgs
{
    public string? Pc { get; init; }
    public string? Phone { get; init; }
    public string? App { get; init; }
    public string? Sensor { get; init; }
    public string? Duration { get; init; }
    public string? Action { get; init; }
    public string? Detail { get; init; }
    public string? Routine { get; init; }
    public string? Date { get; init; }
    public string? N { get; init; }
}

/// <summary>One step of a <see cref="RoutineRun"/> (§8.8).</summary>
public sealed record RoutineRunStep
{
    public int Index { get; init; }

    /// <summary>The step <c>type</c>.</summary>
    public string? Kind { get; init; }

    /// <summary><see cref="RoutineStepStatuses"/>.</summary>
    public string? Status { get; init; }

    public long? StartedAtUnixMs { get; init; }

    public long? EndedAtUnixMs { get; init; }

    public string? ReasonCode { get; init; }

    public RoutineReasonArgs? ReasonArgs { get; init; }
}

/// <summary>The destructive-step countdown of a run (§8.6, §8.8).</summary>
public sealed record RoutineRunCountdown
{
    /// <summary>Whether the PC countdown surface was actually displayed.</summary>
    public bool Shown { get; init; }

    public long StartedAtUnixMs { get; init; }

    /// <summary><see cref="RoutineCancelledBy"/>; null when it ran out.</summary>
    public string? CancelledBy { get; init; }
}
