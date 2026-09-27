package com.clindsay94.remex.ui.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineText
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.routines.model.RoutineValidator
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

// The editor's rules, kept free of Compose and Context so they can be proven off-device
// (routines spec 1.6-1.8, R-UX-08/10/11/15/18/55; RemEx-pp0rt.6).

/** A step in the editor, with a key that survives reordering so Compose keeps each card's state. */
data class DraftStep(val key: Long, val step: RoutineStep)

/**
 * What the editor holds. Nothing is stored until Save (R-UX-08): opening a template or a blank
 * routine only builds one of these.
 *
 * @property base the stored routine being edited, or null for a new one. Its id, revision,
 *   timestamps and appearance carry through [toRoutine] untouched.
 */
data class RoutineDraft(
    val base: Routine?,
    val name: String,
    val hostIdentity: String?,
    val enabled: Boolean,
    val trigger: RoutineTrigger?,
    val steps: List<DraftStep>,
    val templateId: String? = null,
) {
    val isNew: Boolean get() = base?.id == null

    val runsOnPc: Boolean get() = RoutineTriggerTypes.isHostRun(trigger?.type)

    val canAddStep: Boolean get() = steps.size < RoutineLimits.MAX_STEPS

    fun nextKey(): Long = (steps.maxOfOrNull { it.key } ?: 0L) + 1L

    /** The routine Save would store. A blank name becomes [autoName] (spec 1.8 "Name empty"). */
    fun toRoutine(autoName: String): Routine {
        val typed = RoutineText.sanitize(name).trim()
        val finalName = typed.ifEmpty { autoName.trim() }.take(RoutineLimits.MAX_NAME_LENGTH).trim()
        return (base ?: Routine()).copy(
            name = finalName,
            hostIdentity = hostIdentity,
            enabled = enabled,
            trigger = trigger,
            steps = steps.map { it.step },
            isMalformed = false,
            rawJson = null,
        )
    }

    /** True when saving would change nothing ("Discard changes?" is only asked when this is false). */
    fun sameContentAs(other: RoutineDraft): Boolean =
        name == other.name &&
            hostIdentity == other.hostIdentity &&
            enabled == other.enabled &&
            trigger == other.trigger &&
            steps.map { it.step } == other.steps.map { it.step }

    fun addStep(step: RoutineStep): RoutineDraft =
        if (canAddStep) copy(steps = steps + DraftStep(nextKey(), step)) else this

    fun updateStep(index: Int, step: RoutineStep): RoutineDraft =
        if (index !in steps.indices) this else copy(steps = steps.mapIndexed { i, s -> if (i == index) s.copy(step = step) else s })

    fun removeStep(index: Int): RoutineDraft =
        if (index !in steps.indices) this else copy(steps = steps.filterIndexed { i, _ -> i != index })

    /** Step overflow "Duplicate step": the copy lands right after the original. */
    fun duplicateStep(index: Int): RoutineDraft {
        if (index !in steps.indices || !canAddStep) return this
        val list = steps.toMutableList()
        list.add(index + 1, DraftStep(nextKey(), steps[index].step))
        return copy(steps = list)
    }

    /** Moves the step at [from] to [to] (drag, overflow Move up/down, TalkBack actions). */
    fun moveStep(from: Int, to: Int): RoutineDraft {
        if (from !in steps.indices || to !in steps.indices || from == to) return this
        return copy(steps = steps.moved(from, to))
    }

    /** Fills every `wake` step with [mac] (the target PC's saved MAC, upper-case colon form). */
    fun withWakeMac(mac: String?): RoutineDraft =
        copy(steps = steps.map { if (it.step.type == RoutineStepTypes.WAKE) it.copy(step = it.step.copy(mac = mac)) else it })
}

fun <T> List<T>.moved(from: Int, to: Int): List<T> {
    val list = toMutableList()
    val item = list.removeAt(from)
    list.add(to, item)
    return list
}

object RoutineDrafts {
    fun blank(hostIdentity: String?): RoutineDraft =
        RoutineDraft(base = null, name = "", hostIdentity = hostIdentity, enabled = true, trigger = null, steps = emptyList())

    fun fromRoutine(routine: Routine): RoutineDraft =
        RoutineDraft(
            base = routine,
            name = routine.name.orEmpty(),
            hostIdentity = routine.hostIdentity,
            enabled = routine.enabled,
            trigger = routine.trigger,
            steps = routine.steps.orEmpty().mapIndexed { i, step -> DraftStep(i + 1L, step ?: RoutineStep()) },
        )

    /**
     * A template, pre-filled (spec 1.3 step 3). [name] and [notifyBody] are the localised strings,
     * resolved now because the routine's text is user data from here on (R-UX-57).
     */
    fun fromTemplate(
        template: RoutineTemplate,
        name: String,
        hostIdentity: String?,
        mac: String?,
        notifyBody: (Int) -> String,
    ): RoutineDraft =
        RoutineDraft(
            base = null,
            name = name.take(RoutineLimits.MAX_NAME_LENGTH),
            hostIdentity = hostIdentity,
            enabled = true,
            trigger = template.trigger,
            steps =
                template.steps.mapIndexed { i, templateStep ->
                    val step = templateStep.step
                    val filled =
                        when (step.type) {
                            RoutineStepTypes.WAKE -> step.copy(mac = mac)
                            RoutineStepTypes.NOTIFY ->
                                step.copy(
                                    title = name.take(RoutineLimits.MAX_NOTIFY_TITLE_LENGTH),
                                    body = templateStep.notifyBodyRes?.let(notifyBody)?.take(RoutineLimits.MAX_NOTIFY_BODY_LENGTH),
                                )
                            else -> step
                        }
                    DraftStep(i + 1L, filled)
                },
            templateId = template.id,
        )
}

/** How serious a problem is (spec 1.8): only errors block Save. */
enum class ProblemKind { ERROR, WARNING, INFO }

/** Where the editor shows a problem, so Save can scroll to the first one and outline each card. */
sealed interface ProblemTarget {
    data object Pc : ProblemTarget

    data object Trigger : ProblemTarget

    data object Steps : ProblemTarget

    data class Step(val index: Int) : ProblemTarget
}

/** Each rule of spec 1.8 the S1 editor can meet. Messages live in `RoutineEditorProblemText`. */
enum class EditorProblemCode {
    NO_PC,
    NO_TRIGGER,
    NO_STEPS,
    WAKE_NO_MAC,
    CHOOSE_APP,
    APP_MISSING,
    NOTIFY_NO_TITLE,
    PHONE_ONLY,
    TOO_MANY_OF_KIND,
    DESTRUCTIVE_NOT_LAST,
    TOO_MANY_DESTRUCTIVE,
    AFTER_POWER_OFF,
    COUNTDOWN,
    BUDGET_EXCEEDED,
    MEDIA_KEYS,

    /** `pc.sensor` with no sensor chosen yet (a health template before the PC's catalog arrived). */
    CHOOSE_SENSOR,

    /** The connected target PC's catalog does not have the chosen sensor (the PC would refuse it). */
    SENSOR_MISSING,

    /** The limit is empty or not a number. */
    SENSOR_LIMIT,

    /** The hold time is outside 5-600 s (§6.3). */
    SENSOR_SUSTAIN,
}

data class EditorProblem(
    val target: ProblemTarget,
    val code: EditorProblemCode,
    val kind: ProblemKind,
    /** The app label for [EditorProblemCode.APP_MISSING]. */
    val app: String? = null,
)

/**
 * What the editor knows about the target PC.
 *
 * @property launcherAppIds the ids in the PC's launcher list, or null while that list is unknown
 *   (not connected yet). An unknown list never produces [EditorProblemCode.APP_MISSING].
 * @property mediaKeysSupported whether the target PC accepts key presses, or null while unknown
 *   (not the connected PC). Only a definite false produces [EditorProblemCode.MEDIA_KEYS].
 * @property sensors the target PC's sensor catalog, or null while unknown (not the connected PC, or
 *   no telemetry yet). An unknown catalog never produces [EditorProblemCode.SENSOR_MISSING].
 */
data class EditorEnvironment(
    val launcherAppIds: Set<String>? = null,
    val mediaKeysSupported: Boolean? = null,
    val sensors: List<RoutineSensorOption>? = null,
)

/** Whether a PC's `host_info` says it accepts key presses (spec 4.2 "Media keys"). */
object RoutineMediaKeys {
    /**
     * Null when unknown: no `host_info` yet, one from an older connection, or one that cannot be
     * parsed. An absent `supportsInputSimulation` means an older PC that does accept them, the same
     * default Remote Control uses.
     */
    fun supported(hostInfoJson: String?, sameConnection: Boolean): Boolean? {
        if (hostInfoJson == null || !sameConnection) return null
        return try {
            org.json.JSONObject(hostInfoJson).optBoolean("supportsInputSimulation", true)
        } catch (e: org.json.JSONException) {
            null
        }
    }
}

/** "Discard changes?" before another draft replaces the open one (R-UX-58). */
object RoutineEditorSwitch {
    /** True when the open draft has unsaved edits and the target is a different routine or template. */
    fun needsDiscardPrompt(draft: RoutineDraft?, original: RoutineDraft?, sameSource: Boolean): Boolean =
        !sameSource && draft != null && original != null && !draft.sameContentAs(original)
}

/** Whether a step kind can be added to this routine right now (R-UX-10). */
enum class StepAvailability {
    AVAILABLE,

    /** `wake` / `waitOnline` on a routine that runs on the PC. */
    PHONE_ONLY,

    /** The per-kind cap (1 wake, 2 waitOnline, 3 notify) is reached. */
    LIMIT_REACHED,
}

object RoutineEditorRules {
    private val kindCaps =
        mapOf(
            RoutineStepTypes.WAKE to RoutineLimits.MAX_WAKE_STEPS,
            RoutineStepTypes.WAIT_ONLINE to RoutineLimits.MAX_WAIT_ONLINE_STEPS,
            RoutineStepTypes.NOTIFY to RoutineLimits.MAX_NOTIFY_STEPS,
        )

    fun availability(type: String, triggerType: String?, stepTypes: List<String?>): StepAvailability {
        if (RoutineTriggerTypes.isHostRun(triggerType) && RoutineStepTypes.isPhoneOnly(type)) return StepAvailability.PHONE_ONLY
        val cap = kindCaps[type] ?: return StepAvailability.AVAILABLE
        return if (stepTypes.count { it == type } >= cap) StepAvailability.LIMIT_REACHED else StepAvailability.AVAILABLE
    }

    /**
     * Indices of steps that switching to [newTriggerType] would make unavailable (R-UX-11): the
     * editor asks "Remove it?" before changing, and keeps the old trigger on Cancel.
     */
    fun stepsInvalidatedBy(newTriggerType: String?, steps: List<RoutineStep?>): List<Int> =
        if (!RoutineTriggerTypes.isHostRun(newTriggerType)) {
            emptyList()
        } else {
            steps.indices.filter { RoutineStepTypes.isPhoneOnly(steps[it]?.type) }
        }

    /** Every problem the editor shows (spec 1.8), in screen order: PC chip, WHEN, THEN, each step. */
    fun problems(draft: RoutineDraft, env: EditorEnvironment): List<EditorProblem> {
        val out = mutableListOf<EditorProblem>()
        val hostRun = draft.runsOnPc
        val steps = draft.steps.map { it.step }

        if (draft.hostIdentity.isNullOrEmpty()) out += EditorProblem(ProblemTarget.Pc, EditorProblemCode.NO_PC, ProblemKind.ERROR)
        if (draft.trigger?.type == null) out += EditorProblem(ProblemTarget.Trigger, EditorProblemCode.NO_TRIGGER, ProblemKind.ERROR)
        draft.trigger?.takeIf { it.type == RoutineTriggerTypes.PC_SENSOR }?.let { out += sensorProblems(it, env) }
        if (steps.isEmpty()) out += EditorProblem(ProblemTarget.Steps, EditorProblemCode.NO_STEPS, ProblemKind.ERROR)

        val seen = HashMap<String, Int>()
        var destructiveSeen = 0
        var poweredOff = false
        for ((i, step) in steps.withIndex()) {
            val target = ProblemTarget.Step(i)
            val type = step.type.orEmpty()
            val count = (seen[type] ?: 0) + 1
            seen[type] = count

            when {
                hostRun && RoutineStepTypes.isPhoneOnly(type) ->
                    out += EditorProblem(target, EditorProblemCode.PHONE_ONLY, ProblemKind.ERROR)
                (kindCaps[type] ?: Int.MAX_VALUE) < count ->
                    out += EditorProblem(target, EditorProblemCode.TOO_MANY_OF_KIND, ProblemKind.ERROR)
            }

            when (type) {
                RoutineStepTypes.WAKE ->
                    if (step.mac.isNullOrEmpty()) out += EditorProblem(target, EditorProblemCode.WAKE_NO_MAC, ProblemKind.ERROR)
                RoutineStepTypes.LAUNCH_APP ->
                    when {
                        step.appId.isNullOrEmpty() -> out += EditorProblem(target, EditorProblemCode.CHOOSE_APP, ProblemKind.ERROR)
                        env.launcherAppIds != null && env.launcherAppIds.none { it.equals(step.appId, ignoreCase = true) } ->
                            out += EditorProblem(target, EditorProblemCode.APP_MISSING, ProblemKind.ERROR, app = step.appLabel)
                    }
                RoutineStepTypes.NOTIFY ->
                    if (RoutineText.sanitize(step.title).isBlank()) {
                        out += EditorProblem(target, EditorProblemCode.NOTIFY_NO_TITLE, ProblemKind.ERROR)
                    }
                // Spec 1.8 / 4.2: a warning, never a block; the PC may be set up before the routine runs.
                RoutineStepTypes.MEDIA ->
                    if (env.mediaKeysSupported == false) out += EditorProblem(target, EditorProblemCode.MEDIA_KEYS, ProblemKind.WARNING)
            }

            // A phone routine may act on the PC again after powering it off, but only once it is
            // back; without a waitOnline in between that step is skipped at run time (after_power_off).
            if (!hostRun && poweredOff && step.isHostExecuted) {
                out += EditorProblem(target, EditorProblemCode.AFTER_POWER_OFF, ProblemKind.WARNING)
            }
            if (type == RoutineStepTypes.WAIT_ONLINE) poweredOff = false

            if (step.isDestructive) {
                destructiveSeen++
                if (destructiveSeen > RoutineLimits.MAX_DESTRUCTIVE_STEPS) {
                    out += EditorProblem(target, EditorProblemCode.TOO_MANY_DESTRUCTIVE, ProblemKind.ERROR)
                } else {
                    out += EditorProblem(target, EditorProblemCode.COUNTDOWN, ProblemKind.INFO)
                    // "This can't run: the step BEFORE it turns off the PC" belongs on the step that
                    // cannot run, the one after the power step, not on the power step itself.
                    if (hostRun && i != steps.lastIndex) {
                        out += EditorProblem(ProblemTarget.Step(i + 1), EditorProblemCode.DESTRUCTIVE_NOT_LAST, ProblemKind.ERROR)
                    }
                }
                poweredOff = true
            }
        }

        if (steps.isNotEmpty()) {
            val max = if (hostRun) RoutineLimits.MAX_HOST_RUN_BUDGET_SECONDS else RoutineLimits.MAX_PHONE_RUN_BUDGET_SECONDS
            val probe = Routine(trigger = draft.trigger, steps = steps)
            if (RoutineValidator.budgetSeconds(probe) > max) {
                out += EditorProblem(ProblemTarget.Steps, EditorProblemCode.BUDGET_EXCEEDED, ProblemKind.ERROR)
            }
        }
        return out
    }

    /** `pc.sensor` (§6.3, §6.5): a sensor the PC has, a numeric limit, and a hold time of 5-600 s. */
    private fun sensorProblems(trigger: RoutineTrigger, env: EditorEnvironment): List<EditorProblem> {
        val out = mutableListOf<EditorProblem>()
        val id = trigger.sensorId
        when {
            id.isNullOrEmpty() -> out += EditorProblem(ProblemTarget.Trigger, EditorProblemCode.CHOOSE_SENSOR, ProblemKind.ERROR)
            env.sensors != null && env.sensors.none { it.id == id } ->
                out += EditorProblem(ProblemTarget.Trigger, EditorProblemCode.SENSOR_MISSING, ProblemKind.ERROR)
        }
        val limit = trigger.threshold
        if (limit == null || !limit.isFinite()) out += EditorProblem(ProblemTarget.Trigger, EditorProblemCode.SENSOR_LIMIT, ProblemKind.ERROR)
        val sustain = trigger.sustainSeconds ?: RoutineLimits.DEFAULT_SUSTAIN_SECONDS
        if (sustain !in RoutineLimits.MIN_SUSTAIN_SECONDS..RoutineLimits.MAX_SUSTAIN_SECONDS) {
            out += EditorProblem(ProblemTarget.Trigger, EditorProblemCode.SENSOR_SUSTAIN, ProblemKind.ERROR)
        }
        return out
    }

    fun errors(problems: List<EditorProblem>): List<EditorProblem> = problems.filter { it.kind == ProblemKind.ERROR }

    /** "Name empty: auto-name from the trigger and first step" (spec 1.8). */
    fun autoName(triggerChip: String?, firstStepChip: String?, pair: (String, String) -> String, fallback: String): String {
        val name =
            when {
                triggerChip != null && firstStepChip != null -> pair(triggerChip, firstStepChip)
                triggerChip != null -> triggerChip
                firstStepChip != null -> firstStepChip
                else -> fallback
            }
        return name.take(RoutineLimits.MAX_NAME_LENGTH).trim()
    }
}

/** "Duplicate" (spec 1.6, R-UX-15): "Name (copy)", switched off, same steps, a new identity. */
object RoutineCopies {
    fun copyOf(routine: Routine, copyName: (String) -> String): Routine {
        val base = routine.name.orEmpty()
        val suffixLength = copyName("").length
        val room = (RoutineLimits.MAX_NAME_LENGTH - suffixLength).coerceAtLeast(1)
        val name = copyName(base.take(room).trimEnd()).take(RoutineLimits.MAX_NAME_LENGTH)
        return routine.copy(
            id = null,
            name = name,
            enabled = false,
            revision = 0,
            createdAtUnixMs = 0,
            updatedAtUnixMs = 0,
            isMalformed = false,
            rawJson = null,
        )
    }
}

/** The list's "Runs on" groups and order within them (spec 1.6 "Reorder routines", R-UX-18). */
object RoutineOrder {
    /** "phone" for phone-run routines, the PC's identity for PC-run ones. */
    fun groupOf(routine: Routine): String =
        if (RoutineTriggerTypes.isHostRun(routine.trigger?.type)) "pc:" + routine.hostIdentity.orEmpty() else PHONE

    const val PHONE = "phone"

    /**
     * The full id order after moving [id] by [delta] places within its own group, or null when it
     * cannot move that way. Other groups keep their places.
     */
    fun move(routines: List<Routine>, id: String, delta: Int): List<String>? {
        val index = routines.indexOfFirst { it.id == id }
        if (index < 0 || delta == 0) return null
        val group = groupOf(routines[index])
        val groupIndices = routines.indices.filter { groupOf(routines[it]) == group }
        val position = groupIndices.indexOf(index)
        val targetPosition = position + delta
        if (targetPosition !in groupIndices.indices) return null
        // Walk the group's own slots so routines of other groups never shift.
        val ids = routines.map { it.id.orEmpty() }.toMutableList()
        val groupIds = groupIndices.map { ids[it] }.moved(position, targetPosition)
        groupIndices.forEachIndexed { i, slot -> ids[slot] = groupIds[i] }
        return ids
    }
}

/** Progress and grouping for runs (spec 1.7, A8, R-UX-27). */
object RoutineRunViews {
    private val done =
        setOf(
            RoutineStepStatuses.SUCCEEDED,
            RoutineStepStatuses.SIMULATED,
            RoutineStepStatuses.SKIPPED,
            RoutineStepStatuses.FAILED,
            RoutineStepStatuses.CANCELLED,
            RoutineStepStatuses.EXPIRED,
        )

    /** The step running now, or the next one to run, or null when every step has finished. */
    fun currentStep(run: RoutineRun): Int? {
        val steps = run.steps.orEmpty()
        return steps.firstOrNull { it.status == RoutineStepStatuses.RUNNING }?.index
            ?: steps.firstOrNull { it.status == RoutineStepStatuses.PENDING || it.status == null }?.index
    }

    fun finishedSteps(run: RoutineRun): Int = run.steps.orEmpty().count { it.status in done }

    fun totalSteps(run: RoutineRun): Int = run.steps.orEmpty().size

    fun succeededSteps(run: RoutineRun): Int =
        run.steps.orEmpty().count { it.status == RoutineStepStatuses.SUCCEEDED || it.status == RoutineStepStatuses.SIMULATED }

    fun progress(run: RoutineRun): Float {
        val total = totalSteps(run)
        return if (total == 0) 0f else finishedSteps(run).toFloat() / total
    }

    /** Newest first, grouped by the day each run started, in [zone]. */
    fun groupByDay(runs: List<RoutineRun>, zone: ZoneId): List<Pair<LocalDate, List<RoutineRun>>> =
        runs
            .sortedByDescending { it.triggeredAtUnixMs }
            .groupBy { Instant.ofEpochMilli(it.triggeredAtUnixMs).atZone(zone).toLocalDate() }
            .toList()

    /** "Edited since this run" (R-UX-28): the routine's revision moved on. */
    fun editedSince(run: RoutineRun, routine: Routine?): Boolean =
        routine != null && run.routineRevision > 0 && routine.revision != run.routineRevision
}
