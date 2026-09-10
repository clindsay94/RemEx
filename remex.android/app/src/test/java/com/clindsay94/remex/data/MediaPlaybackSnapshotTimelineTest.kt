package com.clindsay94.remex.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pins the timeline fields on [MediaPlaybackSnapshot] and their phone-clock-only extrapolation. */
class MediaPlaybackSnapshotTimelineTest {

    @Test
    fun `parse fills the timeline fields when present`() {
        val snapshot =
                MediaPlaybackSnapshot.parse(
                        """{"status":"playing","title":"T","artworkId":"abc123","durationMs":180000,"positionMs":5000}""",
                        receivedAtElapsedMs = 1000L
                )

        assertEquals("abc123", snapshot.artworkId)
        assertEquals(180000L, snapshot.durationMs)
        assertEquals(5000L, snapshot.positionMs)
        assertEquals(1000L, snapshot.receivedAtElapsedMs)
    }

    @Test
    fun `absent timeline fields parse to null rather than zero`() {
        val snapshot = MediaPlaybackSnapshot.parse("""{"status":"playing","title":"T"}""")

        assertNull(snapshot.artworkId)
        assertNull(snapshot.durationMs)
        assertNull(snapshot.positionMs)
    }

    @Test
    fun `canSeek parses true when the host says the session accepts a position change`() {
        val snapshot =
                MediaPlaybackSnapshot.parse(
                        """{"status":"playing","title":"T","durationMs":180000,"canSeek":true}"""
                )

        assertTrue(snapshot.canSeek)
    }

    @Test
    fun `an absent or false canSeek both mean the slider stays disabled`() {
        // THE CONSERVATIVE DEFAULT IS THE WHOLE POINT. A host that predates the field and a session
        // that genuinely cannot seek must land on the same answer, because the alternative is a
        // slider that jumps to the user's finger and snaps back 2.5 s later - the exact behaviour
        // this field was added to stop.
        assertFalse(MediaPlaybackSnapshot.parse("""{"status":"playing","title":"T"}""").canSeek)
        assertFalse(MediaPlaybackSnapshot.parse("""{"status":"playing","canSeek":false}""").canSeek)
        assertFalse(MediaPlaybackSnapshot.Unknown.canSeek)
    }

    @Test
    fun `a blank artworkId parses to null, matching title and artist`() {
        val snapshot = MediaPlaybackSnapshot.parse("""{"status":"playing","artworkId":""}""")

        assertNull(snapshot.artworkId)
    }

    @Test
    fun `a playing snapshot extrapolates position forward on the phone clock`() {
        // Received at 1000 with position 5000; asked for at 4000 -> 5000 + (4000-1000) = 8000.
        val snapshot =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PLAYING,
                        durationMs = 600_000L,
                        positionMs = 5000L,
                        receivedAtElapsedMs = 1000L
                )

        assertEquals(8000L, snapshot.positionAt(nowElapsedMs = 4000L))
    }

    @Test
    fun `a paused snapshot reports the same position regardless of how much later it is asked`() {
        val snapshot =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PAUSED,
                        durationMs = 600_000L,
                        positionMs = 5000L,
                        receivedAtElapsedMs = 1000L
                )

        assertEquals(5000L, snapshot.positionAt(nowElapsedMs = 1000L))
        assertEquals(5000L, snapshot.positionAt(nowElapsedMs = 999_999L))
    }

    @Test
    fun `extrapolation clamps at the duration rather than overrunning it`() {
        val snapshot =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PLAYING,
                        durationMs = 10_000L,
                        positionMs = 9_000L,
                        receivedAtElapsedMs = 0L
                )

        assertEquals(10_000L, snapshot.positionAt(nowElapsedMs = 5_000L))
    }

    @Test
    fun `positionAt is null when there is no position to extrapolate`() {
        val snapshot = MediaPlaybackSnapshot(status = MediaPlaybackStatus.PLAYING)

        assertNull(snapshot.positionAt(nowElapsedMs = 5000L))
    }

    @Test
    fun `hasTimeline requires both a positive duration and a known position`() {
        assertFalse(MediaPlaybackSnapshot(durationMs = null, positionMs = 1000L).hasTimeline)
        assertFalse(MediaPlaybackSnapshot(durationMs = 0L, positionMs = 1000L).hasTimeline)
        assertFalse(MediaPlaybackSnapshot(durationMs = 10_000L, positionMs = null).hasTimeline)
        assertTrue(MediaPlaybackSnapshot(durationMs = 10_000L, positionMs = 1000L).hasTimeline)
    }

    @Test
    fun `progressAt is null without a timeline`() {
        val snapshot =
                MediaPlaybackSnapshot(status = MediaPlaybackStatus.PLAYING, positionMs = 1000L)

        assertNull(snapshot.progressAt(nowElapsedMs = 0L))
    }

    @Test
    fun `progressAt reports position over duration, coerced to 0 to 1`() {
        val atEnd =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PLAYING,
                        durationMs = 10_000L,
                        positionMs = 9_000L,
                        receivedAtElapsedMs = 0L
                )
        assertEquals(1.0f, atEnd.progressAt(nowElapsedMs = 5_000L))

        val quarter =
                MediaPlaybackSnapshot(
                        status = MediaPlaybackStatus.PAUSED,
                        durationMs = 10_000L,
                        positionMs = 2_500L
                )
        assertEquals(0.25f, quarter.progressAt(nowElapsedMs = 0L))
    }
}
