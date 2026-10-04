package com.clindsay94.remex.ui.screens

import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import org.json.JSONObject

/**
 * The phone's read-only view of the PC's logs and status (RemEx-pp4cm.13): the wire shapes, the
 * request builders, the parsers and the text format used by Copy and Share.
 *
 * The PC redacts every string before it sends it, so nothing here tries to; this file only has to
 * never throw, because [parseLogs] and [parseSummary] run on every `diagnostic_*` message and a
 * malformed one has to be ignored rather than end the connection.
 *
 * Mirrors `remex.core/Validation/DiagnosticsValidation.cs`: the same level words, the same cap of 500
 * lines a request, and the same one-request-a-second budget the PC enforces.
 */
object PcDiagnostics {

    const val LogsRequestType = "diagnostic_logs_get"
    const val LogsResultType = "diagnostic_logs_result"
    const val SummaryRequestType = "diagnostic_summary_get"
    const val SummaryResultType = "diagnostic_summary_result"

    /** The `RemexMessage` protocol version every control envelope carries; unchanged by this feature. */
    const val ProtocolVersion = 2

    /** Most lines one request may ask for. The PC refuses more. */
    const val MaxLinesPerRequest = 500

    /** Lines kept in memory. The PC's own buffer holds 3000. */
    const val MaxLinesKept = 3000

    /** How often Live asks for what is new. The PC allows one request a second. */
    const val PollIntervalMs = 2000L

    /** How long to wait for the PC to answer one request. */
    const val AnswerTimeoutMs = 5000L

    /** How long to wait before retrying a request the PC rate-limited. Just over its one second. */
    const val RateLimitRetryMs = 1100L

    /** `{"type":"diagnostic_logs_get",...}` for one page, as a whole envelope. */
    fun buildLogsRequest(
            correlationId: String,
            afterSeq: Long,
            minLevel: PcLogLevel,
            max: Int = MaxLinesPerRequest,
    ): String =
            JSONObject()
                    .put("type", LogsRequestType)
                    .put("protocolVersion", ProtocolVersion)
                    .put("correlationId", correlationId)
                    .put(
                            "diagnosticLogsRequest",
                            JSONObject()
                                    .put("afterSeq", afterSeq.coerceAtLeast(0L))
                                    .put("minLevel", minLevel.wire)
                                    .put("max", max.coerceIn(1, MaxLinesPerRequest))
                    )
                    .toString()

    /** `{"type":"diagnostic_summary_get",...}`. Carries no payload. */
    fun buildSummaryRequest(correlationId: String): String =
            JSONObject()
                    .put("type", SummaryRequestType)
                    .put("protocolVersion", ProtocolVersion)
                    .put("correlationId", correlationId)
                    .toString()

    /** The correlation id of a `diagnostic_*` envelope, or null for anything else. Never throws. */
    fun correlationIdOf(json: String?): String? {
        if (json.isNullOrBlank()) return null
        return runCatching {
                    val envelope = JSONObject(json)
                    val type = envelope.optString("type")
                    if (type != LogsResultType && type != SummaryResultType) return@runCatching null
                    if (envelope.isNull("correlationId")) null
                    else envelope.optString("correlationId").takeIf { it.isNotEmpty() }
                }
                .getOrNull()
    }

    /** What a `diagnostic_logs_result` says. [error] is a token from the PC, never a sentence. */
    data class LogsResult(
            val lines: List<PcLogLine>,
            val lastSeq: Long,
            val truncated: Boolean,
            val error: String?,
    )

    /** What a `diagnostic_summary_result` says. */
    data class SummaryResult(val rows: List<PcSummaryRow>, val error: String?)

    /**
     * Reads a whole `diagnostic_logs_result` envelope. Null for anything that is not one with a
     * `diagnosticLogsResponse` object, or is not JSON. A line with no usable sequence number or level
     * is skipped rather than failing the page. Never throws.
     */
    fun parseLogs(json: String?): LogsResult? {
        if (json.isNullOrBlank()) return null
        return runCatching {
                    val envelope = JSONObject(json)
                    if (envelope.optString("type") != LogsResultType) return@runCatching null
                    val payload =
                            envelope.optJSONObject("diagnosticLogsResponse")
                                    ?: return@runCatching null
                    val array = payload.optJSONArray("entries")
                    val lines = ArrayList<PcLogLine>(array?.length() ?: 0)
                    if (array != null) {
                        for (i in 0 until array.length()) {
                            val e = array.optJSONObject(i) ?: continue
                            val seq = if (e.has("seq")) e.optLong("seq", -1L) else -1L
                            if (seq < 0L) continue
                            lines +=
                                    PcLogLine(
                                            seq = seq,
                                            timeUtcMillis = parseTimeMillis(e.optStringOrNull("timeUtc")),
                                            level =
                                                    PcLogLevel.fromWire(e.optStringOrNull("level"))
                                                            ?: PcLogLevel.Information,
                                            category = e.optStringOrNull("category").orEmpty(),
                                            message = e.optStringOrNull("message").orEmpty(),
                                    )
                        }
                    }
                    LogsResult(
                            lines = lines,
                            lastSeq = payload.optLong("lastSeq", 0L),
                            truncated = payload.optBoolean("truncated", false),
                            error = payload.optStringOrNull("error"),
                    )
                }
                .getOrNull()
    }

    /** Reads a whole `diagnostic_summary_result` envelope. Null for anything else. Never throws. */
    fun parseSummary(json: String?): SummaryResult? {
        if (json.isNullOrBlank()) return null
        return runCatching {
                    val envelope = JSONObject(json)
                    if (envelope.optString("type") != SummaryResultType) return@runCatching null
                    val payload =
                            envelope.optJSONObject("diagnosticSummaryResponse")
                                    ?: return@runCatching null
                    val array = payload.optJSONArray("items")
                    val rows = ArrayList<PcSummaryRow>(array?.length() ?: 0)
                    if (array != null) {
                        for (i in 0 until array.length()) {
                            val item = array.optJSONObject(i) ?: continue
                            val key = item.optStringOrNull("key") ?: continue
                            rows +=
                                    PcSummaryRow(
                                            key = key,
                                            state = PcRowState.fromWire(item.optStringOrNull("state")),
                                            detail = item.optStringOrNull("detail").orEmpty(),
                                    )
                        }
                    }
                    SummaryResult(rows = rows, error = payload.optStringOrNull("error"))
                }
                .getOrNull()
    }

    /**
     * The lines whose message or category contains [query], ignoring case. A blank query keeps every
     * line, and the same list instance comes back so the caller can tell nothing was filtered.
     */
    fun filter(lines: List<PcLogLine>, query: String): List<PcLogLine> {
        val q = query.trim()
        if (q.isEmpty()) return lines
        return lines.filter {
            it.message.contains(q, ignoreCase = true) || it.category.contains(q, ignoreCase = true)
        }
    }

    /**
     * The lines as plain text for Copy and Share, oldest first, one per line:
     * `[09:30:12] [WRN] [Category] message`. Times are in the phone's own zone.
     */
    fun formatLines(lines: List<PcLogLine>, zone: ZoneId = ZoneId.systemDefault()): String {
        val clock = DateTimeFormatter.ofPattern("HH:mm:ss").withZone(zone)
        return lines.joinToString("\n") {
            val time = clock.format(java.time.Instant.ofEpochMilli(it.timeUtcMillis))
            "[$time] [${it.level.tag}] [${it.category}] ${it.message}"
        }
    }

    /** Most characters Copy and Share hand to the system. A clip travels through a binder buffer shared with the whole app. */
    const val MaxShareChars = 200_000

    /**
     * [formatLines] limited to the NEWEST [maxChars] characters, cut at a line boundary so no line is
     * split in half. Whole text unchanged when it already fits.
     */
    fun formatForShare(
            lines: List<PcLogLine>,
            zone: ZoneId = ZoneId.systemDefault(),
            maxChars: Int = MaxShareChars,
    ): String {
        val text = formatLines(lines, zone)
        if (text.length <= maxChars) return text
        val tail = text.takeLast(maxChars)
        val firstBreak = tail.indexOf('\n')
        return if (firstBreak < 0) tail else tail.substring(firstBreak + 1)
    }

    /** Epoch milliseconds for the PC's ISO timestamp, or 0 when it cannot be read. */
    fun parseTimeMillis(text: String?): Long {
        if (text.isNullOrBlank()) return 0L
        return runCatching { OffsetDateTime.parse(text).toInstant().toEpochMilli() }.getOrDefault(0L)
    }

    /** `optString` that reads a JSON null or a missing key as null instead of "null" or "". */
    private fun JSONObject.optStringOrNull(name: String): String? =
            if (isNull(name)) null else optString(name)
}

/** The PC log levels, lowest first. [wire] is the word the protocol carries; [tag] is the row label. */
enum class PcLogLevel(val wire: String, val tag: String) {
    Trace("trace", "TRC"),
    Debug("debug", "DBG"),
    Information("information", "INF"),
    Warning("warning", "WRN"),
    Error("error", "ERR"),
    Critical("critical", "CRT");

    companion object {
        fun fromWire(word: String?): PcLogLevel? = entries.firstOrNull { it.wire == word }
    }
}

/** One log line from the PC, already redacted by the PC. */
data class PcLogLine(
        val seq: Long,
        val timeUtcMillis: Long,
        val level: PcLogLevel,
        val category: String,
        val message: String,
)

/** How a status row came out on the PC. An unknown word reads as [Warn], never as [Ok]. */
enum class PcRowState {
    Ok,
    Warn,
    Error;

    companion object {
        fun fromWire(word: String?): PcRowState =
                when (word) {
                    "ok" -> Ok
                    "error" -> Error
                    else -> Warn
                }
    }
}

/** One row of the PC's diagnostics summary. [key] is the PC's word; the screen translates it. */
data class PcSummaryRow(val key: String, val state: PcRowState, val detail: String)

/** Why the PC's logs or summary could not be shown. */
enum class PcDiagError {
    /** The PC refused: this phone is not a proven paired one on the PC's side. */
    Refused,
    /** Nothing came back in time, or the request could not be sent. */
    NoAnswer,
    /** The PC answered that it could not build the page, or the answer was unreadable. */
    Unavailable,
}
