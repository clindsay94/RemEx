package com.clindsay94.remex.service

import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.delay

/**
 * How many messages may sit in the native outbound queue before a legacy v2 transfer loop waits
 * (perf audit P4-4). One 64 KiB chunk is ~87 KB of base64 JSON, so this caps the backlog near 1.4 MB
 * instead of letting a whole file pile up in the queue.
 */
internal const val LEGACY_OUTBOUND_HIGH_WATER = 16

/** How often a waiting legacy loop re-reads the queue depth. */
internal const val LEGACY_OUTBOUND_POLL_MS = 5L

/**
 * Suspends until the native outbound queue has fewer than [highWater] messages waiting (perf audit
 * P4-4).
 *
 * The legacy v2 base64 loops (`FileTransferViewModel.legacyUpload` and the v2 download serve in
 * [AndroidFileTransferHost]) used to read and enqueue an entire file as fast as storage allowed, into
 * a queue that is DELIBERATELY UNBOUNDED (P0-12: dropping on full would punch holes in the transfer,
 * and DesktopInput / MediaSeek / artwork share the queue). So the queue stays as it is and the
 * producers pace themselves before each chunk.
 *
 * No deadline, on purpose: the native send loop always drains (every send is bounded, and on a dead
 * socket the queued messages are discarded at once), so this cannot wait on a queue that never
 * empties, and a timeout that gave up would just go back to flooding it. Cancellable, so a user
 * cancel still unwinds the loop.
 *
 * [depth] defaults to the native queue; tests pass their own so [RemexCoreClient] (and its native
 * library load) is never touched.
 */
internal suspend fun awaitLegacyOutboundRoom(
    depth: () -> Int = RemexCoreClient::outboundQueueDepth,
    highWater: Int = LEGACY_OUTBOUND_HIGH_WATER,
    pollMs: Long = LEGACY_OUTBOUND_POLL_MS,
) {
    while (depth() >= highWater) delay(pollMs)
}
