namespace Remex.Core.Routines;

// The closed string vocabularies of the routine schema (routines spec §6.3, §6.4, §7.3, §8.8).
// Plain string constants and ordinal comparisons, never enums: an enum would make an unknown value a
// deserialization failure, and a routine the reader does not understand must deserialize and then be
// REJECTED by the validator (unsupported_trigger / unsupported_step), not take a session down with it.
// Every class here is mirrored one for one in Kotlin (RoutineVocabulary.kt).

/// <summary><c>RoutineTrigger.type</c> values (§6.3). Schedule is deferred to 3.x.</summary>
public static class RoutineTriggerTypes
{
    public const string HomeArrive = "home.arrive";
    public const string HomeLeave = "home.leave";
    public const string NfcTap = "nfc.tap";
    public const string Manual = "manual";
    public const string PcSensor = "pc.sensor";
    public const string PcIdle = "pc.idle";
    public const string PcSession = "pc.session";

    public static readonly IReadOnlyList<string> All =
        [HomeArrive, HomeLeave, NfcTap, Manual, PcSensor, PcIdle, PcSession];

    /// <summary>
    /// True for the <c>pc.*</c> triggers, whose routines run on the PC. Every other known trigger runs
    /// on the phone. The runner follows the trigger (L4).
    /// </summary>
    public static bool IsHostRun(string? type) =>
        type is PcSensor or PcIdle or PcSession;

    /// <summary>True when <paramref name="type"/> is one this version knows.</summary>
    public static bool IsKnown(string? type) =>
        type is HomeArrive or HomeLeave or NfcTap or Manual or PcSensor or PcIdle or PcSession;

    /// <summary>Automatic sources, which Pause all stops (§8.7). The others are person-initiated.</summary>
    public static bool IsAutomatic(string? type) =>
        type is HomeArrive or HomeLeave or PcSensor or PcIdle or PcSession;
}

/// <summary><c>RoutineStep.type</c> values (§6.4).</summary>
public static class RoutineStepTypes
{
    public const string Wake = "wake";
    public const string WaitOnline = "waitOnline";
    public const string Delay = "delay";
    public const string Power = "power";
    public const string LaunchApp = "launchApp";
    public const string Media = "media";
    public const string Notify = "notify";

    public static readonly IReadOnlyList<string> All =
        [Wake, WaitOnline, Delay, Power, LaunchApp, Media, Notify];

    public static bool IsKnown(string? type) =>
        type is Wake or WaitOnline or Delay or Power or LaunchApp or Media or Notify;

    /// <summary>Steps that only a phone-run routine may contain (<c>step_not_allowed_on_pc</c>).</summary>
    public static bool IsPhoneOnly(string? type) => type is Wake or WaitOnline;
}

/// <summary>
/// The power verbs a routine <c>power</c> step may name (§6.4, D1, D5).
/// </summary>
/// <remarks>
/// The ten routine verbs are the 8338 script-ingress verbs (<c>CommandVerbs.ScriptIngress</c>) minus
/// <c>WAKEONLAN</c>, which is RESERVED in v1 and rejected with <c>invalid_field</c>: the <c>wake</c>
/// step covers waking the PC, and waking a third machine is a later power-user feature.
/// </remarks>
public static class RoutinePowerVerbs
{
    public const string Shutdown = "SHUTDOWN";
    public const string ForceShutdown = "FORCESHUTDOWN";
    public const string Restart = "RESTART";
    public const string ForceRestart = "FORCERESTART";
    public const string RestartToUefi = "RESTARTTOUEFI";
    public const string SignOut = "SIGNOUT";
    public const string Sleep = "SLEEP";
    public const string Hibernate = "HIBERNATE";
    public const string Lock = "LOCK";
    public const string MonitorOff = "MONITOROFF";

    /// <summary>Reserved in v1 (D5). Never offered, never accepted, never advertised.</summary>
    public const string ReservedWakeOnLan = "WAKEONLAN";

    /// <summary>Every verb a v1 routine may carry, in <c>ScriptIngress</c> order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi, Sleep, Hibernate, SignOut, Lock, MonitorOff];

    /// <summary>
    /// The D1 destructive set: these count down 15 s on the PC whatever started them, a routine may
    /// hold at most one, and in a PC-run routine it must be the last step. LOCK and MONITOROFF are
    /// NOT destructive and never count down.
    /// </summary>
    public static readonly IReadOnlyList<string> Destructive =
        [Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi, SignOut, Sleep, Hibernate];

    /// <summary>The verbs that accept <c>delaySeconds</c>.</summary>
    public static readonly IReadOnlyList<string> Delayable =
        [Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi];

    public static bool IsAllowed(string? verb) =>
        verb is Shutdown or ForceShutdown or Restart or ForceRestart or RestartToUefi
            or SignOut or Sleep or Hibernate or Lock or MonitorOff;

    public static bool IsDestructive(string? verb) =>
        verb is Shutdown or ForceShutdown or Restart or ForceRestart or RestartToUefi
            or SignOut or Sleep or Hibernate;

    public static bool IsDelayable(string? verb) =>
        verb is Shutdown or ForceShutdown or Restart or ForceRestart or RestartToUefi;
}

/// <summary><c>media.mediaAction</c> values.</summary>
public static class RoutineMediaActions
{
    public const string PlayPause = "playPause";
    public const string Next = "next";
    public const string Previous = "previous";

    public static readonly IReadOnlyList<string> All = [PlayPause, Next, Previous];

    public static bool IsKnown(string? action) => action is PlayPause or Next or Previous;
}

/// <summary><c>notify.target</c> values.</summary>
public static class RoutineNotifyTargets
{
    public const string Phone = "phone";
    public const string Pc = "pc";

    public static bool IsKnown(string? target) => target is Phone or Pc;
}

/// <summary><c>pc.sensor.direction</c> values.</summary>
public static class RoutineSensorDirections
{
    public const string Above = "above";
    public const string Below = "below";

    public static bool IsKnown(string? direction) => direction is Above or Below;
}

/// <summary><c>pc.session.sessionState</c> values.</summary>
public static class RoutineSessionStates
{
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";

    public static bool IsKnown(string? state) => state is Locked or Unlocked;
}

/// <summary><c>routine_sync_result.status</c> values (§7.3.2).</summary>
public static class RoutineSyncStatuses
{
    public const string Ok = "ok";
    public const string Partial = "partial";
    public const string StaleRevision = "stale_revision";
    public const string RevisionConflict = "revision_conflict";
    public const string SchemaTooNew = "schema_too_new";
    public const string PayloadTooLarge = "payload_too_large";
    public const string BlockedByPc = "blocked_by_pc";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
}

/// <summary><c>routine_step_result.outcome</c> values (§7.3.4).</summary>
public static class RoutineStepOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Simulated = "simulated";
    public const string InProgress = "in_progress";
}

/// <summary><c>RoutineRun.outcome</c> values (§8.8).</summary>
public static class RoutineRunOutcomes
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
    public const string Interrupted = "interrupted";
}

/// <summary><c>RoutineRun.steps[].status</c> values (§8.8).</summary>
public static class RoutineStepStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
    public const string Simulated = "simulated";
    public const string Expired = "expired";
}

/// <summary><c>RoutineRun.source</c> and request <c>source</c> values (§8.1, §8.8).</summary>
public static class RoutineRunSources
{
    public const string HomeArrive = "home.arrive";
    public const string HomeLeave = "home.leave";
    public const string NfcTap = "nfc.tap";
    public const string ManualApp = "manual.app";
    public const string ManualShortcut = "manual.shortcut";
    public const string ManualWidget = "manual.widget";
    public const string ManualPcRunNow = "manual.pcRunNow";
    public const string PcSensor = "pc.sensor";
    public const string PcIdle = "pc.idle";
    public const string PcSession = "pc.session";
}

/// <summary><c>RoutineRun.origin</c> values: where the runner ran.</summary>
public static class RoutineRunOrigins
{
    public const string Phone = "phone";
    public const string Pc = "pc";
}

/// <summary><c>RoutineRun.attributes</c> values (§8.8). Each is also a reason code.</summary>
public static class RoutineRunAttributes
{
    public const string Simulated = RoutineReasonCodes.Simulated;
    public const string CountdownUnseen = RoutineReasonCodes.CountdownUnseen;
    public const string DeferredByOs = RoutineReasonCodes.DeferredByOs;
    public const string DryRun = RoutineReasonCodes.DryRun;
    public const string NotifyQueued = RoutineReasonCodes.NotifyQueued;
    public const string BackgroundRestricted = RoutineReasonCodes.BackgroundRestricted;
}

/// <summary><c>cancelledBy</c> values.</summary>
public static class RoutineCancelledBy
{
    public const string Pc = "pc";
    public const string Phone = "phone";
    public const string Pause = "pause";
}

/// <summary><c>routine_notify.kind</c> values (§7.3.5).</summary>
public static class RoutineNotifyKinds
{
    public const string Step = "step";
    public const string Countdown = "countdown";
}

/// <summary><c>routine_cancel.reason</c> values (§7.3.7).</summary>
public static class RoutineCancelReasons
{
    public const string User = "user";
    public const string Pause = "pause";
}
