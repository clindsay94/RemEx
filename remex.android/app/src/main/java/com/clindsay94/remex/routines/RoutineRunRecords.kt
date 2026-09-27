package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineMediaActions
import com.clindsay94.remex.routines.model.RoutineNotifyTargets
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunSourceDetail
import com.clindsay94.remex.routines.model.RoutineRunStep
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepTypes

/** Building phone [RoutineRun] records (routines spec §8.1, §8.8). Pure JVM. */
object RoutineRunRecords {

    /**
     * The record a run starts life as: `running`, every step `pending`. It is persisted BEFORE the
     * work is enqueued (§8.1), which is what later makes an interruption visible.
     */
    fun queued(
        runId: String,
        routine: Routine,
        source: String,
        testRun: Boolean,
        triggeredAtUnixMs: Long,
        sourceDetail: RoutineRunSourceDetail? = null,
    ): RoutineRun =
        RoutineRun(
            runId = runId,
            routineId = routine.id,
            routineName = routine.name,
            routineRevision = routine.revision,
            origin = RoutineRunOrigins.PHONE,
            hostIdentity = routine.hostIdentity,
            source = source,
            testRun = testRun,
            sourceDetail = sourceDetail,
            triggeredAtUnixMs = triggeredAtUnixMs,
            startedAtUnixMs = 0,
            outcome = RoutineRunOutcomes.RUNNING,
            attributes = emptyList(),
            steps = pendingSteps(routine.steps.orEmpty()),
        )

    /** A start that never happened (§8.1 "Skipped"): every step `skipped`. */
    fun skipped(queued: RoutineRun, reasonCode: String, nowUnixMs: Long, args: RoutineReasonArgs? = null): RoutineRun =
        queued.copy(
            outcome = RoutineRunOutcomes.SKIPPED,
            reasonCode = reasonCode,
            reasonArgs = args,
            endedAtUnixMs = nowUnixMs,
            steps = queued.steps.orEmpty().map { it.copy(status = RoutineStepStatuses.SKIPPED) },
        )

    fun pendingSteps(steps: List<RoutineStep?>): List<RoutineRunStep> =
        steps.mapIndexed { index, step -> RoutineRunStep(index = index, kind = step?.type, status = RoutineStepStatuses.PENDING) }

    /**
     * A host-executed step after a power-off step that ACTUALLY SUCCEEDED, with no `waitOnline`
     * between them (§6.4): the PC is gone, so it is skipped with `after_power_off` rather than sent
     * into the void. Keyed on what happened, not on the definition: a power-off step that was
     * simulated (a test run), skipped for a countdown conflict or cancelled left the PC on, and the
     * steps after it must still run. [statuses] is the run's step statuses, by index.
     */
    fun isAfterPowerOff(steps: List<RoutineStep?>, statuses: List<String?>, index: Int): Boolean {
        val step = steps.getOrNull(index) ?: return false
        if (!step.isHostExecuted) return false
        val powerOff =
            (0 until index).lastOrNull {
                steps[it]?.isDestructive == true && statuses.getOrNull(it) == RoutineStepStatuses.SUCCEEDED
            } ?: return false
        return (powerOff + 1 until index).none { steps[it]?.type == RoutineStepTypes.WAIT_ONLINE }
    }
}

/**
 * The `{action}` placeholder of a reason message (§10.1), stored in `reasonArgs.action` as a token,
 * never as translated text, so history reads correctly after a language change. `RoutineReasonText`
 * turns the token into the phrase for the current locale.
 */
object RoutineActionTokens {
    const val LAUNCH_APP = "launchApp"
    const val NOTIFY_PC = "notify.pc"
    const val MEDIA_PREFIX = "media."

    /** A power verb (`SHUTDOWN`), [LAUNCH_APP], `media.<action>`, [NOTIFY_PC], or null. */
    fun of(step: RoutineStep?): String? =
        when (step?.type) {
            RoutineStepTypes.POWER -> step.verb
            RoutineStepTypes.LAUNCH_APP -> LAUNCH_APP
            RoutineStepTypes.MEDIA -> step.mediaAction?.takeIf(RoutineMediaActions::isKnown)?.let { MEDIA_PREFIX + it }
            RoutineStepTypes.NOTIFY -> if (step.target == RoutineNotifyTargets.PC) NOTIFY_PC else null
            else -> null
        }
}
