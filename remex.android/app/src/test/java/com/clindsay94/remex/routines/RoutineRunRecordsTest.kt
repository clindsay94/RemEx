package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunSourceDetail
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepStatuses.FAILED
import com.clindsay94.remex.routines.model.RoutineStepStatuses.SUCCEEDED
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RoutineRunRecordsTest {

    private fun launchApp() = RoutineStep(type = "launchApp", appId = "steam", appLabel = "Steam")

    @Test
    fun queued_isRunningPhoneRecordWithOnePendingStepPerRoutineStep() {
        val routine = manualRoutine(wake(), waitOnline(), launchApp()).copy(revision = 7)
        val detail = RoutineRunSourceDetail(homeLabel = "Home")

        val run = RoutineRunRecords.queued("run-1", routine, "manual", testRun = true, triggeredAtUnixMs = 1_000, sourceDetail = detail)

        assertEquals("run-1", run.runId)
        assertEquals(routine.id, run.routineId)
        assertEquals("Game time", run.routineName)
        assertEquals(7L, run.routineRevision)
        assertEquals(HOST, run.hostIdentity)
        assertEquals(RoutineRunOrigins.PHONE, run.origin)
        assertEquals(RoutineRunOutcomes.RUNNING, run.outcome)
        assertEquals("manual", run.source)
        assertTrue(run.testRun)
        assertEquals(detail, run.sourceDetail)
        assertEquals(1_000L, run.triggeredAtUnixMs)
        assertEquals(0L, run.startedAtUnixMs)
        assertNull(run.endedAtUnixMs)
        val steps = run.steps!!
        assertEquals(listOf(0, 1, 2), steps.map { it.index })
        assertEquals(listOf("wake", "waitOnline", "launchApp"), steps.map { it.kind })
        assertTrue(steps.all { it.status == RoutineStepStatuses.PENDING })
    }

    @Test
    fun queued_nullStepGetsNullKind_andMissingStepsGiveNoRows() {
        val withNull = manualRoutine().copy(steps = listOf(lock(), null))
        val steps = RoutineRunRecords.queued("r", withNull, "manual", false, 1).steps!!
        assertEquals(listOf(0, 1), steps.map { it.index })
        assertEquals(listOf("power", null), steps.map { it.kind })
        assertEquals(RoutineStepStatuses.PENDING, steps[1].status)

        val noSteps = manualRoutine().copy(steps = null)
        assertEquals(emptyList<Any>(), RoutineRunRecords.queued("r", noSteps, "manual", false, 1).steps)
    }

    @Test
    fun skipped_marksEveryStepSkippedAndKeepsTheRunIdentity() {
        val routine = manualRoutine(wake(), shutdown())
        val queued = RoutineRunRecords.queued("run-9", routine, "manual", testRun = false, triggeredAtUnixMs = 500)
        val args = RoutineReasonArgs(pc = "Desk")

        val skipped = RoutineRunRecords.skipped(queued, "host_offline", nowUnixMs = 900, args = args)

        assertEquals(RoutineRunOutcomes.SKIPPED, skipped.outcome)
        assertEquals("host_offline", skipped.reasonCode)
        assertEquals(args, skipped.reasonArgs)
        assertEquals(900L, skipped.endedAtUnixMs)
        assertEquals("run-9", skipped.runId)
        assertEquals(routine.id, skipped.routineId)
        assertEquals(routine.name, skipped.routineName)
        assertEquals(500L, skipped.triggeredAtUnixMs)
        assertEquals(RoutineRunOrigins.PHONE, skipped.origin)
        val steps = skipped.steps!!
        assertEquals(2, steps.size)
        assertTrue(steps.all { it.status == RoutineStepStatuses.SKIPPED })
        assertEquals(listOf("wake", "power"), steps.map { it.kind })
    }

    @Test
    fun isAfterPowerOff_trueForHostStepAfterSucceededPowerOff() {
        val steps = listOf(shutdown(), delaySeconds(5), launchApp())
        assertTrue(RoutineRunRecords.isAfterPowerOff(steps, listOf(SUCCEEDED, SUCCEEDED, null), 2))
    }

    @Test
    fun isAfterPowerOff_falseWhenPowerOffDidNotSucceed() {
        val steps = listOf(shutdown(), launchApp())
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, listOf(FAILED, null), 1))
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, listOf(RoutineStepStatuses.SIMULATED, null), 1))
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, emptyList(), 1))
    }

    @Test
    fun isAfterPowerOff_falseWithoutPrecedingDestructiveStep() {
        val steps = listOf(lock(), launchApp())
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, listOf(SUCCEEDED, null), 1))
    }

    @Test
    fun isAfterPowerOff_falseWhenWaitOnlineSitsBetween() {
        val steps = listOf(shutdown(), wake(), waitOnline(), launchApp())
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, listOf(SUCCEEDED, SUCCEEDED, SUCCEEDED, null), 3))
    }

    @Test
    fun isAfterPowerOff_falseForPhoneStepsAndOutOfRangeIndex() {
        val steps = listOf(shutdown(), notifyPhone(), wake())
        val statuses = listOf(SUCCEEDED, null, null)
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, statuses, 1))
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, statuses, 2))
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, statuses, 3))
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, statuses, -1))
    }

    @Test
    fun isAfterPowerOff_toleratesNullEntries() {
        val steps = listOf(shutdown(), null, RoutineStep(type = "notify", target = "pc", title = "t", body = "b"), null)
        assertTrue(RoutineRunRecords.isAfterPowerOff(steps, listOf(SUCCEEDED, null), 2))
        assertFalse(RoutineRunRecords.isAfterPowerOff(steps, listOf(SUCCEEDED), 3))
    }

    @Test
    fun actionTokens_coverEachHostStepKind() {
        assertEquals("SHUTDOWN", RoutineActionTokens.of(shutdown()))
        assertEquals(RoutineActionTokens.LAUNCH_APP, RoutineActionTokens.of(launchApp()))
        assertEquals("media.next", RoutineActionTokens.of(RoutineStep(type = "media", mediaAction = "next")))
        assertEquals(RoutineActionTokens.NOTIFY_PC, RoutineActionTokens.of(RoutineStep(type = "notify", target = "pc")))
    }

    @Test
    fun actionTokens_nullForUnknownMediaPhoneNotifyAndNonHostSteps() {
        assertNull(RoutineActionTokens.of(RoutineStep(type = "media", mediaAction = "rewind")))
        assertNull(RoutineActionTokens.of(RoutineStep(type = "media")))
        assertNull(RoutineActionTokens.of(notifyPhone()))
        assertNull(RoutineActionTokens.of(wake()))
        assertNull(RoutineActionTokens.of(null))
    }
}
