package com.clindsay94.remex.service

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Waits for a download's data stream to finish, but gives up when the stream has gone quiet
 * (RemEx-pp4cm.9).
 *
 * **A LOST FRAME USED TO COST THE WHOLE QUEUE SIX HOURS.** The only bound on this wait was the
 * six-hour ceiling for a single transfer. If the final frame never arrived - dropped before the sink
 * existed, or the PC gave up and released the transfer - the download kept the one-at-a-time drain
 * slot for the whole ceiling, and every file behind it showed "Queued". The ceiling is still here
 * for a transfer that is slow but moving; what is new is that silence ends it: after [stallMs]
 * without a frame ([lastActivityMs]) the wait returns false, the row fails and the queue moves on.
 *
 * @return the stream's own verdict once [done] completes, or false for a stall or the ceiling.
 */
internal suspend fun awaitDownloadStream(
    done: CompletableDeferred<Boolean>,
    ceilingMs: Long,
    stallMs: Long,
    lastActivityMs: () -> Long,
    clockMs: () -> Long,
    pollMs: Long = 1_000L,
): Boolean {
    val startedAt = clockMs()
    while (true) {
        withTimeoutOrNull(pollMs) { done.await() }?.let { return it }
        val now = clockMs()
        if (now - startedAt >= ceilingMs) return false
        if (now - maxOf(lastActivityMs(), startedAt) >= stallMs) return false
    }
}
