package com.clindsay94.remex

import com.clindsay94.remex.TelemetryBackgroundGate.Action
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Covers when the phone tells the PC to pause and resume its telemetry push (perf audit P0-5).
 *
 * Both directions fail silently. Pause too little and the PC keeps sending a 60-100 KB envelope a
 * second to a phone in a pocket. Resume wrongly and the dashboard sits on stale numbers with a
 * healthy connection. And because the host forgets a pause with its socket, a reconnect while
 * backgrounded must pause again - the case a plain boolean would get wrong.
 */
class TelemetryBackgroundGateTest {

    private val pcA = EstablishedConnection("192.168.1.10", 5005, epoch = 1)
    private val pcAReconnected = EstablishedConnection("192.168.1.10", 5005, epoch = 2)

    @Test
    fun `nothing is sent while there is no authenticated connection`() {
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.NONE, gate.reconcile(foreground = false, connection = null, widgetPlaced = false))
        assertEquals(Action.NONE, gate.reconcile(foreground = true, connection = null, widgetPlaced = false))
    }

    @Test
    fun `a foreground connection is left streaming`() {
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.NONE, gate.reconcile(foreground = true, connection = pcA, widgetPlaced = false))
    }

    @Test
    fun `backgrounding pauses once and foregrounding resumes once`() {
        val gate = TelemetryBackgroundGate()
        gate.reconcile(foreground = true, connection = pcA, widgetPlaced = false)

        assertEquals(Action.PAUSE, gate.reconcile(foreground = false, connection = pcA, widgetPlaced = false))
        assertEquals(Action.NONE, gate.reconcile(foreground = false, connection = pcA, widgetPlaced = false))

        assertEquals(Action.RESUME, gate.reconcile(foreground = true, connection = pcA, widgetPlaced = false))
        assertEquals(Action.NONE, gate.reconcile(foreground = true, connection = pcA, widgetPlaced = false))
    }

    @Test
    fun `a connection authenticated while backgrounded is paused`() {
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.PAUSE, gate.reconcile(foreground = false, connection = pcA, widgetPlaced = false))
    }

    @Test
    fun `a reconnect while backgrounded pauses the new socket again`() {
        // THE CASE A BOOLEAN GETS WRONG. The host's pause died with the old socket; the new one
        // streams until told otherwise, even with no null observed in between (StateFlow conflation).
        val gate = TelemetryBackgroundGate()
        gate.reconcile(foreground = false, connection = pcA, widgetPlaced = false)

        assertEquals(
                Action.PAUSE,
                gate.reconcile(foreground = false, connection = pcAReconnected, widgetPlaced = false)
        )
    }

    @Test
    fun `a reconnect in the foreground after a background pause needs no resume`() {
        // The new socket already streams; a resume would be a no-op message.
        val gate = TelemetryBackgroundGate()
        gate.reconcile(foreground = false, connection = pcA, widgetPlaced = false)
        gate.reconcile(foreground = false, connection = null, widgetPlaced = false)

        assertEquals(Action.NONE, gate.reconcile(foreground = true, connection = pcAReconnected, widgetPlaced = false))
    }

    @Test
    fun `a placed hardware widget keeps the stream running in the background`() {
        // The widget renders from this stream while the app is backgrounded; pausing freezes it.
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.NONE, gate.reconcile(foreground = false, connection = pcA, widgetPlaced = true))
        assertEquals(Action.NONE, gate.reconcile(foreground = true, connection = pcA, widgetPlaced = true))
    }

    // ── Leanness K8: the "telemetry wanted" lease ──

    @Test
    fun `open with nothing on screen reading telemetry pauses, and a reader resumes it`() {
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.PAUSE, gate.reconcile(true, pcA, widgetPlaced = false, telemetryWanted = false))
        assertEquals(Action.NONE, gate.reconcile(true, pcA, widgetPlaced = false, telemetryWanted = false))
        assertEquals(Action.RESUME, gate.reconcile(true, pcA, widgetPlaced = false, telemetryWanted = true))
        assertEquals(Action.NONE, gate.reconcile(true, pcA, widgetPlaced = false, telemetryWanted = true))
    }

    @Test
    fun `a lease left held in the background does not keep a pocketed phone streaming`() {
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.PAUSE, gate.reconcile(false, pcA, widgetPlaced = false, telemetryWanted = true))
    }

    @Test
    fun `a placed widget streams whether or not anything on screen reads it`() {
        val gate = TelemetryBackgroundGate()

        assertEquals(Action.NONE, gate.reconcile(true, pcA, widgetPlaced = true, telemetryWanted = false))
        assertEquals(Action.NONE, gate.reconcile(false, pcA, widgetPlaced = true, telemetryWanted = false))
    }

    @Test
    fun `the full truth table streams only for a widget or an open app with a reader`() {
        for (foreground in listOf(false, true)) for (widget in listOf(false, true)) for (wanted in listOf(false, true)) {
            val gate = TelemetryBackgroundGate()
            val action = gate.reconcile(foreground, pcA, widgetPlaced = widget, telemetryWanted = wanted)
            val shouldStream = widget || (foreground && wanted)
            // A fresh socket streams, so streaming needs nothing sent and pausing needs one PAUSE.
            assertEquals(
                    "fg=$foreground widget=$widget wanted=$wanted",
                    if (shouldStream) Action.NONE else Action.PAUSE,
                    action,
            )
        }
    }

    @Test
    fun `leases are counted by key, so a doubled release cannot pause a screen that still reads`() {
        val leases = TelemetryLeases()
        assertEquals(false, leases.wanted.value)

        leases.acquire(TelemetryDemand.SENSORS_CANVAS)
        leases.acquire(TelemetryDemand.HOME_PIN_SHEET)
        assertEquals(true, leases.wanted.value)

        leases.release(TelemetryDemand.HOME_PIN_SHEET)
        leases.release(TelemetryDemand.HOME_PIN_SHEET)
        assertEquals(true, leases.wanted.value)
        assertEquals(setOf(TelemetryDemand.SENSORS_CANVAS), leases.holders)

        leases.release(TelemetryDemand.SENSORS_CANVAS)
        assertEquals(false, leases.wanted.value)
    }

    @Test
    fun `acquiring the same lease twice needs only one release`() {
        val leases = TelemetryLeases()
        leases.acquire(TelemetryDemand.ROUTINE_EDITOR)
        leases.acquire(TelemetryDemand.ROUTINE_EDITOR)
        leases.release(TelemetryDemand.ROUTINE_EDITOR)
        assertEquals(false, leases.wanted.value)
    }
}
