package com.clindsay94.remex.data

/**
 * Pure logic behind a phone-initiated seek (RemEx-vtorl): the optimistic snapshot shown the
 * instant the user releases the now-playing sheet's slider, and the decision to revert it if the
 * host never confirms. No Android types, so this is JVM-testable without Robolectric.
 */
object MediaSeekReconciler {

    /**
     * [current] with [positionMs] substituted and stamped at [nowElapsedMs] — exactly like a host
     * arrival (Spec 1.3), never compared against a host timestamp. Every other field is carried
     * over unchanged. Clamped to `[0, durationMs]` only when [current] already has a known
     * timeline; applied as given otherwise, since there is no duration yet to clamp against.
     */
    fun optimistic(
            current: MediaPlaybackSnapshot,
            positionMs: Long,
            nowElapsedMs: Long
    ): MediaPlaybackSnapshot {
        val clamped =
                if (current.hasTimeline) {
                    positionMs.coerceIn(0L, requireNotNull(current.durationMs) {
                        "hasTimeline implies a duration"
                    })
                } else {
                    positionMs
                }
        return current.copy(positionMs = clamped, receivedAtElapsedMs = nowElapsedMs)
    }

    /**
     * True when no host `media_state` has arrived since [seekIssuedAt] — the host ignored the
     * seek (some sessions on Windows do this), and the optimistic snapshot must not be left
     * standing as a claim the phone cannot back.
     */
    fun shouldRevert(seekIssuedAt: Long, lastHostArrivalAt: Long): Boolean =
            lastHostArrivalAt < seekIssuedAt
}
