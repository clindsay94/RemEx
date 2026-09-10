package com.clindsay94.remex

import com.clindsay94.remex.data.SettingsManager
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * A manually entered MAC only applies to the PC it was entered for (RemEx-263f).
 *
 * The MAC, broadcast address and subnet are stored once, globally, while tap-to-connect on a Known
 * PC changes only the host. So a MAC typed for PC A was still being broadcast after switching to
 * PC B — waking the wrong machine, and doing it silently: nothing on the phone looks wrong, and the
 * machine that woke is not the one the user is looking at.
 */
class WakeOnLanMacScopeTest {

    private val a = "AA:AA:AA:AA:AA:AA"
    private val b = "BB:BB:BB:BB:BB:BB"

    @Test
    fun `a manual mac is used for the pc it was entered for`() {
        assertEquals(
            a,
            SettingsManager.resolveMacAddress(
                manual = a, manualHost = "pc-a", hostReported = b, currentHost = "pc-a"))
    }

    @Test
    fun `a manual mac is ignored after switching to a different pc`() {
        // THE BUG. Before this, the answer here was PC A's MAC while every other setting pointed at
        // PC B - a magic packet aimed at a machine the user was not looking at.
        assertEquals(
            b,
            SettingsManager.resolveMacAddress(
                manual = a, manualHost = "pc-a", hostReported = b, currentHost = "pc-b"))
    }

    @Test
    fun `a manual mac with no recorded host is still preferred`() {
        // Upgrade path, and deliberately NOT treated as a mismatch: this is everyone's data before
        // RemEx-263f, and discarding it would throw away a MAC someone set on purpose for a different
        // NIC or a router quirk - which is the entire reason the manual slot exists. These installs
        // heal on the next settings save, because the host is recorded in the same edit.
        assertEquals(
            a,
            SettingsManager.resolveMacAddress(
                manual = a, manualHost = "", hostReported = b, currentHost = "pc-b"))
    }

    @Test
    fun `the host-reported mac is used when there is no manual one`() {
        // The ordinary case, and the floor for the rest: without it, a resolver that returned the
        // manual value unconditionally would satisfy nothing above but still break every user who
        // has never opened the MAC field.
        assertEquals(
            b,
            SettingsManager.resolveMacAddress(
                manual = "", manualHost = "", hostReported = b, currentHost = "pc-b"))
    }

    @Test
    fun `a mismatched manual mac with no host-reported mac yields nothing rather than the wrong one`() {
        // Wake-on-LAN then does nothing, which is the right outcome: the alternative is broadcasting
        // for a machine the user is not looking at, and a silent no-op is recoverable where a silent
        // wrong wake is not.
        assertEquals(
            "",
            SettingsManager.resolveMacAddress(
                manual = a, manualHost = "pc-a", hostReported = "", currentHost = "pc-b"))
    }
}
