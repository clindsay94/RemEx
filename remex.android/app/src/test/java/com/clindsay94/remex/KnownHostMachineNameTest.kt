package com.clindsay94.remex

import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.KnownHostRecord
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.ui.splash.HandshakePeer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the PC's reported machine name on the phone (RemEx-odqj5): stored per identity, read from
 * `host_info`, and used as the label for a known PC with no nickname instead of its IP address.
 *
 * The order is the contract: nickname, then machine name, then address. A nickname is the user's own
 * choice and must never lose to an observed value; an address is what this bead exists to stop
 * showing when anything better is known.
 */
class KnownHostMachineNameTest {

    private val pin = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="
    private val identity = HostIdentity.keyFor(pin)!!

    // ── Label order ──────────────────────────────────────────────────────────

    @Test
    fun `a nickname wins over the machine name and the address`() {
        assertEquals("Studio", KnownHosts.displayName("Studio", "CONNOR-DESKTOP", "192.168.1.50"))
    }

    @Test
    fun `with no nickname the machine name wins over the address`() {
        assertEquals("CONNOR-DESKTOP", KnownHosts.displayName("", "CONNOR-DESKTOP", "192.168.1.50"))
    }

    @Test
    fun `with neither the address is the last resort`() {
        assertEquals("192.168.1.50", KnownHosts.displayName("", "", "192.168.1.50"))
    }

    @Test
    fun `whitespace counts as absent at every step`() {
        assertEquals("CONNOR-DESKTOP", KnownHosts.displayName("   ", " CONNOR-DESKTOP ", "192.168.1.50"))
        assertEquals("192.168.1.50", KnownHosts.displayName("  ", "  ", "192.168.1.50"))
    }

    @Test
    fun `a known PC row uses the same order`() {
        val host = KnownHost(identity, "", listOf("192.168.1.50"), 5005, 0L, machineName = "CONNOR-DESKTOP")
        assertEquals("CONNOR-DESKTOP", host.displayName)
        assertEquals("Studio", host.copy(nickname = "Studio").displayName)
        assertEquals("192.168.1.50", host.copy(machineName = "").displayName)
    }

    @Test
    fun `a Known PCs entry names the row by machine name and keeps the address on its own line`() {
        val host = KnownHost(identity, "", listOf("192.168.1.50"), 5005, 0L, machineName = "CONNOR-DESKTOP")
        val entry = KnownPcEntry("192.168.1.50", 5005, "", 0L, host, isTrusted = true)

        assertEquals("CONNOR-DESKTOP", entry.displayName)
        assertTrue(entry.isNamed)

        val unnamed = entry.copy(knownHost = host.copy(machineName = ""))
        assertEquals("192.168.1.50", unnamed.displayName)
        assertFalse(unnamed.isNamed)
    }

    @Test
    fun `the splash puts the machine name ahead of a paired hostname or IP`() {
        assertEquals(
            "CONNOR-DESKTOP",
            HandshakePeer.displayNameFor("", listOf("desk-rig.local", "192.168.1.50"), "CONNOR-DESKTOP")
        )
        assertEquals("Studio", HandshakePeer.displayNameFor("Studio", listOf("192.168.1.50"), "CONNOR-DESKTOP"))
        // Unchanged for a PC that never reported one.
        assertEquals("desk-rig", HandshakePeer.displayNameFor("", listOf("desk-rig.local", "192.168.1.50")))
    }

    // ── Storage ──────────────────────────────────────────────────────────────

    @Test
    fun `the machine name key reassembles into the record`() {
        val records = KnownHosts.parseRecords(
            mapOf(
                KnownHosts.nicknameKeyName(identity) to "Studio",
                KnownHosts.machineNameKeyName(identity) to " CONNOR-DESKTOP "
            )
        )

        assertEquals(KnownHostRecord(nickname = "Studio", machineName = "CONNOR-DESKTOP"), records[identity])
    }

    @Test
    fun `build carries the machine name onto the known host`() {
        val hosts = KnownHosts.build(
            mapOf("192.168.1.50" to pin),
            mapOf(identity to KnownHostRecord(machineName = "CONNOR-DESKTOP"))
        )

        assertEquals("CONNOR-DESKTOP", hosts.single().machineName)
        assertEquals("CONNOR-DESKTOP", hosts.single().displayName)
    }

    @Test
    fun `a record written before the field existed still builds and falls back to the address`() {
        val records = KnownHosts.parseRecords(
            mapOf(
                KnownHosts.lastAddressKeyName(identity) to "192.168.1.50",
                KnownHosts.lastPortKeyName(identity) to 5005,
                KnownHosts.lastConnectedKeyName(identity) to 1_700_000_000_000L
            )
        )
        val host = KnownHosts.build(mapOf("192.168.1.50" to pin), records).single()

        assertEquals("", host.machineName)
        assertEquals("192.168.1.50", host.displayName)
    }

    @Test
    fun `a machine name of the wrong type is ignored rather than invented`() {
        val records = KnownHosts.parseRecords(mapOf(KnownHosts.machineNameKeyName(identity) to 42))
        assertTrue(records.isEmpty())
    }

    // ── host_info parse ──────────────────────────────────────────────────────

    @Test
    fun `the machine name is read from host capabilities`() {
        assertEquals(
            "CONNOR-DESKTOP",
            KnownHosts.parseMachineName("""{"version":"1.0","machineName":"CONNOR-DESKTOP"}""")
        )
    }

    @Test
    fun `an older PC that sends no machine name reads as blank`() {
        assertEquals("", KnownHosts.parseMachineName("""{"version":"1.0","macAddress":"AA:BB:CC:DD:EE:FF"}"""))
        assertEquals("", KnownHosts.parseMachineName("""{"machineName":null}"""))
        assertEquals("", KnownHosts.parseMachineName("""{"machineName":42}"""))
    }

    @Test
    fun `a payload that is not JSON never throws`() {
        assertEquals("", KnownHosts.parseMachineName("not json"))
        assertEquals("", KnownHosts.parseMachineName(""))
        assertEquals("", KnownHosts.parseMachineName(null))
    }
}
