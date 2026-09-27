package com.clindsay94.remex.routines

import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.routines.model.RoutineInboundMessage
import com.clindsay94.remex.routines.model.RoutineStepResultPayload
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * `routine_step_result` travels on its own flow (S1a Kotlin review finding 1, RemEx-pp0rt.5).
 *
 * The runner waits on exactly one step result per host step. When it shared a DROP_OLDEST buffer
 * with run-report pages, a burst of pages could evict it and the phone timed the step out as "no
 * answer from the PC" although the PC had answered.
 */
class RoutineStepResultRoutingTest {
    private val stepResult =
        """{"type":"routine_step_result","routineStepResult":{"runId":"run-step","stepIndex":2,"outcome":"succeeded","reasonCode":"ok","countdownShown":false}}"""

    private fun runReport(i: Int) =
        """{"type":"routine_run_report","routineRunReport":{"runs":[{"runId":"pc-$i","routineId":"r","outcome":"succeeded","routineRevision":1,"testRun":false,"triggeredAtUnixMs":0,"startedAtUnixMs":0}],"more":true,"live":false}}"""

    @Test
    fun `a step result is delivered on its own flow even behind a burst of run reports`() =
        runBlocking {
            val got = CompletableDeferred<RoutineStepResultPayload>()
            val seen = mutableListOf<RoutineInboundMessage>()
            val hold = CompletableDeferred<Unit>()
            val steps =
                launch(Dispatchers.Unconfined, start = CoroutineStart.UNDISPATCHED) {
                    RemexClientManager.routineStepResults.collect { if (it.runId == "run-step") got.complete(it) }
                }
            // A slow consumer of everything else: its buffer overflows and drops, which must not matter.
            val others =
                launch(Dispatchers.Unconfined, start = CoroutineStart.UNDISPATCHED) {
                    RemexClientManager.routineMessages.collect {
                        seen += it
                        hold.await()
                    }
                }
            try {
                repeat(200) { RemexClientManager.onRoutineMessage(runReport(it)) }
                RemexClientManager.onRoutineMessage(stepResult)
                val result = withTimeout(5_000) { got.await() }
                assertEquals(2, result.stepIndex)
                assertEquals("succeeded", result.outcome)
                assertTrue("a step result never reaches the general flow", seen.none { it is RoutineInboundMessage.StepResult })
            } finally {
                hold.complete(Unit)
                steps.cancel()
                others.cancel()
            }
        }

    @Test
    fun `unknown routine types are dropped, not forwarded or thrown`() =
        runBlocking {
            val seen = mutableListOf<RoutineInboundMessage>()
            val job =
                launch(Dispatchers.Unconfined, start = CoroutineStart.UNDISPATCHED) {
                    RemexClientManager.routineMessages.collect { seen += it }
                }
            try {
                RemexClientManager.onRoutineMessage("""{"type":"routine_from_the_future","x":1}""")
                RemexClientManager.onRoutineMessage("not json at all")
                RemexClientManager.onRoutineMessage(null)
                RemexClientManager.onRoutineMessage(runReport(1))
                assertEquals(listOf("RunReport"), seen.map { it::class.simpleName })
            } finally {
                job.cancel()
            }
        }
}
