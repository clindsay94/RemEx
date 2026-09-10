package com.clindsay94.remex.data

import org.json.JSONObject

/**
 * What the PC reports it is playing, as the phone understands it (RemEx-xx6xf).
 *
 * The wire form is `Remex.Core.Models.MediaPlaybackState`, pushed as `media_state` and delivered to
 * [com.clindsay94.remex.RemexCoreClient.RemexCallback.onMediaState].
 */
data class MediaPlaybackSnapshot(
        val status: MediaPlaybackStatus = MediaPlaybackStatus.UNKNOWN,
        val title: String? = null,
        val artist: String? = null,
        /** First 16 lowercase hex chars of the SHA-256 of the artwork bytes; null draws a glyph. */
        val artworkId: String? = null,
        /** Null or 0 means live/unknown; never used to clamp when absent. */
        val durationMs: Long? = null,
        /**
         * The PC's reading at [receivedAtElapsedMs], never a live position. Read [positionAt] instead
         * of this directly — it is the phone-clock-only extrapolation, and comparing this against
         * anything but [receivedAtElapsedMs] would mix a host timestamp with the phone clock.
         */
        val positionMs: Long? = null,
        /** [android.os.SystemClock.elapsedRealtime] when this snapshot arrived. */
        val receivedAtElapsedMs: Long = 0L,
        /**
         * Whether the PC's session will accept a position change at all.
         *
         * THE ANSWER TO A SLIDER THAT JUMPED AND BOUNCED BACK. Some sessions — Apple Music through
         * SMTC is the one that was caught — accept a seek, report success and never move, so the
         * phone's optimistic guess was reverted 2.5 s later every single time. This says so up
         * front, and the sheet disables the slider instead.
         *
         * DEFAULTS FALSE, AND ABSENT PARSES TO FALSE, so an older host that never sends it lands on
         * the conservative side: no drag offered rather than a drag the PC will swallow. Declared
         * LAST so every existing positional caller keeps compiling.
         */
        val canSeek: Boolean = false
) {
    /**
     * [positionMs] projected forward to [nowElapsedMs], entirely on the phone's own monotonic clock —
     * never compared against a host timestamp (Spec 1.3 step 3). Frozen while paused; extrapolated
     * only while [status] is [MediaPlaybackStatus.PLAYING], and clamped to `[0, durationMs]` once
     * [durationMs] is known and positive.
     */
    fun positionAt(nowElapsedMs: Long): Long? {
        val base = positionMs ?: return null
        val projected =
                if (status == MediaPlaybackStatus.PLAYING) {
                    base + (nowElapsedMs - receivedAtElapsedMs)
                } else {
                    base
                }
        val duration = durationMs
        return if (duration != null && duration > 0) projected.coerceIn(0L, duration) else projected
    }

    /** True only when there is both a positive duration and a known position to place on it. */
    val hasTimeline: Boolean
        get() = durationMs != null && durationMs > 0 && positionMs != null

    /** [positionAt] as a fraction of [durationMs], coerced to `0f..1f`; null when [hasTimeline] is false. */
    fun progressAt(nowElapsedMs: Long): Float? {
        if (!hasTimeline) return null
        val position = positionAt(nowElapsedMs) ?: return null
        return (position.toFloat() / durationMs!!.toFloat()).coerceIn(0f, 1f)
    }

    companion object {
        /** The state before the PC has said anything, and the state after a disconnect. */
        val Unknown = MediaPlaybackSnapshot()

        /**
         * Parses one `media_state` payload, degrading to [Unknown] rather than throwing.
         *
         * THIS RUNS ON A JNI CALLBACK, so an exception escaping it takes down the delivery thread for
         * every other message type as well. A media reading is the least important thing arriving on
         * that path and must never be the reason something else stops working — which is why the
         * failure mode is the neutral state rather than a crash or a retry.
         *
         * [receivedAtElapsedMs] defaults so existing single-argument callers keep compiling; it should
         * always be [android.os.SystemClock.elapsedRealtime] at the moment this is called.
         */
        fun parse(json: String, receivedAtElapsedMs: Long = 0L): MediaPlaybackSnapshot =
                runCatching {
                            val obj = JSONObject(json)
                            MediaPlaybackSnapshot(
                                    status = MediaPlaybackStatus.fromToken(obj.optString("status")),
                                    title = obj.optString("title").ifBlank { null },
                                    artist = obj.optString("artist").ifBlank { null },
                                    artworkId = obj.optString("artworkId").ifBlank { null },
                                    durationMs =
                                            if (obj.has("durationMs") && !obj.isNull("durationMs")) {
                                                obj.optLong("durationMs")
                                            } else {
                                                null
                                            },
                                    positionMs =
                                            if (obj.has("positionMs") && !obj.isNull("positionMs")) {
                                                obj.optLong("positionMs")
                                            } else {
                                                null
                                            },
                                    receivedAtElapsedMs = receivedAtElapsedMs,
                                    canSeek = obj.optBoolean("canSeek", false)
                            )
                        }
                        .getOrDefault(Unknown)
    }
}

/**
 * The playback states the host can report.
 *
 * The tokens are the wire contract; see `Remex.Core.Models.MediaPlaybackStatus`. An unrecognised one
 * becomes [UNKNOWN], which is the whole reason the wire form is a string rather than an ordinal — a
 * host that learns a new state must degrade an older phone to "say nothing", not to whichever value
 * happened to be first.
 */
enum class MediaPlaybackStatus {
    PLAYING,
    PAUSED,
    STOPPED,

    /** The PC answered, and nothing is playing. */
    NONE,

    /**
     * No answer. The PC cannot report, has not reported yet, or said something this build does not
     * recognise.
     *
     * NOT INTERCHANGEABLE WITH [NONE]. That one is a reading; this is the absence of one, and the UI
     * owes the user a different face for it — the neutral triangle it has always drawn rather than a
     * claim about their machine.
     */
    UNKNOWN;

    companion object {
        fun fromToken(token: String?): MediaPlaybackStatus =
                when (token) {
                    "playing" -> PLAYING
                    "paused" -> PAUSED
                    "stopped" -> STOPPED
                    "none" -> NONE
                    else -> UNKNOWN
                }
    }
}
