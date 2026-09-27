package com.clindsay94.remex.routines.model

import java.text.BreakIterator
import java.util.Locale

/** [RoutineReasonCodes.OK] or the FIRST rule that failed, with the offending field path. */
data class RoutineVerdict(val reasonCode: String, val detail: String? = null) {
    val isValid: Boolean get() = reasonCode == RoutineReasonCodes.OK

    companion object {
        val VALID = RoutineVerdict(RoutineReasonCodes.OK)
    }
}

/** Set-level rules, then one verdict per routine in order. */
data class RoutineSetVerdict(val reasonCode: String, val detail: String?, val routines: List<RoutineVerdict>) {
    /** The set-level code when it failed, else the first failing routine's, else `ok`. */
    val firstFailure: String
        get() =
            if (reasonCode != RoutineReasonCodes.OK) reasonCode
            else routines.firstOrNull { !it.isValid }?.reasonCode ?: RoutineReasonCodes.OK
}

/** Facts the validator cannot know from the routine alone. Null [knownHomeIds] = not checked. */
data class RoutineValidationContext(val knownHomeIds: Collection<String>? = null)

/**
 * The routine schema rules of spec §6.2-§6.5 (RemEx-pp0rt.3). A LINE-FOR-LINE mirror of
 * `remex.core/Routines/RoutineValidator.cs`: first failure wins, in the same fixed order (routine
 * shape, trigger, step count, each step, step caps, destructive rules, budget), with the same field
 * paths. The shared fixtures (`routines/manifest.json`) assert both sides reach the same code, so a
 * rule changed or reordered on one side only is a failing test, not a phone and a PC that disagree
 * about why a routine is invalid.
 *
 * Host-state checks (`wrong_pc`, `power_unsupported`, `launch_not_allowed`, ...) are the PC's and are
 * not here.
 */
object RoutineValidator {

    private val TRIGGER_FIELD_ORDER =
        listOf("homeId", "leaveDebounceSeconds", "sensorId", "sensorLabel", "direction", "threshold",
            "sustainSeconds", "idleMinutes", "ignoreWhileMediaPlaying", "sessionState")

    private val STEP_FIELD_ORDER =
        listOf("mac", "broadcastIp", "port", "timeoutSeconds", "seconds", "verb", "delaySeconds",
            "appId", "appLabel", "mediaAction", "target", "title", "body")

    fun validateSet(set: RoutineSet?, context: RoutineValidationContext? = null): RoutineSetVerdict {
        if (set == null) return RoutineSetVerdict(RoutineReasonCodes.INVALID_FIELD, "set", emptyList())
        if (set.schemaVersion > RoutineSchema.CURRENT_VERSION) {
            return RoutineSetVerdict(RoutineReasonCodes.SCHEMA_TOO_NEW, "schemaVersion", emptyList())
        }
        if (set.schemaVersion < 1) {
            return RoutineSetVerdict(RoutineReasonCodes.INVALID_FIELD, "schemaVersion", emptyList())
        }
        return validateRoutines(set.routines, context)
    }

    fun validateRoutines(routines: List<Routine>?, context: RoutineValidationContext? = null): RoutineSetVerdict {
        val list = routines ?: emptyList()
        val verdicts = ArrayList<RoutineVerdict>(list.size)
        val seenIds = HashSet<String>()
        for (routine in list) {
            var verdict = validateRoutine(routine, context)
            val id = routine.id
            if (verdict.isValid && id != null && id in seenIds) {
                verdict = RoutineVerdict(RoutineReasonCodes.DUPLICATE_ID, "id")
            }
            if (id != null) seenIds.add(id)
            verdicts.add(verdict)
        }

        if (list.size > RoutineLimits.MAX_ROUTINES_PER_PHONE) {
            return RoutineSetVerdict(RoutineReasonCodes.TOO_MANY_ROUTINES, "routines", verdicts)
        }

        val pcRunPerHost = HashMap<String, Int>()
        for (routine in list) {
            val host = routine.hostIdentity ?: continue
            if (RoutineTriggerTypes.isHostRun(routine.trigger?.type)) {
                val count = (pcRunPerHost[host] ?: 0) + 1
                pcRunPerHost[host] = count
                if (count > RoutineLimits.MAX_PC_ROUTINES_PER_HOST) {
                    return RoutineSetVerdict(RoutineReasonCodes.TOO_MANY_ROUTINES, "routines", verdicts)
                }
            }
        }

        return RoutineSetVerdict(RoutineReasonCodes.OK, null, verdicts)
    }

    fun validateRoutine(routine: Routine?, context: RoutineValidationContext? = null): RoutineVerdict {
        if (routine == null || routine.isMalformed) return invalid("routine")
        if (!isLowerUuidV4(routine.id)) return invalid("id")
        if (!isValidName(routine.name)) return invalid("name")
        if (!isHostIdentity(routine.hostIdentity)) return invalid("hostIdentity")
        if (routine.revision < 1) return invalid("revision")
        if (routine.createdAtUnixMs < 0) return invalid("createdAtUnixMs")
        if (routine.updatedAtUnixMs < routine.createdAtUnixMs) return invalid("updatedAtUnixMs")

        routine.appearance?.let { appearance ->
            if (appearance.icon != null && !isIconToken(appearance.icon)) return invalid("appearance.icon")
            if (appearance.color != null && !isHexColor(appearance.color)) return invalid("appearance.color")
        }

        val triggerVerdict = validateTrigger(routine.trigger, context)
        if (!triggerVerdict.isValid) return triggerVerdict

        val trigger = requireNotNull(routine.trigger) { "validateTrigger accepted a routine with no trigger" }
        val hostRun = RoutineTriggerTypes.isHostRun(trigger.type)
        val steps = routine.steps
        if (steps.isNullOrEmpty()) return invalid("steps")
        if (steps.size > RoutineLimits.MAX_STEPS) return RoutineVerdict(RoutineReasonCodes.TOO_MANY_STEPS, "steps")

        for ((i, step) in steps.withIndex()) {
            val stepVerdict = validateStep(step, hostRun, "steps[$i]")
            if (!stepVerdict.isValid) return stepVerdict
        }

        if (steps.count { it?.type == RoutineStepTypes.WAKE } > RoutineLimits.MAX_WAKE_STEPS ||
            steps.count { it?.type == RoutineStepTypes.WAIT_ONLINE } > RoutineLimits.MAX_WAIT_ONLINE_STEPS ||
            steps.count { it?.type == RoutineStepTypes.NOTIFY } > RoutineLimits.MAX_NOTIFY_STEPS
        ) {
            return invalid("steps")
        }

        var destructiveCount = 0
        var lastDestructiveIndex = -1
        for ((i, step) in steps.withIndex()) {
            if (step?.isDestructive == true) {
                destructiveCount++
                lastDestructiveIndex = i
            }
        }
        if (destructiveCount > RoutineLimits.MAX_DESTRUCTIVE_STEPS) {
            return RoutineVerdict(RoutineReasonCodes.TOO_MANY_DESTRUCTIVE, "steps")
        }
        // PC-run only: nothing can run after the host process is gone or suspended.
        if (hostRun && destructiveCount == 1 && lastDestructiveIndex != steps.size - 1) {
            return RoutineVerdict(RoutineReasonCodes.DESTRUCTIVE_NOT_LAST, "steps[$lastDestructiveIndex]")
        }

        val max = if (hostRun) RoutineLimits.MAX_HOST_RUN_BUDGET_SECONDS else RoutineLimits.MAX_PHONE_RUN_BUDGET_SECONDS
        if (budgetSeconds(routine) > max) return RoutineVerdict(RoutineReasonCodes.BUDGET_EXCEEDED, "steps")

        return RoutineVerdict.VALID
    }

    /** One step on its own; [hostRun] is whether it belongs to a PC-run routine. */
    fun validateStep(step: RoutineStep?, hostRun: Boolean): RoutineVerdict = validateStep(step, hostRun, "step")

    /** The static time budget (§6.5), identical to C# `RoutineValidator.BudgetSeconds`. */
    fun budgetSeconds(routine: Routine): Int {
        val hostRun = RoutineTriggerTypes.isHostRun(routine.trigger?.type)
        var total = 0L
        var anyDestructive = false
        for (step in routine.steps ?: emptyList()) {
            if (step == null) continue
            if (step.type == RoutineStepTypes.DELAY) total += maxOf(0, step.seconds ?: 0)
            if (hostRun) {
                total += RoutineLimits.HOST_BUDGET_PER_STEP_SECONDS
            } else if (step.type == RoutineStepTypes.WAIT_ONLINE) {
                total += maxOf(0, step.timeoutSeconds ?: RoutineLimits.DEFAULT_WAIT_ONLINE_SECONDS)
            } else if (step.isHostExecuted) {
                total += RoutineLimits.PHONE_BUDGET_PER_HOST_STEP_SECONDS
            }
            anyDestructive = anyDestructive || step.isDestructive
        }
        if (anyDestructive) total += RoutineLimits.COUNTDOWN_SECONDS
        return minOf(total, Int.MAX_VALUE.toLong()).toInt()
    }

    private fun validateTrigger(trigger: RoutineTrigger?, context: RoutineValidationContext?): RoutineVerdict {
        if (trigger == null || trigger.isMalformed || trigger.type == null) return invalid("trigger")
        val type = trigger.type
        if (!RoutineTriggerTypes.isKnown(type)) return RoutineVerdict(RoutineReasonCodes.UNSUPPORTED_TRIGGER, "trigger.type")

        val present = triggerFieldsPresent(trigger)
        for ((i, field) in TRIGGER_FIELD_ORDER.withIndex()) {
            if (present[i] && !isTriggerFieldAllowed(type, field)) {
                return RoutineVerdict(RoutineReasonCodes.FIELD_NOT_ALLOWED, "trigger.$field")
            }
        }

        when (type) {
            RoutineTriggerTypes.HOME_ARRIVE, RoutineTriggerTypes.HOME_LEAVE -> {
                val homeId = trigger.homeId
                if (homeId.isNullOrEmpty()) return RoutineVerdict(RoutineReasonCodes.HOME_NOT_SET, "trigger.homeId")
                if (!isLowerUuidV4(homeId)) return invalid("trigger.homeId")
                val homes = context?.knownHomeIds
                if (homes != null && homeId !in homes) {
                    return RoutineVerdict(RoutineReasonCodes.HOME_NOT_SET, "trigger.homeId")
                }
                val debounce = trigger.leaveDebounceSeconds
                if (debounce != null &&
                    (debounce < RoutineLimits.MIN_LEAVE_DEBOUNCE_SECONDS || debounce > RoutineLimits.MAX_LEAVE_DEBOUNCE_SECONDS)
                ) {
                    return invalid("trigger.leaveDebounceSeconds")
                }
            }
            RoutineTriggerTypes.PC_SENSOR -> {
                val sensorId = trigger.sensorId
                if (sensorId.isNullOrEmpty() || sensorId.length > RoutineLimits.MAX_SENSOR_ID_LENGTH) return invalid("trigger.sensorId")
                if (trigger.sensorLabel != null && textLength(trigger.sensorLabel) > RoutineLimits.MAX_LABEL_LENGTH) {
                    return invalid("trigger.sensorLabel")
                }
                if (!RoutineSensorDirections.isKnown(trigger.direction)) return invalid("trigger.direction")
                val threshold = trigger.threshold
                if (threshold == null || !threshold.isFinite()) return invalid("trigger.threshold")
                val sustain = trigger.sustainSeconds
                if (sustain != null && (sustain < RoutineLimits.MIN_SUSTAIN_SECONDS || sustain > RoutineLimits.MAX_SUSTAIN_SECONDS)) {
                    return invalid("trigger.sustainSeconds")
                }
            }
            RoutineTriggerTypes.PC_IDLE -> {
                val idle = trigger.idleMinutes
                if (idle == null || idle < RoutineLimits.MIN_IDLE_MINUTES || idle > RoutineLimits.MAX_IDLE_MINUTES) {
                    return invalid("trigger.idleMinutes")
                }
            }
            RoutineTriggerTypes.PC_SESSION -> {
                if (!RoutineSessionStates.isKnown(trigger.sessionState)) return invalid("trigger.sessionState")
            }
        }

        return RoutineVerdict.VALID
    }

    private fun validateStep(step: RoutineStep?, hostRun: Boolean, path: String): RoutineVerdict {
        if (step == null || step.isMalformed || step.type == null) return invalid(path)
        val type = step.type
        if (!RoutineStepTypes.isKnown(type)) return RoutineVerdict(RoutineReasonCodes.UNSUPPORTED_STEP, "$path.type")
        if (hostRun && RoutineStepTypes.isPhoneOnly(type)) {
            return RoutineVerdict(RoutineReasonCodes.STEP_NOT_ALLOWED_ON_PC, "$path.type")
        }

        val present = stepFieldsPresent(step)
        for ((i, field) in STEP_FIELD_ORDER.withIndex()) {
            if (present[i] && !isStepFieldAllowed(type, field)) {
                return RoutineVerdict(RoutineReasonCodes.FIELD_NOT_ALLOWED, "$path.$field")
            }
        }

        when (type) {
            RoutineStepTypes.WAKE -> {
                val mac = step.mac ?: return RoutineVerdict(RoutineReasonCodes.WAKE_NO_MAC, "$path.mac")
                if (!isUpperMac(mac)) return invalid("$path.mac")
                if (step.broadcastIp != null && !isIpv4(step.broadcastIp)) return invalid("$path.broadcastIp")
                val port = step.port
                if (port != null && (port < RoutineLimits.MIN_PORT || port > RoutineLimits.MAX_PORT)) return invalid("$path.port")
            }
            RoutineStepTypes.WAIT_ONLINE -> {
                val timeout = step.timeoutSeconds
                if (timeout != null &&
                    (timeout < RoutineLimits.MIN_WAIT_ONLINE_SECONDS || timeout > RoutineLimits.MAX_WAIT_ONLINE_SECONDS)
                ) {
                    return invalid("$path.timeoutSeconds")
                }
            }
            RoutineStepTypes.DELAY -> {
                val seconds = step.seconds
                if (seconds == null || seconds < RoutineLimits.MIN_DELAY_SECONDS || seconds > RoutineLimits.MAX_DELAY_SECONDS) {
                    return invalid("$path.seconds")
                }
            }
            RoutineStepTypes.POWER -> {
                // WAKEONLAN is reserved in v1 (D5): not in the allowed set, so invalid_field.
                if (!RoutinePowerVerbs.isAllowed(step.verb)) return invalid("$path.verb")
                val delay = step.delaySeconds
                if (delay != null) {
                    if (!RoutinePowerVerbs.isDelayable(step.verb)) {
                        return RoutineVerdict(RoutineReasonCodes.FIELD_NOT_ALLOWED, "$path.delaySeconds")
                    }
                    if (delay < RoutineLimits.MIN_POWER_DELAY_SECONDS || delay > RoutineLimits.MAX_POWER_DELAY_SECONDS) {
                        return invalid("$path.delaySeconds")
                    }
                }
            }
            RoutineStepTypes.LAUNCH_APP -> {
                if (!isGuidShape(step.appId, lowerOnly = false)) return invalid("$path.appId")
                if (step.appLabel != null && textLength(step.appLabel) > RoutineLimits.MAX_LABEL_LENGTH) {
                    return invalid("$path.appLabel")
                }
            }
            RoutineStepTypes.MEDIA -> {
                if (!RoutineMediaActions.isKnown(step.mediaAction)) return invalid("$path.mediaAction")
            }
            RoutineStepTypes.NOTIFY -> {
                if (!RoutineNotifyTargets.isKnown(step.target)) return invalid("$path.target")
                val titleLength = textLength(RoutineText.sanitize(step.title))
                if (titleLength < 1 || titleLength > RoutineLimits.MAX_NOTIFY_TITLE_LENGTH) return invalid("$path.title")
                if (textLength(RoutineText.sanitize(step.body)) > RoutineLimits.MAX_NOTIFY_BODY_LENGTH) return invalid("$path.body")
            }
        }

        return RoutineVerdict.VALID
    }

    private fun triggerFieldsPresent(t: RoutineTrigger): BooleanArray =
        booleanArrayOf(
            t.homeId != null, t.leaveDebounceSeconds != null, t.sensorId != null, t.sensorLabel != null,
            t.direction != null, t.threshold != null, t.sustainSeconds != null, t.idleMinutes != null,
            t.ignoreWhileMediaPlaying != null, t.sessionState != null,
        )

    private fun stepFieldsPresent(s: RoutineStep): BooleanArray =
        booleanArrayOf(
            s.mac != null, s.broadcastIp != null, s.port != null, s.timeoutSeconds != null, s.seconds != null,
            s.verb != null, s.delaySeconds != null, s.appId != null, s.appLabel != null, s.mediaAction != null,
            s.target != null, s.title != null, s.body != null,
        )

    private fun isTriggerFieldAllowed(type: String, field: String): Boolean =
        when (type) {
            RoutineTriggerTypes.HOME_ARRIVE -> field == "homeId"
            RoutineTriggerTypes.HOME_LEAVE -> field == "homeId" || field == "leaveDebounceSeconds"
            RoutineTriggerTypes.PC_SENSOR ->
                field == "sensorId" || field == "sensorLabel" || field == "direction" || field == "threshold" ||
                    field == "sustainSeconds"
            RoutineTriggerTypes.PC_IDLE -> field == "idleMinutes" || field == "ignoreWhileMediaPlaying"
            RoutineTriggerTypes.PC_SESSION -> field == "sessionState"
            else -> false // nfc.tap and manual take no fields
        }

    private fun isStepFieldAllowed(type: String, field: String): Boolean =
        when (type) {
            RoutineStepTypes.WAKE -> field == "mac" || field == "broadcastIp" || field == "port"
            RoutineStepTypes.WAIT_ONLINE -> field == "timeoutSeconds"
            RoutineStepTypes.DELAY -> field == "seconds"
            RoutineStepTypes.POWER -> field == "verb" || field == "delaySeconds"
            RoutineStepTypes.LAUNCH_APP -> field == "appId" || field == "appLabel"
            RoutineStepTypes.MEDIA -> field == "mediaAction"
            RoutineStepTypes.NOTIFY -> field == "target" || field == "title" || field == "body"
            else -> false
        }

    private fun invalid(detail: String) = RoutineVerdict(RoutineReasonCodes.INVALID_FIELD, detail)

    private fun isValidName(name: String?): Boolean {
        if (name == null || RoutineText.hasControlOrLineBreak(name)) return false
        val length = textLength(name.trim())
        return length in 1..RoutineLimits.MAX_NAME_LENGTH
    }

    /** User-perceived characters (grapheme clusters), as C# `StringInfo.LengthInTextElements`. */
    internal fun textLength(text: String): Int {
        if (text.isEmpty()) return 0
        val iterator = BreakIterator.getCharacterInstance(Locale.ROOT)
        iterator.setText(text)
        var count = 0
        while (iterator.next() != BreakIterator.DONE) count++
        return count
    }

    private fun isHexLower(c: Char) = c in '0'..'9' || c in 'a'..'f'

    private fun isHexAny(c: Char) = isHexLower(c) || c in 'A'..'F'

    private fun isHostIdentity(value: String?): Boolean = value != null && value.length == 16 && value.all(::isHexLower)

    private fun isLowerUuidV4(value: String?): Boolean {
        if (!isGuidShape(value, lowerOnly = true)) return false
        val uuid = requireNotNull(value) { "isGuidShape accepted a null value" }
        return uuid[14] == '4' && uuid[19] in "89ab"
    }

    private fun isGuidShape(value: String?, lowerOnly: Boolean): Boolean {
        if (value == null || value.length != 36) return false
        for ((i, c) in value.withIndex()) {
            if (i == 8 || i == 13 || i == 18 || i == 23) {
                if (c != '-') return false
            } else if (if (lowerOnly) !isHexLower(c) else !isHexAny(c)) {
                return false
            }
        }
        return true
    }

    private fun isUpperMac(value: String): Boolean {
        if (value.length != 17) return false
        for ((i, c) in value.withIndex()) {
            if (i % 3 == 2) {
                if (c != ':') return false
            } else if (!(c in '0'..'9' || c in 'A'..'F')) {
                return false
            }
        }
        return true
    }

    private fun isIpv4(value: String): Boolean {
        val parts = value.split('.')
        if (parts.size != 4) return false
        for (part in parts) {
            if (part.length !in 1..3 || (part.length > 1 && part[0] == '0')) return false
            if (!part.all { it in '0'..'9' }) return false
            if (part.toInt() > 255) return false
        }
        return true
    }

    private fun isIconToken(value: String): Boolean =
        value.length in 1..RoutineLimits.MAX_ICON_LENGTH && value.all { it in 'a'..'z' || it in '0'..'9' || it == '_' }

    private fun isHexColor(value: String): Boolean =
        value.length == 7 && value[0] == '#' && value.substring(1).all(::isHexAny)
}

/** Plain-text rules for routine user text. Mirrors C# `RoutineText`. */
object RoutineText {
    /** C0, DEL and C1 (U+0000-U+001F, U+007F-U+009F), and U+2028 / U+2029. */
    fun isControlOrLineBreak(c: Char): Boolean {
        val code = c.code
        return code < 0x20 || code in 0x7F..0x9F || code == 0x2028 || code == 0x2029
    }

    fun hasControlOrLineBreak(text: String): Boolean = text.any(::isControlOrLineBreak)

    /** `notify` text as presented: control characters and line breaks removed, then trimmed. */
    fun sanitize(text: String?): String {
        if (text.isNullOrEmpty()) return ""
        return text.filterNot(::isControlOrLineBreak).trim()
    }
}
