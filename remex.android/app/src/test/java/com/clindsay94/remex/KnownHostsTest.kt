package com.clindsay94.remex

import com.clindsay94.remex.data.KnownHostRecord
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.security.HostIdentity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the Known PCs list: grouping, ordering, and the preference-key scheme (RemEx-k62t).
 *
 * The failures these prevent are all the same shape - a list that LOOKS right. One machine shown
 * three times because the rows were keyed by address; a "last connected" order that reshuffles
 * under the user's finger; a row that dials an address the user has already unpaired.
 */
class KnownHostsTest {

    private val pinA = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="
    private val pinB = "Zx9Lm2QwErTyUiOpAsDfGhJkLzXcVbNm1234567890A="

    private val identityA = HostIdentity.keyFor(pinA)!!
    private val identityB = HostIdentity.keyFor(pinB)!!

    @Test
    fun `one PC at three addresses is one row`() {
        val paired = mapOf(
            "192.168.1.50" to pinA,
            "100.72.10.4" to pinA,
            "desktop-pc.local" to pinA
        )

        val hosts = KnownHosts.build(paired, emptyMap())

        assertEquals(1, hosts.size)
        assertEquals(
            listOf("192.168.1.50", "100.72.10.4", "desktop-pc.local"),
            hosts.single().addresses
        )
    }

    @Test
    fun `a host with no usable pin gets no row`() {
        // Not a placeholder row. A placeholder would collapse every unidentifiable host into one
        // entry claiming to be several machines - which looks correct and is not.
        val hosts = KnownHosts.build(mapOf("192.168.1.50" to "  ", "10.0.0.2" to pinA), emptyMap())

        assertEquals(listOf(identityA), hosts.map { it.identity })
    }

    @Test
    fun `most recently connected comes first`() {
        val paired = mapOf("192.168.1.50" to pinA, "192.168.1.51" to pinB)
        val records = mapOf(
            identityA to KnownHostRecord(lastConnectedAtMillis = 1_000L),
            identityB to KnownHostRecord(lastConnectedAtMillis = 9_000L)
        )

        assertEquals(listOf(identityB, identityA), KnownHosts.build(paired, records).map { it.identity })
    }

    @Test
    fun `PCs that have never connected keep their pairing order behind the rest`() {
        // A tie broken by anything unstable - hash order, map iteration order - would reshuffle
        // rows between recompositions and land a tap on a different PC than the one aimed at.
        val paired = mapOf("192.168.1.50" to pinA, "192.168.1.51" to pinB)

        val hosts = KnownHosts.build(paired, emptyMap())

        assertEquals(listOf(identityA, identityB), hosts.map { it.identity })
    }

    @Test
    fun `the address that last worked leads the row`() {
        val paired = mapOf(
            "192.168.1.50" to pinA,
            "100.72.10.4" to pinA,
            "desktop-pc.local" to pinA
        )
        val records = mapOf(identityA to KnownHostRecord(lastAddress = "100.72.10.4"))

        val host = KnownHosts.build(paired, records).single()

        assertEquals("100.72.10.4", host.preferredAddress)
        assertEquals(
            listOf("100.72.10.4", "192.168.1.50", "desktop-pc.local"),
            host.addresses
        )
    }

    @Test
    fun `an unpaired last address is not resurrected`() {
        // The user unpaired that one address. Dialling it anyway would be the row remembering
        // something the user deliberately removed.
        val paired = mapOf("192.168.1.50" to pinA)
        val records = mapOf(identityA to KnownHostRecord(lastAddress = "100.72.10.4"))

        assertEquals("192.168.1.50", KnownHosts.build(paired, records).single().preferredAddress)
    }

    @Test
    fun `port falls back to the default when never recorded or out of range`() {
        val paired = mapOf("192.168.1.50" to pinA)

        assertEquals(
            KnownHosts.DefaultPort,
            KnownHosts.build(paired, emptyMap()).single().port
        )
        assertEquals(
            KnownHosts.DefaultPort,
            KnownHosts.build(paired, mapOf(identityA to KnownHostRecord(lastPort = 0))).single().port
        )
        assertEquals(
            KnownHosts.DefaultPort,
            KnownHosts.build(paired, mapOf(identityA to KnownHostRecord(lastPort = 70_000)))
                .single()
                .port
        )
        assertEquals(
            5100,
            KnownHosts.build(paired, mapOf(identityA to KnownHostRecord(lastPort = 5100)))
                .single()
                .port
        )
    }

    @Test
    fun `a paired PC that has never connected is not the default target`() {
        // "Last used" has to mean used. Pre-filling the form with a machine the user has never
        // reached looks like a remembered choice while being a guess.
        val paired = mapOf("192.168.1.50" to pinA)

        assertNull(KnownHosts.mostRecentlyConnected(KnownHosts.build(paired, emptyMap())))
    }

    @Test
    fun `the default target is the most recently connected PC`() {
        val paired = mapOf("192.168.1.50" to pinA, "192.168.1.51" to pinB)
        val records = mapOf(
            identityA to KnownHostRecord(lastConnectedAtMillis = 9_000L),
            identityB to KnownHostRecord(lastConnectedAtMillis = 1_000L)
        )

        val target = KnownHosts.mostRecentlyConnected(KnownHosts.build(paired, records))

        assertEquals(identityA, target?.identity)
    }

    @Test
    fun `the four preference keys of one PC reassemble into one record`() {
        val preferences = mapOf(
            KnownHosts.nicknameKeyName(identityA) to "  Studio PC  ",
            KnownHosts.lastAddressKeyName(identityA) to "100.72.10.4",
            KnownHosts.lastPortKeyName(identityA) to 5100,
            KnownHosts.lastConnectedKeyName(identityA) to 1_700_000_000_000L
        )

        assertEquals(
            mapOf(
                identityA to KnownHostRecord(
                    nickname = "Studio PC",
                    lastAddress = "100.72.10.4",
                    lastPort = 5100,
                    lastConnectedAtMillis = 1_700_000_000_000L
                )
            ),
            KnownHosts.parseRecords(preferences)
        )
    }

    @Test
    fun `foreign keys and wrong-typed values cannot invent a PC`() {
        // A preference file written by an older or newer build must not be able to conjure a row.
        val preferences = mapOf(
            "host" to "192.168.1.50",
            "fileTrust_abc_fullBrowse" to true,
            "${KnownHosts.KeyPrefix}$identityA" to "no field suffix",
            "${KnownHosts.KeyPrefix}${identityA}_" to "empty field",
            KnownHosts.lastPortKeyName(identityB) to "5100",
            "${KnownHosts.KeyPrefix}${identityB}_favouriteColour" to "blue"
        )

        assertTrue(KnownHosts.parseRecords(preferences).isEmpty())
    }

    @Test
    fun `a nickname alone is enough to make a record`() {
        // Naming a PC and never connecting to it again still has to survive a restart.
        val records = KnownHosts.parseRecords(
            mapOf(KnownHosts.nicknameKeyName(identityA) to "Studio PC")
        )

        assertEquals(KnownHostRecord(nickname = "Studio PC"), records[identityA])
    }

    @Test
    fun `records for a PC that is no longer paired produce no row`() {
        // Unpairing removes the pins; a leftover nickname must not keep a dead row on screen.
        val hosts = KnownHosts.build(
            emptyMap(),
            mapOf(identityA to KnownHostRecord(nickname = "Studio PC"))
        )

        assertTrue(hosts.isEmpty())
    }
}
