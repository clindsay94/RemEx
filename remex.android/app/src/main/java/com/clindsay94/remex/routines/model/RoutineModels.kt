package com.clindsay94.remex.routines.model

// The routine models (routines spec §6.2-§6.6, §8.8, RemEx-pp0rt.3). A field-for-field mirror of
// remex.core/Routines/Routine.cs and RoutineRun.cs: the same nullability, because "absent" and
// "present with a default" mean different things to the validator (field_not_allowed), and the same
// JSON names (RoutineJson). Pure JVM.

/** A document of routines: the phone store body and the shape inside the export file. */
data class RoutineSet(
    /** [RoutineSchema.CURRENT_VERSION] when written by this build; 0 = absent. */
    val schemaVersion: Int = 0,
    /** List order is the user's order. */
    val routines: List<Routine>? = null,
)

/** One trigger followed by 1-12 ordered steps (§6.2). */
data class Routine(
    val id: String? = null,
    val name: String? = null,
    val hostIdentity: String? = null,
    val enabled: Boolean = false,
    val revision: Long = 0,
    val appearance: RoutineAppearance? = null,
    val trigger: RoutineTrigger? = null,
    val steps: List<RoutineStep?>? = null,
    val createdAtUnixMs: Long = 0,
    val updatedAtUnixMs: Long = 0,
    /**
     * This routine's JSON could not be read (a field of the wrong JSON type). Never on the wire;
     * the validator answers `invalid_field`. Mirrors C# `Routine.IsMalformed`.
     */
    val isMalformed: Boolean = false,
    /**
     * The original JSON text of a malformed routine, exactly as it arrived. [RoutineJson] writes it
     * back unchanged instead of this lossy placeholder, so a store round trip never overwrites the
     * user's routine with an empty shell (spec §6.7, "kept verbatim"). A malformed routine without it
     * cannot be written. Mirrors C# `Routine.RawJson`.
     */
    val rawJson: String? = null,
)

data class RoutineAppearance(
    val icon: String? = null,
    val color: String? = null,
)

/** One flat record; the fields that apply depend on [type] (§6.3). */
data class RoutineTrigger(
    val type: String? = null,
    val homeId: String? = null,
    val leaveDebounceSeconds: Int? = null,
    val sensorId: String? = null,
    val sensorLabel: String? = null,
    val direction: String? = null,
    val threshold: Double? = null,
    val sustainSeconds: Int? = null,
    val idleMinutes: Int? = null,
    val ignoreWhileMediaPlaying: Boolean? = null,
    val sessionState: String? = null,
    val isMalformed: Boolean = false,
)

/** One flat record; the fields that apply depend on [type] (§6.4). */
data class RoutineStep(
    val type: String? = null,
    val mac: String? = null,
    val broadcastIp: String? = null,
    val port: Int? = null,
    val timeoutSeconds: Int? = null,
    val seconds: Int? = null,
    val verb: String? = null,
    val delaySeconds: Int? = null,
    val appId: String? = null,
    val appLabel: String? = null,
    val mediaAction: String? = null,
    val target: String? = null,
    val title: String? = null,
    val body: String? = null,
    val isMalformed: Boolean = false,
) {
    /** Executed by the host: `power`, `launchApp`, `media`, and `notify` with `target = pc`. */
    val isHostExecuted: Boolean
        get() =
            type == RoutineStepTypes.POWER ||
                type == RoutineStepTypes.LAUNCH_APP ||
                type == RoutineStepTypes.MEDIA ||
                (type == RoutineStepTypes.NOTIFY && target == RoutineNotifyTargets.PC)

    /** A `power` step whose verb is in the D1 destructive set. */
    val isDestructive: Boolean
        get() = type == RoutineStepTypes.POWER && RoutinePowerVerbs.isDestructive(verb)
}

/** One run, as history shows it (§8.8). The PC adds [seq] and [ownerClientId]. */
data class RoutineRun(
    val runId: String? = null,
    val seq: Long? = null,
    /** PC only; the host never sends it to the phone. */
    val ownerClientId: String? = null,
    val routineId: String? = null,
    val routineName: String? = null,
    val routineRevision: Long = 0,
    val origin: String? = null,
    val hostIdentity: String? = null,
    val source: String? = null,
    val testRun: Boolean = false,
    val sourceDetail: RoutineRunSourceDetail? = null,
    val triggeredAtUnixMs: Long = 0,
    val startedAtUnixMs: Long = 0,
    val endedAtUnixMs: Long? = null,
    val outcome: String? = null,
    val reasonCode: String? = null,
    val reasonArgs: RoutineReasonArgs? = null,
    val cancelledBy: String? = null,
    val attributes: List<String>? = null,
    val steps: List<RoutineRunStep>? = null,
    val countdown: RoutineRunCountdown? = null,
)

data class RoutineRunSourceDetail(
    val sensorName: String? = null,
    val value: Double? = null,
    val unit: String? = null,
    val idleMinutes: Int? = null,
    val sessionState: String? = null,
    val homeLabel: String? = null,
)

/** Placeholder values for a reason code's message; only the keys the message uses are set. */
data class RoutineReasonArgs(
    val pc: String? = null,
    val phone: String? = null,
    val app: String? = null,
    val sensor: String? = null,
    val duration: String? = null,
    val action: String? = null,
    val detail: String? = null,
    val routine: String? = null,
    val date: String? = null,
    val n: String? = null,
)

data class RoutineRunStep(
    val index: Int = 0,
    val kind: String? = null,
    val status: String? = null,
    val startedAtUnixMs: Long? = null,
    val endedAtUnixMs: Long? = null,
    val reasonCode: String? = null,
    val reasonArgs: RoutineReasonArgs? = null,
)

data class RoutineRunCountdown(
    val shown: Boolean = false,
    val startedAtUnixMs: Long = 0,
    val cancelledBy: String? = null,
)

// ── Wire payloads (§7.3), one per RemexMessage slot. Mirrors RoutinePayloads.cs. ──

/** `routines_sync`, phone -> host (§7.3.1). */
data class RoutinesSyncPayload(
    val schemaVersion: Int = 0,
    val revision: Long = 0,
    val paused: Boolean = false,
    val routines: List<Routine>? = null,
    val runCursor: Long = 0,
    val forget: Boolean = false,
    val sentAtUnixMs: Long = 0,
)

/** `routine_sync_result`, host -> phone (§7.3.2). */
data class RoutineSyncResultPayload(
    val revision: Long = 0,
    val storedRevision: Long = 0,
    val status: String? = null,
    val results: List<RoutineSyncItemResult>? = null,
    val hostPaused: Boolean = false,
    val ownerPaused: Boolean = false,
    val unsolicited: Boolean = false,
    val pcDisabled: List<String>? = null,
    val ownerSuspended: String? = null,
    val idleSource: String? = null,
    val sessionSource: String? = null,
    val sensorTrigger: Boolean = false,
)

data class RoutineSyncItemResult(
    val routineId: String? = null,
    val accepted: Boolean = false,
    val reasonCode: String? = null,
    val detail: String? = null,
)

/** `routine_step_request`, phone -> host (§7.3.3). */
data class RoutineStepRequestPayload(
    val runId: String? = null,
    val routineId: String? = null,
    val routineName: String? = null,
    val triggerType: String? = null,
    val stepIndex: Int = 0,
    val step: RoutineStep? = null,
    /** D7: the host never executes a destructive verb for a test run (T22). */
    val testRun: Boolean = false,
    val source: String? = null,
)

/** `routine_step_result`, host -> phone (§7.3.4). */
data class RoutineStepResultPayload(
    val runId: String? = null,
    val stepIndex: Int = 0,
    val outcome: String? = null,
    val reasonCode: String? = null,
    val countdownShown: Boolean = false,
    val cancelledBy: String? = null,
    val detail: String? = null,
)

/** `routine_notify`, host -> phone (§7.3.5). */
data class RoutineNotifyPayload(
    val notifyId: String? = null,
    val kind: String? = null,
    val routineId: String? = null,
    val routineName: String? = null,
    val runId: String? = null,
    val title: String? = null,
    val body: String? = null,
    val countdownEndsAtUnixMs: Long? = null,
    val queuedAtUnixMs: Long = 0,
    val expiresAtUnixMs: Long = 0,
)

/** `routine_notify_ack`, phone -> host (§7.3.5). */
data class RoutineNotifyAckPayload(
    val notifyIds: List<String>? = null,
)

/** `routine_run_report`, host -> phone (§7.3.6). */
data class RoutineRunReportPayload(
    val runs: List<RoutineRun>? = null,
    val more: Boolean = false,
    val live: Boolean = false,
)

/** `routine_cancel`, phone -> host (§7.3.7). */
data class RoutineCancelPayload(
    val runId: String? = null,
    val reason: String? = null,
)

/**
 * `routine_run_request`, phone -> host (§7.3.8). Names a STORED routine (T24); nothing here can
 * express presence at the PC, so a phone request always counts down (T21).
 */
data class RoutineRunRequestPayload(
    val runId: String? = null,
    val routineId: String? = null,
    val testRun: Boolean = false,
    val source: String? = null,
)
