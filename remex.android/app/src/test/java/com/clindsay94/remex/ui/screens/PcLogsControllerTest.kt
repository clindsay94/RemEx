package com.clindsay94.remex.ui.screens

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [PcLogsController] (RemEx-pp4cm.13): paging by sequence number, the Live poll, and above all that the
 * poll stops the moment the screen is not visible, Live is off or the phone is not authenticated.
 * A fake PC answers on a shared flow; time is the test scheduler's.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PcLogsControllerTest {

    /** A PC holding [all] lines, answering like `PhoneDiagnosticsService`, and recording what it was asked. */
    private class FakePc(val inbound: MutableSharedFlow<String>) {
        var all: List<Long> = emptyList()
        var head: Long = 0
        val requests = ArrayList<JSONObject>()
        var respond = true
        var errorToken: String? = null
        var rateLimitedTimes = 0
        var sendSucceeds = true

        suspend fun send(envelope: String): Boolean {
            val request = JSONObject(envelope)
            requests += request
            if (!sendSucceeds) return false
            if (!respond) return true
            val corr = request.getString("correlationId")
            if (rateLimitedTimes > 0) {
                rateLimitedTimes--
                inbound.emit(logsEnvelope(corr, emptyList(), 0, false, "rate_limited"))
                return true
            }
            if (request.getString("type") == "diagnostic_summary_get") {
                inbound.emit(
                        JSONObject()
                                .put("type", "diagnostic_summary_result")
                                .put("correlationId", corr)
                                .put(
                                        "diagnosticSummaryResponse",
                                        JSONObject()
                                                .put(
                                                        "items",
                                                        JSONArray()
                                                                .put(JSONObject().put("key", "version").put("state", "ok").put("detail", "3.0.0"))
                                                )
                                                .apply { errorToken?.let { put("error", it) } }
                                )
                                .toString()
                )
                return true
            }
            val payload = request.getJSONObject("diagnosticLogsRequest")
            val after = payload.getLong("afterSeq")
            val max = payload.getInt("max")
            if (errorToken != null) {
                inbound.emit(logsEnvelope(corr, emptyList(), 0, false, errorToken))
                return true
            }
            val effectiveAfter = if (after > head) 0 else after
            val matching = all.filter { it > effectiveAfter }
            val page = if (effectiveAfter == 0L) matching.takeLast(max) else matching.take(max)
            val truncated = matching.size > page.size
            val last = if (truncated && effectiveAfter != 0L) page.last() else head
            inbound.emit(logsEnvelope(corr, page, last, truncated, null))
            return true
        }

        fun logsRequests() = requests.filter { it.getString("type") == "diagnostic_logs_get" }
        fun afterSeqs() = logsRequests().map { it.getJSONObject("diagnosticLogsRequest").getLong("afterSeq") }
    }

    private class Harness(scope: TestScope) {
        val inbound = MutableSharedFlow<String>(extraBufferCapacity = 64)
        val pc = FakePc(inbound)
        val ready = MutableStateFlow(true)
        private var next = 0
        val controller =
                PcLogsController(
                        scope = scope.backgroundScope,
                        send = pc::send,
                        inbound = inbound,
                        ready = ready,
                        newCorrelationId = { "c${next++}" },
                        pollIntervalMs = 2000,
                        answerTimeoutMs = 5000,
                        rateLimitRetryMs = 1100,
                )
    }

    companion object {
        fun logsEnvelope(corr: String, seqs: List<Long>, lastSeq: Long, truncated: Boolean, error: String?): String =
                JSONObject()
                        .put("type", "diagnostic_logs_result")
                        .put("correlationId", corr)
                        .put(
                                "diagnosticLogsResponse",
                                JSONObject()
                                        .put(
                                                "entries",
                                                JSONArray(
                                                        seqs.map {
                                                            JSONObject()
                                                                    .put("seq", it)
                                                                    .put("timeUtc", "2026-10-03T09:30:12+00:00")
                                                                    .put("level", "information")
                                                                    .put("category", "Cat")
                                                                    .put("message", "line $it")
                                                        }
                                                )
                                        )
                                        .put("lastSeq", lastSeq)
                                        .put("truncated", truncated)
                                        .apply { if (error != null) put("error", error) }
                        )
                        .toString()
    }

    private fun TestScope.harness(lines: List<Long> = (1L..10L).toList()) =
            Harness(this).also {
                it.pc.all = lines
                it.pc.head = lines.lastOrNull() ?: 0
            }

    // ── paging ─────────────────────────────────────────────────────────────────────────────────

    @Test
    fun `a refresh loads the newest page from the top`() = runTest {
        val h = harness()
        h.controller.refreshLogs()
        runCurrent()

        val state = h.controller.logs.value
        assertEquals((1L..10L).toList(), state.lines.map { it.seq })
        assertEquals(10L, state.lastSeq)
        assertTrue(state.loadedOnce)
        assertFalse(state.loading)
        assertNull(state.error)
        assertEquals(listOf(0L), h.pc.afterSeqs())
    }

    @Test
    fun `a poll asks only for what came after the last line and appends it`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        runCurrent()
        assertEquals(10L, h.controller.logs.value.lastSeq)

        h.pc.all = (1L..13L).toList()
        h.pc.head = 13
        advanceTimeBy(2001)
        runCurrent()

        assertEquals((1L..13L).toList(), h.controller.logs.value.lines.map { it.seq })
        assertEquals(listOf(0L, 10L), h.pc.afterSeqs())
    }

    @Test
    fun `a PC restart replaces the list instead of mixing two runs`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        runCurrent()

        // The PC restarted: numbering began again and is lower than what the phone holds.
        h.pc.all = listOf(1L, 2L)
        h.pc.head = 2
        advanceTimeBy(2001)
        runCurrent()

        assertEquals(listOf(1L, 2L), h.controller.logs.value.lines.map { it.seq })
        assertEquals(2L, h.controller.logs.value.lastSeq)
    }

    @Test
    fun `changing the level starts over at that level`() = runTest {
        val h = harness()
        h.controller.refreshLogs()
        runCurrent()

        h.controller.setMinLevel(PcLogLevel.Error)
        runCurrent()

        val last = h.pc.logsRequests().last().getJSONObject("diagnosticLogsRequest")
        assertEquals("error", last.getString("minLevel"))
        assertEquals(0L, last.getLong("afterSeq"))
        assertEquals(PcLogLevel.Error, h.controller.logs.value.minLevel)
    }

    @Test
    fun `lines are capped in memory`() = runTest {
        val h = harness()
        h.controller.refreshLogs()
        runCurrent()
        val seqs = (1L..(PcDiagnostics.MaxLinesKept + 600L)).toList()
        h.pc.all = seqs
        h.pc.head = seqs.last()
        h.controller.setVisible(true)
        advanceTimeBy(2001)
        runCurrent()

        // Pages come 500 at a time, so several polls are needed to catch up; none may exceed the cap.
        repeat(8) {
            advanceTimeBy(2001)
            runCurrent()
        }
        assertTrue(h.controller.logs.value.lines.size <= PcDiagnostics.MaxLinesKept)
        assertEquals(seqs.last(), h.controller.logs.value.lastSeq)
    }

    // ── the poll's three conditions ────────────────────────────────────────────────────────────

    @Test
    fun `nothing is requested until the screen is visible`() = runTest {
        val h = harness()
        advanceTimeBy(30_000)
        runCurrent()

        assertTrue(h.pc.requests.isEmpty())
        assertFalse(h.controller.isPolling)
    }

    @Test
    fun `polling runs every two seconds while visible and live`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        runCurrent()
        assertEquals(1, h.pc.logsRequests().size)

        advanceTimeBy(2001)
        runCurrent()
        advanceTimeBy(2000)
        runCurrent()

        assertEquals(3, h.pc.logsRequests().size)
        assertTrue(h.controller.isPolling)
    }

    @Test
    fun `polling stops when the screen is no longer visible`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        advanceTimeBy(4100)
        runCurrent()
        val before = h.pc.requests.size
        assertTrue(before >= 2)

        h.controller.setVisible(false)
        advanceTimeBy(60_000)
        runCurrent()

        assertEquals("no request may go out once the screen is hidden", before, h.pc.requests.size)
        assertFalse(h.controller.isPolling)
    }

    @Test
    fun `polling resumes and catches up when the screen comes back`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        runCurrent()
        h.controller.setVisible(false)
        h.pc.all = (1L..15L).toList()
        h.pc.head = 15
        advanceTimeBy(10_000)

        h.controller.setVisible(true)
        runCurrent()

        assertEquals(15L, h.controller.logs.value.lastSeq)
        assertEquals(10L, h.pc.afterSeqs().last())
    }

    @Test
    fun `polling stops when Live is switched off and nothing is lost`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        runCurrent()
        val before = h.pc.requests.size

        h.controller.setLive(false)
        advanceTimeBy(60_000)
        runCurrent()

        assertEquals(before, h.pc.requests.size)
        assertEquals(10, h.controller.logs.value.lines.size)
    }

    @Test
    fun `polling stops when the phone is not authenticated and restarts when it is`() = runTest {
        val h = harness()
        h.controller.setVisible(true)
        runCurrent()
        val before = h.pc.requests.size

        h.ready.value = false
        advanceTimeBy(60_000)
        runCurrent()
        assertEquals(before, h.pc.requests.size)

        h.ready.value = true
        runCurrent()
        assertTrue(h.pc.requests.size > before)
    }

    @Test
    fun `a request in flight when the screen is hidden is abandoned and its late answer ignored`() = runTest {
        val h = harness()
        h.pc.respond = false
        h.controller.setVisible(true)
        runCurrent()
        assertEquals(1, h.pc.requests.size)

        h.controller.setVisible(false)
        runCurrent()
        // The PC answers late.
        h.inbound.emit(logsEnvelope("c0", listOf(99L), 99, false, null))
        runCurrent()

        assertTrue(h.controller.logs.value.lines.isEmpty())
    }

    // ── errors ─────────────────────────────────────────────────────────────────────────────────

    @Test
    fun `no answer in time is reported as no answer`() = runTest {
        val h = harness()
        h.pc.respond = false
        h.controller.refreshLogs()
        runCurrent()
        advanceTimeBy(5001)
        runCurrent()

        assertEquals(PcDiagError.NoAnswer, h.controller.logs.value.error)
        assertFalse(h.controller.logs.value.loading)
    }

    @Test
    fun `a send that could not leave is reported as no answer without waiting`() = runTest {
        val h = harness()
        h.pc.sendSucceeds = false
        h.controller.refreshLogs()
        runCurrent()

        assertEquals(PcDiagError.NoAnswer, h.controller.logs.value.error)
    }

    @Test
    fun `a refusal from the PC is reported as refused and a success clears it`() = runTest {
        val h = harness()
        h.pc.errorToken = "refused"
        h.controller.refreshLogs()
        runCurrent()
        assertEquals(PcDiagError.Refused, h.controller.logs.value.error)

        h.pc.errorToken = null
        h.controller.refreshLogs()
        runCurrent()
        assertNull(h.controller.logs.value.error)
        assertEquals(10, h.controller.logs.value.lines.size)
    }

    @Test
    fun `a rate-limited answer is retried once after a beat and never shown as an error`() = runTest {
        val h = harness()
        h.pc.rateLimitedTimes = 1
        h.controller.refreshLogs()
        runCurrent()
        assertNull(h.controller.logs.value.error)
        assertEquals(1, h.pc.requests.size)

        advanceTimeBy(1101)
        runCurrent()

        assertEquals(2, h.pc.requests.size)
        assertEquals(10, h.controller.logs.value.lines.size)
        assertNull(h.controller.logs.value.error)
    }

    @Test
    fun `an unreadable answer is unavailable`() = runTest {
        val h = harness()
        h.controller.refreshLogs()
        runCurrent()
        // Replace the PC with one that answers garbage under the right correlation id.
        h.pc.respond = false
        h.controller.refreshLogs()
        runCurrent()
        h.inbound.emit("""{"type":"diagnostic_logs_result","correlationId":"c1","diagnosticLogsResponse":"nope"}""")
        runCurrent()

        assertEquals(PcDiagError.Unavailable, h.controller.logs.value.error)
    }

    // ── summary ────────────────────────────────────────────────────────────────────────────────

    @Test
    fun `the summary loads on request and not before`() = runTest {
        val h = harness()
        runCurrent()
        assertTrue(h.controller.summary.value.rows.isEmpty())
        assertTrue(h.pc.requests.isEmpty())

        h.controller.refreshSummary()
        runCurrent()

        val state = h.controller.summary.value
        assertEquals(listOf("version"), state.rows.map { it.key })
        assertTrue(state.loadedOnce)
        assertFalse(state.loading)
    }

    @Test
    fun `the summary is not requested while the phone is not authenticated`() = runTest {
        val h = harness()
        h.ready.value = false
        h.controller.refreshSummary()
        runCurrent()

        assertTrue(h.pc.requests.isEmpty())
    }
}
