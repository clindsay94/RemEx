package com.clindsay94.remex.routines

import android.content.Context
import android.icu.text.MeasureFormat
import android.icu.util.Measure
import android.icu.util.MeasureUnit
import androidx.annotation.StringRes
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.model.RoutineMediaActions
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes as C

/**
 * Every routine reason code, mapped to its Android message and history label (routines spec §6.11,
 * §10.1; RemEx-pp0rt.5). The ONE mapping: result notifications, the run sheet, history and the
 * editor (S1d) all read it, so a code can never show one text in a notification and another in
 * history.
 *
 * Strings are `routine_reason_<code>` (the message) and `routine_history_<code>` (the short history
 * label), in all nine locale `strings.xml` files. They carry named placeholders - `{pc}`, `{app}`,
 * `{action}`, `{duration}`, `{detail}` and the rest of [RoutineMessageTemplate.TOKENS] - filled from
 * the run's [RoutineReasonArgs], never printf, because a reason message is rendered long after the
 * run by whatever screen shows it, from arguments stored with the record.
 *
 * `RoutineReasonCodeLocalizationTest` fails if a code in `RoutineReasonCodes.ALL` has no entry here
 * or no string in any locale, or if a translation drops or invents a placeholder.
 */
object RoutineReasonText {
    private class Keys(@StringRes val message: Int, @StringRes val history: Int)

    private val TABLE: Map<String, Keys> =
        mapOf(
            C.OK to Keys(R.string.routine_reason_ok, R.string.routine_history_ok),
            C.PC_UNREACHABLE to Keys(R.string.routine_reason_pc_unreachable, R.string.routine_history_pc_unreachable),
            C.PC_UNREACHABLE_AWAY to Keys(R.string.routine_reason_pc_unreachable_away, R.string.routine_history_pc_unreachable_away),
            C.WAIT_TIMEOUT to Keys(R.string.routine_reason_wait_timeout, R.string.routine_history_wait_timeout),
            C.WAKE_NO_MAC to Keys(R.string.routine_reason_wake_no_mac, R.string.routine_history_wake_no_mac),
            C.WAKE_SEND_FAILED to Keys(R.string.routine_reason_wake_send_failed, R.string.routine_history_wake_send_failed),
            C.PERMISSION_LOCAL_NETWORK to Keys(R.string.routine_reason_permission_local_network, R.string.routine_history_permission_local_network),
            C.PC_NOT_SELECTED to Keys(R.string.routine_reason_pc_not_selected, R.string.routine_history_pc_not_selected),
            C.PC_NOT_PAIRED to Keys(R.string.routine_reason_pc_not_paired, R.string.routine_history_pc_not_paired),
            C.PC_TOO_OLD to Keys(R.string.routine_reason_pc_too_old, R.string.routine_history_pc_too_old),
            C.STEP_TIMEOUT to Keys(R.string.routine_reason_step_timeout, R.string.routine_history_step_timeout),
            C.TRANSPORT_LOST to Keys(R.string.routine_reason_transport_lost, R.string.routine_history_transport_lost),
            C.AFTER_POWER_OFF to Keys(R.string.routine_reason_after_power_off, R.string.routine_history_after_power_off),
            C.LAUNCH_NOT_ALLOWED to Keys(R.string.routine_reason_launch_not_allowed, R.string.routine_history_launch_not_allowed),
            C.LAUNCH_FAILED to Keys(R.string.routine_reason_launch_failed, R.string.routine_history_launch_failed),
            C.POWER_UNSUPPORTED to Keys(R.string.routine_reason_power_unsupported, R.string.routine_history_power_unsupported),
            C.POWER_DENIED_BY_OS to Keys(R.string.routine_reason_power_denied_by_os, R.string.routine_history_power_denied_by_os),
            C.POWER_FAILED to Keys(R.string.routine_reason_power_failed, R.string.routine_history_power_failed),
            C.MEDIA_UNAVAILABLE to Keys(R.string.routine_reason_media_unavailable, R.string.routine_history_media_unavailable),
            C.SENSOR_UNAVAILABLE to Keys(R.string.routine_reason_sensor_unavailable, R.string.routine_history_sensor_unavailable),
            C.IDLE_SOURCE_UNAVAILABLE to Keys(R.string.routine_reason_idle_source_unavailable, R.string.routine_history_idle_source_unavailable),
            C.SESSION_SOURCE_UNAVAILABLE to
                Keys(R.string.routine_reason_session_source_unavailable, R.string.routine_history_session_source_unavailable),
            C.ROUTINE_NOT_FOUND to Keys(R.string.routine_reason_routine_not_found, R.string.routine_history_routine_not_found),
            C.REJECTED_BY_PC to Keys(R.string.routine_reason_rejected_by_pc, R.string.routine_history_rejected_by_pc),
            C.SCHEMA_TOO_NEW to Keys(R.string.routine_reason_schema_too_new, R.string.routine_history_schema_too_new),
            C.PAYLOAD_TOO_LARGE to Keys(R.string.routine_reason_payload_too_large, R.string.routine_history_payload_too_large),
            C.STALE_REVISION to Keys(R.string.routine_reason_stale_revision, R.string.routine_history_stale_revision),
            C.REVISION_CONFLICT to Keys(R.string.routine_reason_revision_conflict, R.string.routine_history_revision_conflict),
            C.BLOCKED_BY_PC to Keys(R.string.routine_reason_blocked_by_pc, R.string.routine_history_blocked_by_pc),
            C.DESTRUCTIVE_NOT_LAST to Keys(R.string.routine_reason_destructive_not_last, R.string.routine_history_destructive_not_last),
            C.TOO_MANY_DESTRUCTIVE to Keys(R.string.routine_reason_too_many_destructive, R.string.routine_history_too_many_destructive),
            C.TOO_MANY_STEPS to Keys(R.string.routine_reason_too_many_steps, R.string.routine_history_too_many_steps),
            C.TOO_MANY_ROUTINES to Keys(R.string.routine_reason_too_many_routines, R.string.routine_history_too_many_routines),
            C.TOO_MANY_HOMES to Keys(R.string.routine_reason_too_many_homes, R.string.routine_history_too_many_homes),
            C.BUDGET_EXCEEDED to Keys(R.string.routine_reason_budget_exceeded, R.string.routine_history_budget_exceeded),
            C.HOME_NOT_SET to Keys(R.string.routine_reason_home_not_set, R.string.routine_history_home_not_set),
            C.STEP_NOT_ALLOWED_ON_PC to Keys(R.string.routine_reason_step_not_allowed_on_pc, R.string.routine_history_step_not_allowed_on_pc),
            C.TRIGGER_NOT_PC to Keys(R.string.routine_reason_trigger_not_pc, R.string.routine_history_trigger_not_pc),
            C.WRONG_PC to Keys(R.string.routine_reason_wrong_pc, R.string.routine_history_wrong_pc),
            C.UNSUPPORTED_TRIGGER to Keys(R.string.routine_reason_unsupported_trigger, R.string.routine_history_unsupported_trigger),
            C.UNSUPPORTED_STEP to Keys(R.string.routine_reason_unsupported_step, R.string.routine_history_unsupported_step),
            C.DUPLICATE_ID to Keys(R.string.routine_reason_duplicate_id, R.string.routine_history_duplicate_id),
            C.FIELD_NOT_ALLOWED to Keys(R.string.routine_reason_field_not_allowed, R.string.routine_history_field_not_allowed),
            C.INVALID_FIELD to Keys(R.string.routine_reason_invalid_field, R.string.routine_history_invalid_field),
            C.PAUSED_ON_PHONE to Keys(R.string.routine_reason_paused_on_phone, R.string.routine_history_paused_on_phone),
            C.PAUSED_ON_PC to Keys(R.string.routine_reason_paused_on_pc, R.string.routine_history_paused_on_pc),
            C.DISABLED_ON_PC to Keys(R.string.routine_reason_disabled_on_pc, R.string.routine_history_disabled_on_pc),
            C.SKIPPED_DISABLED to Keys(R.string.routine_reason_skipped_disabled, R.string.routine_history_skipped_disabled),
            C.OWNER_ABSENT to Keys(R.string.routine_reason_owner_absent, R.string.routine_history_owner_absent),
            C.ALREADY_RUNNING to Keys(R.string.routine_reason_already_running, R.string.routine_history_already_running),
            C.COOLDOWN to Keys(R.string.routine_reason_cooldown, R.string.routine_history_cooldown),
            C.FLAP_SUPPRESSED to Keys(R.string.routine_reason_flap_suppressed, R.string.routine_history_flap_suppressed),
            C.RATE_LIMITED to Keys(R.string.routine_reason_rate_limited, R.string.routine_history_rate_limited),
            C.CONFLICT_COUNTDOWN_ACTIVE to
                Keys(R.string.routine_reason_conflict_countdown_active, R.string.routine_history_conflict_countdown_active),
            C.CANCELLED_ON_PC to Keys(R.string.routine_reason_cancelled_on_pc, R.string.routine_history_cancelled_on_pc),
            C.CANCELLED_ON_PHONE to Keys(R.string.routine_reason_cancelled_on_phone, R.string.routine_history_cancelled_on_phone),
            C.INTERRUPTED_PC to Keys(R.string.routine_reason_interrupted_pc, R.string.routine_history_interrupted_pc),
            C.INTERRUPTED_PHONE to Keys(R.string.routine_reason_interrupted_phone, R.string.routine_history_interrupted_phone),
            C.NOTIFY_QUEUED to Keys(R.string.routine_reason_notify_queued, R.string.routine_history_notify_queued),
            C.NOTIFY_EXPIRED to Keys(R.string.routine_reason_notify_expired, R.string.routine_history_notify_expired),
            C.NOTIFY_DENIED_PHONE to Keys(R.string.routine_reason_notify_denied_phone, R.string.routine_history_notify_denied_phone),
            C.BACKGROUND_RESTRICTED to Keys(R.string.routine_reason_background_restricted, R.string.routine_history_background_restricted),
            C.DEFERRED_BY_OS to Keys(R.string.routine_reason_deferred_by_os, R.string.routine_history_deferred_by_os),
            C.SIMULATED to Keys(R.string.routine_reason_simulated, R.string.routine_history_simulated),
            C.DRY_RUN to Keys(R.string.routine_reason_dry_run, R.string.routine_history_dry_run),
            C.COUNTDOWN_UNSEEN to Keys(R.string.routine_reason_countdown_unseen, R.string.routine_history_countdown_unseen),
            C.NFC_UNKNOWN_TAG to Keys(R.string.routine_reason_nfc_unknown_tag, R.string.routine_history_nfc_unknown_tag),
            C.NFC_DEVICE_LOCKED to Keys(R.string.routine_reason_nfc_device_locked, R.string.routine_history_nfc_device_locked),
            C.NFC_DISABLED to Keys(R.string.routine_reason_nfc_disabled, R.string.routine_history_nfc_disabled),
            C.FINGERPRINT_CAPTURE_FAILED to
                Keys(R.string.routine_reason_fingerprint_capture_failed, R.string.routine_history_fingerprint_capture_failed),
            C.HOME_FINGERPRINT_STALE to Keys(R.string.routine_reason_home_fingerprint_stale, R.string.routine_history_home_fingerprint_stale),
            C.STORE_RESET to Keys(R.string.routine_reason_store_reset, R.string.routine_history_store_reset),
            C.INTERNAL_ERROR to Keys(R.string.routine_reason_internal_error, R.string.routine_history_internal_error),
        )

    /** The `{action}` phrases, keyed by [RoutineActionTokens] value. */
    private val ACTIONS: Map<String, Int> =
        mapOf(
            RoutinePowerVerbs.SHUTDOWN to R.string.routine_action_shutdown,
            RoutinePowerVerbs.FORCE_SHUTDOWN to R.string.routine_action_force_shutdown,
            RoutinePowerVerbs.RESTART to R.string.routine_action_restart,
            RoutinePowerVerbs.FORCE_RESTART to R.string.routine_action_force_restart,
            RoutinePowerVerbs.RESTART_TO_UEFI to R.string.routine_action_restart_uefi,
            RoutinePowerVerbs.SIGN_OUT to R.string.routine_action_sign_out,
            RoutinePowerVerbs.SLEEP to R.string.routine_action_sleep,
            RoutinePowerVerbs.HIBERNATE to R.string.routine_action_hibernate,
            RoutinePowerVerbs.LOCK to R.string.routine_action_lock,
            RoutinePowerVerbs.MONITOR_OFF to R.string.routine_action_monitor_off,
            RoutineActionTokens.LAUNCH_APP to R.string.routine_action_launch_app,
            RoutineActionTokens.MEDIA_PREFIX + RoutineMediaActions.PLAY_PAUSE to R.string.routine_action_media_play_pause,
            RoutineActionTokens.MEDIA_PREFIX + RoutineMediaActions.NEXT to R.string.routine_action_media_next,
            RoutineActionTokens.MEDIA_PREFIX + RoutineMediaActions.PREVIOUS to R.string.routine_action_media_previous,
            RoutineActionTokens.NOTIFY_PC to R.string.routine_action_notify_pc,
        )

    /** The codes with a mapping; the localization test asserts this equals `RoutineReasonCodes.ALL`. */
    val coveredCodes: Set<String> get() = TABLE.keys

    /** The action tokens with a phrase. */
    val coveredActions: Set<String> get() = ACTIONS.keys

    /** An unknown code (a newer PC's) reads as `internal_error` rather than as nothing. */
    @StringRes
    fun messageRes(code: String?): Int = (TABLE[code] ?: TABLE.getValue(C.INTERNAL_ERROR)).message

    @StringRes
    fun historyRes(code: String?): Int = (TABLE[code] ?: TABLE.getValue(C.INTERNAL_ERROR)).history

    /** The full message for [code], filled from [args]. */
    fun message(context: Context, code: String?, args: RoutineReasonArgs?): String =
        render(context, context.getString(messageRes(code)), args)

    /** The short history label for [code], filled from [args]. */
    fun history(context: Context, code: String?, args: RoutineReasonArgs?): String =
        render(context, context.getString(historyRes(code)), args)

    /** The `{action}` phrase for a token, with `{app}` already filled, or null for no action. */
    fun actionPhrase(context: Context, token: String?, app: String?): String? {
        val res = ACTIONS[token ?: return null] ?: return null
        return RoutineMessageTemplate.fill(context.getString(res), mapOf("app" to app.orEmpty()))
    }

    /**
     * Fills [template]. `{pc}` falls back to "your PC" when the run has no nickname for it (never a
     * hostname, T10); `{detail}` holding a reason code becomes that code's own message, which is how
     * `rejected_by_pc` names the specific refusal; `{duration}` holds whole seconds.
     */
    fun render(context: Context, template: String, args: RoutineReasonArgs?): String {
        val pc = args?.pc?.takeIf { it.isNotBlank() }
        val detail =
            args?.detail?.let { d ->
                if (d in TABLE) message(context, d, args.copy(detail = null)) else d
            }
        val values =
            mapOf(
                "pc" to (pc ?: context.getString(R.string.routine_pc_fallback_name)),
                "phone" to args?.phone.orEmpty(),
                "app" to args?.app.orEmpty(),
                "sensor" to args?.sensor.orEmpty(),
                "duration" to (args?.duration?.let { formatDuration(context, it) }).orEmpty(),
                "action" to actionPhrase(context, args?.action, args?.app).orEmpty(),
                "detail" to detail.orEmpty(),
                "routine" to args?.routine.orEmpty(),
                "date" to args?.date.orEmpty(),
                "n" to args?.n.orEmpty(),
            )
        val filled = RoutineMessageTemplate.fill(template, values)
        // "your PC didn't come online" starts a sentence; a user's own nickname is left as typed.
        return if (pc == null && template.startsWith("{pc}")) {
            filled.replaceFirstChar { it.titlecase(context.resources.configuration.locales[0]) }
        } else {
            filled
        }
    }

    /** Whole seconds as "45 seconds" / "2 minutes" / "2 minutes, 30 seconds" in the app's locale. */
    fun formatDuration(context: Context, secondsText: String): String {
        val seconds = secondsText.toLongOrNull()?.takeIf { it >= 0 } ?: return secondsText
        val format = MeasureFormat.getInstance(context.resources.configuration.locales[0], MeasureFormat.FormatWidth.WIDE)
        val minutes = seconds / 60
        val rest = seconds % 60
        return when {
            minutes == 0L -> format.format(Measure(rest, MeasureUnit.SECOND))
            rest == 0L -> format.format(Measure(minutes, MeasureUnit.MINUTE))
            else -> format.formatMeasures(Measure(minutes, MeasureUnit.MINUTE), Measure(rest, MeasureUnit.SECOND))
        }
    }
}

/** Named-placeholder templates for routine messages. Pure JVM. */
object RoutineMessageTemplate {
    /** Every placeholder a routine message may use; the keys of [RoutineReasonArgs]. */
    val TOKENS: List<String> = listOf("pc", "phone", "app", "sensor", "duration", "action", "detail", "routine", "date", "n")

    private val PATTERN = Regex("\\{([a-z]+)\\}")

    /** The placeholders [template] uses. */
    fun tokensIn(template: String): Set<String> = PATTERN.findAll(template).map { it.groupValues[1] }.toSet()

    /** One pass: a value that itself contains `{pc}` is NOT expanded again. Unknown tokens stay. */
    fun fill(template: String, values: Map<String, String>): String =
        PATTERN.replace(template) { match -> values[match.groupValues[1]] ?: match.value }
}
