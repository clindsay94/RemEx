package com.clindsay94.remex

import com.clindsay94.remex.data.CertRepairMigration
import com.clindsay94.remex.data.KnownHostRecord
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.security.HostIdentity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
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

    // ── Certificate-change inheritance (RemEx-bye7) ────────────────────────────────────────────

    @Test
    fun `a confirmed repair on the connected host migrates the old row`() {
        // The bead itself. A PC that legitimately changes certificate - a RemEx or Windows reinstall
        // - derives a NEW identity, so it comes back as a new row and the nickname the user chose is
        // stranded on a pin nothing will ever present again.
        val from =
            KnownHosts.identityToMigrateFrom(
                CertRepairMigration(host = "studio.local", oldIdentity = "old-id"),
                connectedHost = "studio.local",
                newIdentity = "new-id"
            )

        assertEquals("old-id", from)
    }

    @Test
    fun `no confirmed repair means no inheritance`() {
        // AN ORDINARY PAIRING MUST NOT INHERIT. This is the whole safety property: without the
        // dialog there is no assertion that the machine is the same one, and a nickname moved on a
        // guess would put someone else's label on the wrong computer.
        assertNull(
            KnownHosts.identityToMigrateFrom(null, connectedHost = "studio.local", newIdentity = "new-id")
        )
    }

    @Test
    fun `a repair confirmed for a different host does not migrate`() {
        // The signal is the dialog AND the host it was raised for. Confirming a repair for one PC
        // and then connecting to another - easy, since the repair drops the user into a pairing flow
        // they can back out of - must not hand the second PC the first one's name.
        assertNull(
            KnownHosts.identityToMigrateFrom(
                CertRepairMigration(host = "studio.local", oldIdentity = "old-id"),
                connectedHost = "laptop.local",
                newIdentity = "new-id"
            )
        )
    }

    @Test
    fun `re-pinning the same certificate is not a migration`() {
        // REACHABLE, NOT DEFENSIVE. The dialog also opens on failures that merely mention SSL, and
        // its Unknown state offers repair when nothing answered - so a user can confirm a repair and
        // then re-pin the identical certificate. Migrating identity onto itself would matter: the
        // move ends by forgetting the source, so it would delete the very row it just wrote.
        assertNull(
            KnownHosts.identityToMigrateFrom(
                CertRepairMigration(host = "studio.local", oldIdentity = "same-id"),
                connectedHost = "studio.local",
                newIdentity = "same-id"
            )
        )
    }

    @Test
    fun `blank identities and hosts are refused`() {
        // HostIdentity.keyFor returns null for an absent pin and the host can be empty on a
        // half-built connection event; neither should reach a store write.
        assertNull(
            KnownHosts.identityToMigrateFrom(
                CertRepairMigration(host = "", oldIdentity = "old-id"),
                connectedHost = "",
                newIdentity = "new-id"
            )
        )
        assertNull(
            KnownHosts.identityToMigrateFrom(
                CertRepairMigration(host = "studio.local", oldIdentity = ""),
                connectedHost = "studio.local",
                newIdentity = "new-id"
            )
        )
        assertNull(
            KnownHosts.identityToMigrateFrom(
                CertRepairMigration(host = "studio.local", oldIdentity = "old-id"),
                connectedHost = "studio.local",
                newIdentity = ""
            )
        )
    }

    @Test
    fun `a connection to another PC does not consume the pending repair`() {
        // THE BUG REVIEW FOUND, and it made the whole feature a silent no-op under an ordinary
        // sequence. Confirming the dialog drops the user into a pairing flow they can back out of,
        // so connecting to a different PC before finishing the re-pair is a normal thing to do. The
        // first version cleared the token on ANY successful connection and only then asked whether
        // to migrate - so the pure predicate declined correctly while the thing it declined on had
        // already been thrown away, and the eventual re-pair inherited nothing.
        val pending = CertRepairMigration(host = "studio.local", oldIdentity = "old-id")

        assertFalse(KnownHosts.isAwaitingRepairOn(pending, connectedHost = "laptop.local"))
        assertTrue(KnownHosts.isAwaitingRepairOn(pending, connectedHost = "studio.local"))
    }

    @Test
    fun `nothing is awaited without a confirmed repair`() {
        assertFalse(KnownHosts.isAwaitingRepairOn(null, connectedHost = "studio.local"))
        assertFalse(
            KnownHosts.isAwaitingRepairOn(
                CertRepairMigration(host = "", oldIdentity = "old-id"),
                connectedHost = ""
            )
        )
    }
}
