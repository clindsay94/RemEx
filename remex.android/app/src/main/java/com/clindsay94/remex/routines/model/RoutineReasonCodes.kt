package com.clindsay94.remex.routines.model

/**
 * Every routine reason code (routines spec §6.11, catalogue §10.1, RemEx-pp0rt.3). Mirrors
 * `remex.core/Routines/RoutineReasonCodes.cs`.
 *
 * [ALL] is the parity list: both sides assert it equals `routines/reason-codes.json`, in order, so a
 * code added on one side only fails the other side's test. Each code maps to
 * `routine_reason_<code>` in every locale's `strings.xml` (added by the UI slices).
 */
object RoutineReasonCodes {
    const val OK = "ok"

    // Reaching the PC
    const val PC_UNREACHABLE = "pc_unreachable"
    const val PC_UNREACHABLE_AWAY = "pc_unreachable_away"
    const val WAIT_TIMEOUT = "wait_timeout"
    const val WAKE_NO_MAC = "wake_no_mac"
    const val WAKE_SEND_FAILED = "wake_send_failed"
    const val PERMISSION_LOCAL_NETWORK = "permission_local_network"
    const val PC_NOT_SELECTED = "pc_not_selected"
    const val PC_NOT_PAIRED = "pc_not_paired"
    const val PC_TOO_OLD = "pc_too_old"
    const val STEP_TIMEOUT = "step_timeout"
    const val TRANSPORT_LOST = "transport_lost"
    const val AFTER_POWER_OFF = "after_power_off"

    // On the PC
    const val LAUNCH_NOT_ALLOWED = "launch_not_allowed"
    const val LAUNCH_FAILED = "launch_failed"
    const val POWER_UNSUPPORTED = "power_unsupported"
    const val POWER_DENIED_BY_OS = "power_denied_by_os"
    const val POWER_FAILED = "power_failed"
    const val MEDIA_UNAVAILABLE = "media_unavailable"
    const val SENSOR_UNAVAILABLE = "sensor_unavailable"
    const val IDLE_SOURCE_UNAVAILABLE = "idle_source_unavailable"
    const val SESSION_SOURCE_UNAVAILABLE = "session_source_unavailable"
    const val ROUTINE_NOT_FOUND = "routine_not_found"

    // Sync and validation
    const val REJECTED_BY_PC = "rejected_by_pc"
    const val SCHEMA_TOO_NEW = "schema_too_new"
    const val PAYLOAD_TOO_LARGE = "payload_too_large"
    const val STALE_REVISION = "stale_revision"
    const val REVISION_CONFLICT = "revision_conflict"
    const val BLOCKED_BY_PC = "blocked_by_pc"
    const val DESTRUCTIVE_NOT_LAST = "destructive_not_last"
    const val TOO_MANY_DESTRUCTIVE = "too_many_destructive"
    const val TOO_MANY_STEPS = "too_many_steps"
    const val TOO_MANY_ROUTINES = "too_many_routines"
    const val TOO_MANY_HOMES = "too_many_homes"
    const val BUDGET_EXCEEDED = "budget_exceeded"
    const val HOME_NOT_SET = "home_not_set"
    const val STEP_NOT_ALLOWED_ON_PC = "step_not_allowed_on_pc"
    const val TRIGGER_NOT_PC = "trigger_not_pc"
    const val WRONG_PC = "wrong_pc"
    const val UNSUPPORTED_TRIGGER = "unsupported_trigger"
    const val UNSUPPORTED_STEP = "unsupported_step"
    const val DUPLICATE_ID = "duplicate_id"
    const val FIELD_NOT_ALLOWED = "field_not_allowed"
    const val INVALID_FIELD = "invalid_field"

    // Skipped and cancelled
    const val PAUSED_ON_PHONE = "paused_on_phone"
    const val PAUSED_ON_PC = "paused_on_pc"
    const val DISABLED_ON_PC = "disabled_on_pc"
    const val SKIPPED_DISABLED = "skipped_disabled"
    const val OWNER_ABSENT = "owner_absent"
    const val ALREADY_RUNNING = "already_running"
    const val COOLDOWN = "cooldown"
    const val FLAP_SUPPRESSED = "flap_suppressed"
    const val RATE_LIMITED = "rate_limited"
    const val CONFLICT_COUNTDOWN_ACTIVE = "conflict_countdown_active"
    const val CANCELLED_ON_PC = "cancelled_on_pc"
    const val CANCELLED_ON_PHONE = "cancelled_on_phone"
    const val INTERRUPTED_PC = "interrupted_pc"
    const val INTERRUPTED_PHONE = "interrupted_phone"

    // Messages, attributes and states
    const val NOTIFY_QUEUED = "notify_queued"
    const val NOTIFY_EXPIRED = "notify_expired"
    const val NOTIFY_DENIED_PHONE = "notify_denied_phone"
    const val BACKGROUND_RESTRICTED = "background_restricted"
    const val DEFERRED_BY_OS = "deferred_by_os"
    const val SIMULATED = "simulated"
    const val DRY_RUN = "dry_run"
    const val COUNTDOWN_UNSEEN = "countdown_unseen"
    const val NFC_UNKNOWN_TAG = "nfc_unknown_tag"
    const val NFC_DEVICE_LOCKED = "nfc_device_locked"
    const val NFC_DISABLED = "nfc_disabled"
    const val FINGERPRINT_CAPTURE_FAILED = "fingerprint_capture_failed"
    const val HOME_FINGERPRINT_STALE = "home_fingerprint_stale"
    const val STORE_RESET = "store_reset"
    const val INTERNAL_ERROR = "internal_error"

    /** Every code, in §10.1 table order. The C# parity list. */
    val ALL: List<String> =
        listOf(
            OK,
            PC_UNREACHABLE,
            PC_UNREACHABLE_AWAY,
            WAIT_TIMEOUT,
            WAKE_NO_MAC,
            WAKE_SEND_FAILED,
            PERMISSION_LOCAL_NETWORK,
            PC_NOT_SELECTED,
            PC_NOT_PAIRED,
            PC_TOO_OLD,
            STEP_TIMEOUT,
            TRANSPORT_LOST,
            AFTER_POWER_OFF,
            LAUNCH_NOT_ALLOWED,
            LAUNCH_FAILED,
            POWER_UNSUPPORTED,
            POWER_DENIED_BY_OS,
            POWER_FAILED,
            MEDIA_UNAVAILABLE,
            SENSOR_UNAVAILABLE,
            IDLE_SOURCE_UNAVAILABLE,
            SESSION_SOURCE_UNAVAILABLE,
            ROUTINE_NOT_FOUND,
            REJECTED_BY_PC,
            SCHEMA_TOO_NEW,
            PAYLOAD_TOO_LARGE,
            STALE_REVISION,
            REVISION_CONFLICT,
            BLOCKED_BY_PC,
            DESTRUCTIVE_NOT_LAST,
            TOO_MANY_DESTRUCTIVE,
            TOO_MANY_STEPS,
            TOO_MANY_ROUTINES,
            TOO_MANY_HOMES,
            BUDGET_EXCEEDED,
            HOME_NOT_SET,
            STEP_NOT_ALLOWED_ON_PC,
            TRIGGER_NOT_PC,
            WRONG_PC,
            UNSUPPORTED_TRIGGER,
            UNSUPPORTED_STEP,
            DUPLICATE_ID,
            FIELD_NOT_ALLOWED,
            INVALID_FIELD,
            PAUSED_ON_PHONE,
            PAUSED_ON_PC,
            DISABLED_ON_PC,
            SKIPPED_DISABLED,
            OWNER_ABSENT,
            ALREADY_RUNNING,
            COOLDOWN,
            FLAP_SUPPRESSED,
            RATE_LIMITED,
            CONFLICT_COUNTDOWN_ACTIVE,
            CANCELLED_ON_PC,
            CANCELLED_ON_PHONE,
            INTERRUPTED_PC,
            INTERRUPTED_PHONE,
            NOTIFY_QUEUED,
            NOTIFY_EXPIRED,
            NOTIFY_DENIED_PHONE,
            BACKGROUND_RESTRICTED,
            DEFERRED_BY_OS,
            SIMULATED,
            DRY_RUN,
            COUNTDOWN_UNSEEN,
            NFC_UNKNOWN_TAG,
            NFC_DEVICE_LOCKED,
            NFC_DISABLED,
            FINGERPRINT_CAPTURE_FAILED,
            HOME_FINGERPRINT_STALE,
            STORE_RESET,
            INTERNAL_ERROR,
        )
}
