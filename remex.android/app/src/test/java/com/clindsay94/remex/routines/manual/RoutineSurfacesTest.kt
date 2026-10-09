package com.clindsay94.remex.routines.manual

import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.RoutineRunObserver
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineRun
import kotlinx.coroutines.CancellationException
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class RoutineSurfacesTest {
    private val run = RoutineRun(runId = "run-1")
    private val routine = Routine(id = "r-1", name = "Morning")
    private val calls = mutableListOf<String>()
    private val warnings = mutableListOf<String>()

    private inner class Named(private val name: String, private val failWith: (() -> Exception)? = null) : RoutineRunObserver {
        override fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?) = record("$name.progress($stepIndex)")

        override fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long) =
            record("$name.countdown($stepIndex,$endsAtUnixMs)")

        override fun onFinished(run: RoutineRun, routine: Routine?) = record("$name.finished(${run.runId},${routine?.id})")

        private fun record(call: String) {
            calls += call
            failWith?.let { throw it() }
        }
    }

    @Before
    fun captureLog() {
        RoutineLog.sink = RoutineLog.Sink { _, message, _ -> warnings += message }
    }

    @After
    fun restoreLog() {
        RoutineLog.resetSinkForTests()
    }

    @Test
    fun `every child receives each event, in registration order`() {
        val composite = CompositeRoutineRunObserver(Named("a"), Named("b"), Named("c"))
        composite.onProgress(run, routine, null)
        composite.onProgress(run, routine, 2)
        composite.onCountdown(run, routine, 3, 1_000L)
        composite.onFinished(run, null)
        composite.onFinished(run, routine)
        assertEquals(
            listOf(
                "a.progress(null)", "b.progress(null)", "c.progress(null)",
                "a.progress(2)", "b.progress(2)", "c.progress(2)",
                "a.countdown(3,1000)", "b.countdown(3,1000)", "c.countdown(3,1000)",
                "a.finished(run-1,null)", "b.finished(run-1,null)", "c.finished(run-1,null)",
                "a.finished(run-1,r-1)", "b.finished(run-1,r-1)", "c.finished(run-1,r-1)",
            ),
            calls,
        )
        assertTrue(warnings.isEmpty())
    }

    @Test
    fun `an empty composite does nothing`() {
        val composite = CompositeRoutineRunObserver()
        composite.onProgress(run, routine, 0)
        composite.onCountdown(run, routine, 0, 0L)
        composite.onFinished(run, routine)
        assertTrue(calls.isEmpty())
        assertTrue(warnings.isEmpty())
    }

    @Test
    fun `a throwing child does not stop the ones after it, and the failure is logged`() {
        val composite = CompositeRoutineRunObserver(Named("a"), Named("bad") { IllegalStateException("boom") }, Named("c"))
        composite.onProgress(run, routine, 1)
        composite.onCountdown(run, routine, 1, 5L)
        composite.onFinished(run, routine)
        assertEquals(
            listOf(
                "a.progress(1)", "bad.progress(1)", "c.progress(1)",
                "a.countdown(1,5)", "bad.countdown(1,5)", "c.countdown(1,5)",
                "a.finished(run-1,r-1)", "bad.finished(run-1,r-1)", "c.finished(run-1,r-1)",
            ),
            calls,
        )
        assertEquals(3, warnings.size)
    }

    @Test(expected = CancellationException::class)
    fun `cancellation is not swallowed`() {
        CompositeRoutineRunObserver(Named("bad") { CancellationException("cancelled") }, Named("c")).onFinished(run, routine)
    }
}
