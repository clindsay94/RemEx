package com.clindsay94.remex.routines.model

// The routine schema constants (routines spec §6.3-§6.7, §10.1, RemEx-pp0rt.3), mirrored one for one
// from remex.core/Routines (RoutineSchema.cs, RoutineLimits.cs, RoutineVocabulary.cs). Pure JVM: no
// Android types, so everything in this package is testable without Robolectric.
//
// Plain string constants, never enums: a routine this build does not understand must still parse,
// be kept verbatim, and be REFUSED by RoutineValidator (unsupported_trigger / unsupported_step).
// The shared fixtures under src/test/resources/routines hold both sides to the same values.

/** Mirrors `RoutineSchema` (C#). */
object RoutineSchema {
    /** The version this build reads and writes. A newer document is read-only on the phone. */
    const val CURRENT_VERSION = 1
}

/** Mirrors `RoutineLimits` (C#), decision D10. */
object RoutineLimits {
    const val MAX_ROUTINES_PER_PHONE = 32
    const val MAX_PC_ROUTINES_PER_HOST = 16
    const val MAX_HOMES = 1
    const val MAX_STEPS = 12
    const val MAX_NAME_LENGTH = 40
    const val MAX_ICON_LENGTH = 32
    const val MIN_LEAVE_DEBOUNCE_SECONDS = 60
    const val MAX_LEAVE_DEBOUNCE_SECONDS = 1800
    const val DEFAULT_LEAVE_DEBOUNCE_SECONDS = 180
    const val MAX_SENSOR_ID_LENGTH = 128
    const val MAX_LABEL_LENGTH = 64
    const val MIN_SUSTAIN_SECONDS = 5
    const val MAX_SUSTAIN_SECONDS = 600
    const val DEFAULT_SUSTAIN_SECONDS = 60
    const val MIN_IDLE_MINUTES = 1
    const val MAX_IDLE_MINUTES = 240
    const val MIN_PORT = 1
    const val MAX_PORT = 65535
    const val DEFAULT_WAKE_PORT = 9
    const val DEFAULT_BROADCAST_IP = "255.255.255.255"
    const val MIN_WAIT_ONLINE_SECONDS = 30
    const val MAX_WAIT_ONLINE_SECONDS = 300
    const val DEFAULT_WAIT_ONLINE_SECONDS = 300
    const val MIN_DELAY_SECONDS = 1
    const val MAX_DELAY_SECONDS = 600
    const val MIN_POWER_DELAY_SECONDS = 0
    const val MAX_POWER_DELAY_SECONDS = 600
    const val MAX_NOTIFY_TITLE_LENGTH = 40
    const val MAX_NOTIFY_BODY_LENGTH = 120
    const val MAX_WAKE_STEPS = 1
    const val MAX_WAIT_ONLINE_STEPS = 2
    const val MAX_NOTIFY_STEPS = 3
    const val MAX_DESTRUCTIVE_STEPS = 1
    const val MAX_PHONE_RUN_BUDGET_SECONDS = 540
    const val PHONE_BUDGET_PER_HOST_STEP_SECONDS = 60
    const val MAX_HOST_RUN_BUDGET_SECONDS = 1800
    const val HOST_BUDGET_PER_STEP_SECONDS = 30
    const val COUNTDOWN_SECONDS = 15
    const val MAX_SYNC_PAYLOAD_BYTES = 64 * 1024
    const val MAX_NOTIFY_WIRE_BODY_LENGTH = 160
    const val MAX_DETAIL_LENGTH = 120
}

/** Mirrors `RoutineTriggerTypes` (C#). */
object RoutineTriggerTypes {
    const val HOME_ARRIVE = "home.arrive"
    const val HOME_LEAVE = "home.leave"
    const val NFC_TAP = "nfc.tap"
    const val MANUAL = "manual"
    const val PC_SENSOR = "pc.sensor"
    const val PC_IDLE = "pc.idle"
    const val PC_SESSION = "pc.session"

    val ALL = listOf(HOME_ARRIVE, HOME_LEAVE, NFC_TAP, MANUAL, PC_SENSOR, PC_IDLE, PC_SESSION)

    /** `pc.*` triggers run on the PC; every other known trigger runs on the phone (L4). */
    fun isHostRun(type: String?): Boolean = type == PC_SENSOR || type == PC_IDLE || type == PC_SESSION

    fun isKnown(type: String?): Boolean = type in ALL

    /** Automatic sources, which Pause all stops (§8.7). */
    fun isAutomatic(type: String?): Boolean =
        type == HOME_ARRIVE || type == HOME_LEAVE || isHostRun(type)
}

/** Mirrors `RoutineStepTypes` (C#). */
object RoutineStepTypes {
    const val WAKE = "wake"
    const val WAIT_ONLINE = "waitOnline"
    const val DELAY = "delay"
    const val POWER = "power"
    const val LAUNCH_APP = "launchApp"
    const val MEDIA = "media"
    const val NOTIFY = "notify"

    val ALL = listOf(WAKE, WAIT_ONLINE, DELAY, POWER, LAUNCH_APP, MEDIA, NOTIFY)

    fun isKnown(type: String?): Boolean = type in ALL

    /** Only a phone-run routine may contain these (`step_not_allowed_on_pc`). */
    fun isPhoneOnly(type: String?): Boolean = type == WAKE || type == WAIT_ONLINE
}

/**
 * Mirrors `RoutinePowerVerbs` (C#): the 8338 script-ingress verbs minus `WAKEONLAN`, which is
 * reserved in v1 (D5) and rejected with `invalid_field`.
 */
object RoutinePowerVerbs {
    const val SHUTDOWN = "SHUTDOWN"
    const val FORCE_SHUTDOWN = "FORCESHUTDOWN"
    const val RESTART = "RESTART"
    const val FORCE_RESTART = "FORCERESTART"
    const val RESTART_TO_UEFI = "RESTARTTOUEFI"
    const val SIGN_OUT = "SIGNOUT"
    const val SLEEP = "SLEEP"
    const val HIBERNATE = "HIBERNATE"
    const val LOCK = "LOCK"
    const val MONITOR_OFF = "MONITOROFF"

    /** Reserved in v1 (D5). Never offered, never accepted. */
    const val RESERVED_WAKE_ON_LAN = "WAKEONLAN"

    val ALL = listOf(SHUTDOWN, FORCE_SHUTDOWN, RESTART, FORCE_RESTART, RESTART_TO_UEFI, SLEEP, HIBERNATE, SIGN_OUT, LOCK, MONITOR_OFF)

    /**
     * The D1 destructive set: these count down 15 s on the PC whatever started them, a routine may
     * hold at most one, and in a PC-run routine it must be last. LOCK and MONITOROFF are not.
     */
    val DESTRUCTIVE = listOf(SHUTDOWN, FORCE_SHUTDOWN, RESTART, FORCE_RESTART, RESTART_TO_UEFI, SIGN_OUT, SLEEP, HIBERNATE)

    /** The verbs that accept `delaySeconds`. */
    val DELAYABLE = listOf(SHUTDOWN, FORCE_SHUTDOWN, RESTART, FORCE_RESTART, RESTART_TO_UEFI)

    fun isAllowed(verb: String?): Boolean = verb in ALL

    fun isDestructive(verb: String?): Boolean = verb in DESTRUCTIVE

    fun isDelayable(verb: String?): Boolean = verb in DELAYABLE
}

object RoutineMediaActions {
    const val PLAY_PAUSE = "playPause"
    const val NEXT = "next"
    const val PREVIOUS = "previous"
    val ALL = listOf(PLAY_PAUSE, NEXT, PREVIOUS)

    fun isKnown(action: String?): Boolean = action in ALL
}

object RoutineNotifyTargets {
    const val PHONE = "phone"
    const val PC = "pc"

    fun isKnown(target: String?): Boolean = target == PHONE || target == PC
}

object RoutineSensorDirections {
    const val ABOVE = "above"
    const val BELOW = "below"

    fun isKnown(direction: String?): Boolean = direction == ABOVE || direction == BELOW
}

object RoutineSessionStates {
    const val LOCKED = "locked"
    const val UNLOCKED = "unlocked"

    fun isKnown(state: String?): Boolean = state == LOCKED || state == UNLOCKED
}

/** `routine_sync_result.status` (§7.3.2). */
object RoutineSyncStatuses {
    const val OK = "ok"
    const val PARTIAL = "partial"
    const val STALE_REVISION = "stale_revision"
    const val REVISION_CONFLICT = "revision_conflict"
    const val SCHEMA_TOO_NEW = "schema_too_new"
    const val PAYLOAD_TOO_LARGE = "payload_too_large"
    const val BLOCKED_BY_PC = "blocked_by_pc"
    const val RATE_LIMITED = "rate_limited"
    const val INTERNAL_ERROR = "internal_error"
}

/** `routine_step_result.outcome` (§7.3.4). */
object RoutineStepOutcomes {
    const val SUCCEEDED = "succeeded"
    const val FAILED = "failed"
    const val CANCELLED = "cancelled"
    const val SIMULATED = "simulated"
    const val IN_PROGRESS = "in_progress"
}

/** `RoutineRun.outcome` (§8.8). */
object RoutineRunOutcomes {
    const val RUNNING = "running"
    const val SUCCEEDED = "succeeded"
    const val FAILED = "failed"
    const val CANCELLED = "cancelled"
    const val SKIPPED = "skipped"
    const val INTERRUPTED = "interrupted"
}

/** `RoutineRun.steps[].status` (§8.8). */
object RoutineStepStatuses {
    const val PENDING = "pending"
    const val RUNNING = "running"
    const val SUCCEEDED = "succeeded"
    const val FAILED = "failed"
    const val SKIPPED = "skipped"
    const val CANCELLED = "cancelled"
    const val SIMULATED = "simulated"
    const val EXPIRED = "expired"
}

/** `RoutineRun.source` and request `source` (§8.1, §8.8). */
object RoutineRunSources {
    const val HOME_ARRIVE = "home.arrive"
    const val HOME_LEAVE = "home.leave"
    const val NFC_TAP = "nfc.tap"
    const val MANUAL_APP = "manual.app"
    const val MANUAL_SHORTCUT = "manual.shortcut"
    const val MANUAL_WIDGET = "manual.widget"
    const val MANUAL_PC_RUN_NOW = "manual.pcRunNow"
    const val PC_SENSOR = "pc.sensor"
    const val PC_IDLE = "pc.idle"
    const val PC_SESSION = "pc.session"
}

object RoutineRunOrigins {
    const val PHONE = "phone"
    const val PC = "pc"
}

/** `RoutineRun.attributes` (§8.8). Each is also a reason code. */
object RoutineRunAttributes {
    const val SIMULATED = RoutineReasonCodes.SIMULATED
    const val COUNTDOWN_UNSEEN = RoutineReasonCodes.COUNTDOWN_UNSEEN
    const val DEFERRED_BY_OS = RoutineReasonCodes.DEFERRED_BY_OS
    const val DRY_RUN = RoutineReasonCodes.DRY_RUN
    const val NOTIFY_QUEUED = RoutineReasonCodes.NOTIFY_QUEUED
    const val BACKGROUND_RESTRICTED = RoutineReasonCodes.BACKGROUND_RESTRICTED
}

object RoutineCancelledBy {
    const val PC = "pc"
    const val PHONE = "phone"
    const val PAUSE = "pause"
}

object RoutineNotifyKinds {
    const val STEP = "step"
    const val COUNTDOWN = "countdown"
}

object RoutineCancelReasons {
    const val USER = "user"
    const val PAUSE = "pause"
}

/** The routine wire type ids (§7.1). Mirrors the routine constants in C# `MessageTypes`. */
object RoutineMessageTypes {
    /** Phone -> host. The locked id; the only one outside the `routine_` prefix. */
    const val ROUTINES_SYNC = "routines_sync"
    const val ROUTINE_SYNC_RESULT = "routine_sync_result"
    const val ROUTINE_STEP_REQUEST = "routine_step_request"
    const val ROUTINE_STEP_RESULT = "routine_step_result"
    const val ROUTINE_NOTIFY = "routine_notify"
    const val ROUTINE_NOTIFY_ACK = "routine_notify_ack"
    const val ROUTINE_RUN_REPORT = "routine_run_report"
    const val ROUTINE_CANCEL = "routine_cancel"
    const val ROUTINE_RUN_REQUEST = "routine_run_request"

    /** The native router forwards every host -> phone type by this prefix (§7.7). */
    const val HOST_TO_PHONE_PREFIX = "routine_"
}
