package com.clindsay94.remex.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pins [MediaSeekReconciler]'s optimistic-copy and revert-decision logic (RemEx-vtorl). */
class MediaSeekReconcilerTest {

    @Test
    fun `optimistic keeps every other field and substitutes position and arrival time`() {
        val current =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PLAYING,
                        title = "Sound of Silence",
                        artist = "Simon & Garfunkel",
                        artworkId = "abc123",
                        durationMs = 180_000L,
                        positionMs = 5_000L,
                        receivedAtElapsedMs = 1_000L
                )

        val result = MediaSeekReconciler.optimistic(current, positionMs = 60_000L, nowElapsedMs = 9_000L)

        assertEquals(MediaPlaybackStatus.PLAYING, result.status)
        assertEquals("Sound of Silence", result.title)
        assertEquals("Simon & Garfunkel", result.artist)
        assertEquals("abc123", result.artworkId)
        assertEquals(180_000L, result.durationMs)
        assertEquals(60_000L, result.positionMs)
        assertEquals(9_000L, result.receivedAtElapsedMs)
    }

    @Test
    fun `optimistic clamps to the duration when the current snapshot has a timeline`() {
        val current =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PAUSED,
                        durationMs = 100_000L,
                        positionMs = 5_000L
                )

        val overshoot = MediaSeekReconciler.optimistic(current, positionMs = 999_999L, nowElapsedMs = 0L)
        assertEquals(100_000L, overshoot.positionMs)

        val undershoot = MediaSeekReconciler.optimistic(current, positionMs = -50L, nowElapsedMs = 0L)
        assertEquals(0L, undershoot.positionMs)
    }

    @Test
    fun `optimistic applies the position unchanged when there is no timeline yet`() {
        val current = MediaPlaybackSnapshot(status = MediaPlaybackStatus.PLAYING)

        val result = MediaSeekReconciler.optimistic(current, positionMs = 999_999L, nowElapsedMs = 0L)

        assertEquals(999_999L, result.positionMs)
    }

    @Test
    fun `shouldRevert is true when no host arrival followed the seek`() {
        assertTrue(MediaSeekReconciler.shouldRevert(seekIssuedAt = 100L, lastHostArrivalAt = 50L))
    }

    @Test
    fun `shouldRevert is false when a host arrival followed the seek`() {
        assertFalse(MediaSeekReconciler.shouldRevert(seekIssuedAt = 100L, lastHostArrivalAt = 150L))
    }

    @Test
    fun `shouldRevert is false when the arrival is exactly at the seek instant`() {
        assertFalse(MediaSeekReconciler.shouldRevert(seekIssuedAt = 100L, lastHostArrivalAt = 100L))
    }
}
