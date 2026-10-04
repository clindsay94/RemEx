package com.clindsay94.remex.ui.screens

import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull

/** What the Logs tab shows. [lines] are oldest first and already limited to [minLevel] by the PC. */
data class PcLogsState(
        val lines: List<PcLogLine> = emptyList(),
        /** The last sequence number the PC reported, sent back as `afterSeq` to ask for what is new. */
        val lastSeq: Long = 0L,
        val minLevel: PcLogLevel = PcLogLevel.Information,
        val loading: Boolean = false,
        /** True once a page has arrived, so an empty list means "nothing logged", not "not loaded". */
        val loadedOnce: Boolean = false,
        /** The PC had more matching lines than one page; only the newest are shown. */
        val truncated: Boolean = false,
        val error: PcDiagError? = null,
)

/** What the Diagnostics tab shows. */
data class PcSummaryState(
        val rows: List<PcSummaryRow> = emptyList(),
        val loading: Boolean = false,
        val loadedOnce: Boolean = false,
        val error: PcDiagError? = null,
)

/**
 * Drives the PC logs and diagnostics screen (RemEx-pp4cm.13): paging by sequence number, the Live
 * poll, and the one-shot summary. No Android types, so a unit test can run it on a test scheduler.
 *
 * **POLLING HAS THREE CONDITIONS AND LOSES THE LOT WHEN ANY ONE FAILS.** A request goes out every
 * [pollIntervalMs] only while the screen is visible ([setVisible]), Live is on ([setLive]) and the
 * phone is authenticated to the PC ([ready]). Visibility is the one that matters for the battery and
 * the PC: a screen left behind, the app backgrounded or the Diagnostics tab selected asks for nothing.
 * `collectLatest` cancels the loop, and any request still waiting, the moment a condition goes false.
 *
 * **ANSWERS ARE MATCHED BY CORRELATION ID**, not by "the next message of that type", so a late answer
 * to a request that timed out is dropped instead of being applied as the answer to a newer one.
 *
 * **THE PC PACES IT TOO.** One request of each kind a second per session; a `rate_limited` answer is
 * retried once after [RateLimitRetryMs] and never shown as an error, because it just means two
 * requests landed close together.
 */
class PcLogsController(
        private val scope: CoroutineScope,
        /** Sends one whole envelope (off the main thread, if that matters). False when it could not be sent at all. */
        private val send: suspend (String) -> Boolean,
        /** Every `diagnostic_*` envelope the PC sends, raw. */
        inbound: Flow<String>,
        /** True while the phone is connected and authenticated to the PC. */
        val ready: StateFlow<Boolean>,
        private val newCorrelationId: () -> String = { UUID.randomUUID().toString() },
        private val pollIntervalMs: Long = PcDiagnostics.PollIntervalMs,
        private val answerTimeoutMs: Long = PcDiagnostics.AnswerTimeoutMs,
        private val rateLimitRetryMs: Long = PcDiagnostics.RateLimitRetryMs,
) {
    private val _logs = MutableStateFlow(PcLogsState())
    val logs: StateFlow<PcLogsState> = _logs.asStateFlow()

    private val _summary = MutableStateFlow(PcSummaryState())
    val summary: StateFlow<PcSummaryState> = _summary.asStateFlow()

    private val _live = MutableStateFlow(true)
    /** Whether Live is switched on. On by default: the person came to see what is happening. */
    val live: StateFlow<Boolean> = _live.asStateFlow()

    private val _visible = MutableStateFlow(false)
    /** Whether the Logs tab is on screen right now. */
    val visible: StateFlow<Boolean> = _visible.asStateFlow()

    private val pending = ConcurrentHashMap<String, CompletableDeferred<String>>()
    private val logsLock = Mutex()
    private val summaryLock = Mutex()

    init {
        scope.launch {
            inbound.collect { raw ->
                PcDiagnostics.correlationIdOf(raw)?.let { pending.remove(it)?.complete(raw) }
            }
        }
        scope.launch {
            combine(_visible, _live, ready) { visible, live, ready -> visible && live && ready }
                    .collectLatest { shouldPoll ->
                        if (!shouldPoll) return@collectLatest
                        while (true) {
                            val state = _logs.value
                            fetchLogs(afterSeq = if (state.loadedOnce) state.lastSeq else 0L, reset = !state.loadedOnce)
                            delay(pollIntervalMs)
                        }
                    }
        }
    }

    /** The Logs tab came on screen or went off it. Off stops the Live poll at once. */
    fun setVisible(visible: Boolean) {
        _visible.value = visible
    }

    fun setLive(live: Boolean) {
        _live.value = live
    }

    /** Whether the Live poll is running right now. */
    val isPolling: Boolean
        get() = _visible.value && _live.value && ready.value

    /**
     * Switches the level the PC filters by and loads that level from the top. The lines already shown
     * are dropped, because a lower level interleaves lines that were never fetched.
     */
    fun setMinLevel(level: PcLogLevel) {
        if (_logs.value.minLevel == level) return
        _logs.value = PcLogsState(minLevel = level, loading = true)
        scope.launch { fetchLogs(afterSeq = 0L, reset = true) }
    }

    /** A person pressed Refresh: reload the newest page from the top. */
    fun refreshLogs() {
        _logs.update { it.copy(loading = true) }
        scope.launch { fetchLogs(afterSeq = 0L, reset = true) }
    }

    /** Loads the status rows. A press of Refresh on the Diagnostics tab, or opening it. */
    fun refreshSummary() {
        scope.launch {
            summaryLock.withLock {
                if (!ready.value) return@withLock
                _summary.update { it.copy(loading = true, error = null) }
                var attempt = 0
                while (true) {
                    val id = newCorrelationId()
                    val raw = request(PcDiagnostics.buildSummaryRequest(id), id)
                    if (raw == null) {
                        _summary.update { it.copy(loading = false, error = PcDiagError.NoAnswer) }
                        return@withLock
                    }
                    val result = PcDiagnostics.parseSummary(raw)
                    if (result == null) {
                        _summary.update { it.copy(loading = false, error = PcDiagError.Unavailable) }
                        return@withLock
                    }
                    if (result.error == RATE_LIMITED && attempt++ < 1) {
                        delay(rateLimitRetryMs)
                        continue
                    }
                    val error = result.error?.let(::errorFor)
                    _summary.update {
                        if (error != null) it.copy(loading = false, error = error)
                        else PcSummaryState(rows = result.rows, loadedOnce = true)
                    }
                    return@withLock
                }
            }
        }
    }

    private suspend fun fetchLogs(afterSeq: Long, reset: Boolean) {
        logsLock.withLock {
            if (!ready.value) {
                _logs.update { it.copy(loading = false) }
                return
            }
            var attempt = 0
            while (true) {
                val level = _logs.value.minLevel
                val id = newCorrelationId()
                val raw =
                        request(PcDiagnostics.buildLogsRequest(id, afterSeq, level), id)
                if (raw == null) {
                    _logs.update { it.copy(loading = false, error = PcDiagError.NoAnswer) }
                    return
                }
                val result = PcDiagnostics.parseLogs(raw)
                if (result == null) {
                    _logs.update { it.copy(loading = false, error = PcDiagError.Unavailable) }
                    return
                }
                if (result.error == RATE_LIMITED) {
                    if (attempt++ < 1) {
                        delay(rateLimitRetryMs)
                        continue
                    }
                    // Still limited after waiting: not the person's problem. The next poll retries.
                    _logs.update { it.copy(loading = false) }
                    return
                }
                result.error?.let { token ->
                    _logs.update { it.copy(loading = false, error = errorFor(token)) }
                    return
                }
                _logs.update { apply(it, level, afterSeq, reset, result) }
                return
            }
        }
    }

    /** Folds one page into the state. A level change that happened while the request was out is dropped. */
    private fun apply(
            state: PcLogsState,
            requestedLevel: PcLogLevel,
            afterSeq: Long,
            reset: Boolean,
            result: PcDiagnostics.LogsResult,
    ): PcLogsState {
        if (state.minLevel != requestedLevel) return state
        val entries = result.lines
        // The PC restarted when its newest sequence number is lower than ours, or the first line is not
        // newer than what we hold: its numbering began again, so what we have is from another run.
        val restarted =
                result.lastSeq < state.lastSeq ||
                        (entries.isNotEmpty() && entries.first().seq <= state.lastSeq && afterSeq != 0L)
        val replace = reset || afterSeq == 0L || restarted
        val merged =
                if (replace) entries
                else (state.lines + entries.filter { it.seq > state.lastSeq })
        return state.copy(
                lines = merged.takeLast(PcDiagnostics.MaxLinesKept),
                lastSeq = result.lastSeq,
                truncated = if (replace) result.truncated else state.truncated,
                loading = false,
                loadedOnce = true,
                error = null,
        )
    }

    /** Sends [envelope] and waits for the answer carrying [id]. Null when it could not be sent or timed out. */
    private suspend fun request(envelope: String, id: String): String? {
        val waiting = CompletableDeferred<String>()
        pending[id] = waiting
        try {
            if (!send(envelope)) return null
            return withTimeoutOrNull(answerTimeoutMs) { waiting.await() }
        } finally {
            pending.remove(id)
        }
    }

    private fun errorFor(token: String): PcDiagError =
            when (token) {
                "refused" -> PcDiagError.Refused
                else -> PcDiagError.Unavailable
            }

    private companion object {
        const val RATE_LIMITED = "rate_limited"
    }
}
