package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.RemoteDesktopBackgroundGate
import com.clindsay94.remex.ui.screens.RemoteDesktopBackgroundGate.Action
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the decision table behind pausing the Remote Desktop stream while the app is backgrounded
 * (perf audit P0-4).
 *
 * The failure this guards is quiet in both directions. Pause too little and the PC keeps capturing
 * and encoding at full rate for a phone in a pocket. Resume wrongly and either a stream the user
 * stopped comes back on its own, or a paused one never comes back. And [allowsStreamStart] is what
 * keeps the frame watchdog's reconnect (and every other start path) from restarting the stream
 * while backgrounded, which REGRESSION-GUARDS "Frame-arrival watchdog" makes the whole point.
 */
class RemoteDesktopBackgroundGateTest {

    @Test
    fun `starts in the foreground with nothing owed`() {
        val gate = RemoteDesktopBackgroundGate()

        assertTrue(gate.isForeground)
        assertTrue(gate.allowsStreamStart())
        assertFalse(gate.resumeOnForeground)
    }

    @Test
    fun `a foreground report while already foreground does nothing`() {
        val gate = RemoteDesktopBackgroundGate()

        // The first ON_START an observer receives on registration lands here.
        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = true, streamWanted = true))
        assertTrue(gate.allowsStreamStart())
    }

    @Test
    fun `backgrounding an active stream pauses it and blocks every start`() {
        val gate = RemoteDesktopBackgroundGate()

        assertEquals(Action.PAUSE, gate.onForegroundChanged(foreground = false, streamWanted = true))
        assertFalse(gate.allowsStreamStart())
        assertTrue(gate.resumeOnForeground)
    }

    @Test
    fun `returning after a pause resumes exactly once and allows starts again`() {
        val gate = RemoteDesktopBackgroundGate()
        gate.onForegroundChanged(foreground = false, streamWanted = true)

        assertEquals(Action.RESUME, gate.onForegroundChanged(foreground = true, streamWanted = false))
        assertTrue(gate.allowsStreamStart())
        assertFalse(gate.resumeOnForeground)

        // A duplicate foreground report must not restart the stream a second time.
        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = true, streamWanted = true))
    }

    @Test
    fun `backgrounding with no stream wanted neither pauses nor resumes`() {
        val gate = RemoteDesktopBackgroundGate()

        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = false, streamWanted = false))
        // Still blocks starts: nothing should open a stream while backgrounded.
        assertFalse(gate.allowsStreamStart())

        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = true, streamWanted = false))
        assertTrue(gate.allowsStreamStart())
    }

    @Test
    fun `a repeated background report pauses only once`() {
        val gate = RemoteDesktopBackgroundGate()

        assertEquals(Action.PAUSE, gate.onForegroundChanged(foreground = false, streamWanted = true))
        // The pause itself clears isStreaming, so a second report sees no stream; it must not
        // cancel the resume that is already owed.
        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = false, streamWanted = false))
        assertTrue(gate.resumeOnForeground)

        assertEquals(Action.RESUME, gate.onForegroundChanged(foreground = true, streamWanted = false))
    }

    @Test
    fun `a deliberate stop while backgrounded cancels the resume`() {
        val gate = RemoteDesktopBackgroundGate()
        gate.onForegroundChanged(foreground = false, streamWanted = true)

        gate.cancelResume()

        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = true, streamWanted = false))
        assertTrue(gate.allowsStreamStart())
    }

    @Test
    fun `each background cycle is judged on its own`() {
        val gate = RemoteDesktopBackgroundGate()
        gate.onForegroundChanged(foreground = false, streamWanted = true)
        gate.onForegroundChanged(foreground = true, streamWanted = false)

        // Second cycle: the user stopped the stream in between, so nothing is owed this time.
        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = false, streamWanted = false))
        assertEquals(Action.NONE, gate.onForegroundChanged(foreground = true, streamWanted = false))
    }
}
