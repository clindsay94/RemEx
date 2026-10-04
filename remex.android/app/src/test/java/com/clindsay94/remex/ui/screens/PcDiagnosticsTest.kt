package com.clindsay94.remex.ui.screens

import java.time.ZoneOffset
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The wire shapes the phone builds and reads for the PC logs and diagnostics page (RemEx-pp4cm.13).
 * The hand-written JSON in the parse cases is what `PhoneDiagnosticsService` sends, camelCase slot
 * names and all, so a field name only this side agrees with fails here.
 */
class PcDiagnosticsTest {

    private fun logsResult(
            entries: List<JSONObject>,
            lastSeq: Long = 0,
            truncated: Boolean = false,
            error: String? = null,
            correlationId: String? = "c1",
    ): String =
            JSONObject()
                    .put("type", "diagnostic_logs_result")
                    .apply { if (correlationId != null) put("correlationId", correlationId) }
                    .put(
                            "diagnosticLogsResponse",
                            JSONObject()
                                    .put("entries", JSONArray(entries))
                                    .put("lastSeq", lastSeq)
                                    .put("truncated", truncated)
                                    .apply { if (error != null) put("error", error) }
                    )
                    .toString()

    private fun entry(seq: Long, level: String = "information", message: String = "m$seq") =
            JSONObject()
                    .put("seq", seq)
                    .put("timeUtc", "2026-10-03T09:30:12+00:00")
                    .put("level", level)
                    .put("category", "Remex.Test")
                    .put("message", message)

    @Test
    fun `the logs request carries the type, version, correlation id and a clamped payload`() {
        val json = JSONObject(PcDiagnostics.buildLogsRequest("abc", afterSeq = 42, minLevel = PcLogLevel.Warning, max = 9999))

        assertEquals("diagnostic_logs_get", json.getString("type"))
        assertEquals(2, json.getInt("protocolVersion"))
        assertEquals("abc", json.getString("correlationId"))
        val payload = json.getJSONObject("diagnosticLogsRequest")
        assertEquals(42L, payload.getLong("afterSeq"))
        assertEquals("warning", payload.getString("minLevel"))
        assertEquals("the PC refuses more than 500", 500, payload.getInt("max"))
    }

    @Test
    fun `a negative after-seq and a zero max are clamped to what the PC accepts`() {
        val payload =
                JSONObject(PcDiagnostics.buildLogsRequest("x", afterSeq = -5, minLevel = PcLogLevel.Debug, max = 0))
                        .getJSONObject("diagnosticLogsRequest")

        assertEquals(0L, payload.getLong("afterSeq"))
        assertEquals(1, payload.getInt("max"))
    }

    @Test
    fun `the summary request carries no payload`() {
        val json = JSONObject(PcDiagnostics.buildSummaryRequest("s1"))

        assertEquals("diagnostic_summary_get", json.getString("type"))
        assertEquals("s1", json.getString("correlationId"))
        assertTrue(!json.has("diagnosticLogsRequest"))
    }

    @Test
    fun `a logs result is read with its lines, cursor and truncation`() {
        val result =
                PcDiagnostics.parseLogs(
                        logsResult(listOf(entry(7, "error", "boom"), entry(8)), lastSeq = 9, truncated = true)
                )!!

        assertEquals(listOf(7L, 8L), result.lines.map { it.seq })
        assertEquals(PcLogLevel.Error, result.lines[0].level)
        assertEquals("boom", result.lines[0].message)
        assertEquals("Remex.Test", result.lines[0].category)
        assertEquals(1791019812000L, result.lines[0].timeUtcMillis)
        assertEquals(9L, result.lastSeq)
        assertTrue(result.truncated)
        assertNull(result.error)
    }

    @Test
    fun `an error token is kept and a bad line is skipped rather than failing the page`() {
        val bad = JSONObject().put("level", "error").put("message", "no sequence number")
        val result = PcDiagnostics.parseLogs(logsResult(listOf(bad, entry(3)), error = null))!!
        assertEquals(listOf(3L), result.lines.map { it.seq })

        val limited = PcDiagnostics.parseLogs(logsResult(emptyList(), error = "rate_limited"))!!
        assertEquals("rate_limited", limited.error)
    }

    @Test
    fun `an unknown level reads as information and a JSON null text is not the word null`() {
        val odd = entry(1, level = "verbose").put("category", JSONObject.NULL)

        val line = PcDiagnostics.parseLogs(logsResult(listOf(odd)))!!.lines.single()

        assertEquals(PcLogLevel.Information, line.level)
        assertEquals("", line.category)
    }

    @Test
    fun `garbage in any shape is ignored rather than thrown`() {
        assertNull(PcDiagnostics.parseLogs(null))
        assertNull(PcDiagnostics.parseLogs(""))
        assertNull(PcDiagnostics.parseLogs("not json"))
        assertNull(PcDiagnostics.parseLogs("""{"type":"diagnostic_logs_result"}"""))
        assertNull(PcDiagnostics.parseLogs("""{"type":"home_pins_sync","diagnosticLogsResponse":{}}"""))
        assertNull(PcDiagnostics.parseSummary("""{"type":"diagnostic_summary_result","diagnosticSummaryResponse":"x"}"""))
        assertNull(PcDiagnostics.correlationIdOf("""{"type":"diagnostic_logs_result","correlationId":null}"""))
        assertNull(PcDiagnostics.correlationIdOf("""{"type":"clipboard_content","correlationId":"x"}"""))
        assertEquals("c1", PcDiagnostics.correlationIdOf(logsResult(emptyList())))
    }

    @Test
    fun `a summary is read and an unknown state is a warning, never ok`() {
        val json =
                JSONObject()
                        .put("type", "diagnostic_summary_result")
                        .put(
                                "diagnosticSummaryResponse",
                                JSONObject()
                                        .put(
                                                "items",
                                                JSONArray()
                                                        .put(JSONObject().put("key", "listener").put("state", "ok").put("detail", "5005"))
                                                        .put(JSONObject().put("key", "firewall").put("state", "error"))
                                                        .put(JSONObject().put("key", "odd").put("state", "fine"))
                                                        .put(JSONObject().put("state", "ok"))
                                        )
                        )
                        .toString()

        val rows = PcDiagnostics.parseSummary(json)!!.rows

        assertEquals(listOf("listener", "firewall", "odd"), rows.map { it.key })
        assertEquals(PcRowState.Ok, rows[0].state)
        assertEquals("5005", rows[0].detail)
        assertEquals(PcRowState.Error, rows[1].state)
        assertEquals(PcRowState.Warn, rows[2].state)
    }

    @Test
    fun `search matches message or category ignoring case, and blank returns the same list`() {
        val a = PcLogLine(1, 0, PcLogLevel.Information, "Remex.Pairing", "Phone connected")
        val b = PcLogLine(2, 0, PcLogLevel.Warning, "Remex.Capture", "Frame dropped")
        val all = listOf(a, b)

        assertEquals(listOf(b), PcDiagnostics.filter(all, "FRAME"))
        assertEquals(listOf(a), PcDiagnostics.filter(all, " pairing "))
        assertTrue(PcDiagnostics.filter(all, "nothing like this").isEmpty())
        assertSame(all, PcDiagnostics.filter(all, "   "))
    }

    @Test
    fun `copy and share text is one line per entry, oldest first, in the given zone`() {
        val lines =
                listOf(
                        PcLogLine(1, 1791019812000L, PcLogLevel.Warning, "Cat", "first"),
                        PcLogLine(2, 1791019813000L, PcLogLevel.Error, "Cat", "second"),
                )

        assertEquals(
                "[09:30:12] [WRN] [Cat] first\n[09:30:13] [ERR] [Cat] second",
                PcDiagnostics.formatLines(lines, ZoneOffset.UTC)
        )
    }

    @Test
    fun `an over-long share keeps the newest lines and never splits one`() {
        val lines = (1L..100L).map { PcLogLine(it, 1791019812000L, PcLogLevel.Information, "C", "line number $it") }
        val full = PcDiagnostics.formatLines(lines, ZoneOffset.UTC)

        val cut = PcDiagnostics.formatForShare(lines, ZoneOffset.UTC, maxChars = 300)

        assertTrue(cut.length <= 300)
        assertTrue("keeps the newest", cut.endsWith("line number 100"))
        assertTrue("starts on a whole line", cut.startsWith("["))
        assertTrue(full.endsWith(cut))
        assertEquals(full, PcDiagnostics.formatForShare(lines, ZoneOffset.UTC, maxChars = full.length))
    }

    @Test
    fun `the level words match the PC's`() {
        assertEquals(
                listOf("trace", "debug", "information", "warning", "error", "critical"),
                PcLogLevel.entries.map { it.wire }
        )
        assertNotNull(PcLogLevel.fromWire("critical"))
        assertNull(PcLogLevel.fromWire("INFORMATION"))
    }

    // The screen's LazyColumns key rows by seq and by summary key; a repeat from the PC would be a
    // duplicate key, which crashes the list (RemEx-pp4cm.13).

    @Test
    fun `a repeated sequence number keeps only its first line`() {
        val result = PcDiagnostics.parseLogs(logsResult(listOf(entry(1), entry(2, message = "first"), entry(2, message = "second"), entry(3))))!!

        assertEquals(listOf(1L, 2L, 3L), result.lines.map { it.seq })
        assertEquals("first", result.lines[1].message)
    }

    @Test
    fun `a repeated summary key keeps only its first row`() {
        val json =
                JSONObject()
                        .put("type", "diagnostic_summary_result")
                        .put(
                                "diagnosticSummaryResponse",
                                JSONObject()
                                        .put(
                                                "items",
                                                JSONArray()
                                                        .put(JSONObject().put("key", "listener").put("state", "ok").put("detail", "first"))
                                                        .put(JSONObject().put("key", "firewall").put("state", "ok"))
                                                        .put(JSONObject().put("key", "listener").put("state", "error").put("detail", "second"))
                                        )
                        )
                        .toString()

        val rows = PcDiagnostics.parseSummary(json)!!.rows

        assertEquals(listOf("listener", "firewall"), rows.map { it.key })
        assertEquals("first", rows[0].detail)
    }
}
