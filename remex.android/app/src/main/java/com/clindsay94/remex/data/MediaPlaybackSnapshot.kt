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
        val artist: String? = null
) {
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
         */
        fun parse(json: String): MediaPlaybackSnapshot =
                runCatching {
                            val obj = JSONObject(json)
                            MediaPlaybackSnapshot(
                                    status = MediaPlaybackStatus.fromToken(obj.optString("status")),
                                    title = obj.optString("title").ifBlank { null },
                                    artist = obj.optString("artist").ifBlank { null }
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
