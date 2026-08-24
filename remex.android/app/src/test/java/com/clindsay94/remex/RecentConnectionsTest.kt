package com.clindsay94.remex

import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.RecentConnection
import com.clindsay94.remex.data.RecentConnections
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the recent-address list behind the Known PCs card (RemEx-obxlo).
 *
 * The failures these prevent are all the shape the card was built to fix, or the one it could
 * introduce going the other way: an address the user connected to that never appears; a PC on a
 * churning DHCP lease that fills every slot and buries the others; a row that claims a trust which
 * lapsed; a paired machine with no row at all, and therefore no reachable unpair.
 */
class RecentConnectionsTest {

    private fun entry(
        address: String,
        millis: Long,
        identity: String = "",
        port: Int = 5005
    ) = RecentConnection(address, port, identity, millis)

    private fun host(
        identity: String,
        addresses: List<String>,
        nickname: String = "",
        millis: Long = 0L,
        port: Int = 5005
    ) = KnownHost(identity, nickname, addresses, port, millis)

    // ── record ───────────────────────────────────────────────────────────────

    @Test
    fun `a new address goes to the front`() {
        val existing = listOf(entry("10.0.0.1", 100L))

        val updated = RecentConnections.record(existing, "10.0.0.2", 5005, "aa", 200L)

        assertEquals(listOf("10.0.0.2", "10.0.0.1"), updated.map { it.address })
    }

    @Test
    fun `re-connecting to a listed address moves it rather than duplicating it`() {
        val existing = listOf(entry("10.0.0.1", 100L), entry("10.0.0.2", 90L))

        val updated = RecentConnections.record(existing, "10.0.0.2", 5005, "aa", 300L)

        assertEquals(listOf("10.0.0.2", "10.0.0.1"), updated.map { it.address })
        assertEquals(300L, updated.first().lastConnectedAtMillis)
    }

    @Test
    fun `a re-pair under the same address refreshes the identity`() {
        // A certificate change is precisely when a machine's identity moves, and the address is the
        // one thing that did not. Keeping the old identity would leave the row grouped with a PC
        // that no longer exists — and counted against its cap.
        val existing = listOf(entry("10.0.0.1", 100L, identity = "old"))

        val updated = RecentConnections.record(existing, "10.0.0.1", 5005, "new", 200L)

        assertEquals(1, updated.size)
        assertEquals("new", updated.first().identity)
    }

    @Test
    fun `a blank address is not recorded`() {
        val existing = listOf(entry("10.0.0.1", 100L))

        assertSame(existing, RecentConnections.record(existing, "   ", 5005, "aa", 200L))
    }

    @Test
    fun `an out-of-range port is not recorded`() {
        // Rather than stored and rendered as a row that dials port 0 — a row that cannot work is
        // worse than one that is absent, because the user retries it.
        val existing = listOf(entry("10.0.0.1", 100L))

        assertSame(existing, RecentConnections.record(existing, "10.0.0.9", 0, "aa", 200L))
        assertSame(existing, RecentConnections.record(existing, "10.0.0.9", 70000, "aa", 200L))
    }

    // ── caps ─────────────────────────────────────────────────────────────────

    @Test
    fun `the list never grows past the maximum`() {
        var list = emptyList<RecentConnection>()
        for (i in 1..12) {
            list = RecentConnections.record(list, "10.0.0.$i", 5005, "pc$i", i.toLong())
        }

        assertEquals(RecentConnections.MaxEntries, list.size)
        assertEquals("10.0.0.12", list.first().address)
    }

    @Test
    fun `one machine cannot occupy more than its share of the list`() {
        // THE REGRESSION THIS EXISTS FOR. Address-keyed rows fix a PC whose lease moved being shown
        // once; without a per-machine cap the same PC on a churning lease fills all five slots and
        // hides every other machine, which is the failure the certificate-keyed grouping originally
        // prevented. Both lists would then be wrong, in opposite directions.
        var list = emptyList<RecentConnection>()
        for (i in 1..5) {
            list = RecentConnections.record(list, "10.0.0.$i", 5005, "same-pc", i.toLong())
        }

        assertEquals(RecentConnections.MaxPerMachine, list.size)
        assertEquals(listOf("10.0.0.5", "10.0.0.4"), list.map { it.address })
    }

    @Test
    fun `a capped machine does not crowd out another machine`() {
        var list = emptyList<RecentConnection>()
        for (i in 1..4) {
            list = RecentConnections.record(list, "10.0.0.$i", 5005, "noisy", i.toLong())
        }
        list = RecentConnections.record(list, "10.0.0.99", 5005, "quiet", 99L)

        assertTrue(list.any { it.identity == "quiet" })
        assertEquals(2, list.count { it.identity == "noisy" })
    }

    @Test
    fun `addresses with no identity are not pooled into one machine`() {
        // They are not KNOWN to be the same PC. Treating them as one would hide addresses on a
        // guess, and the guess is wrong exactly when the user has several unpaired PCs.
        var list = emptyList<RecentConnection>()
        for (i in 1..4) {
            list = RecentConnections.record(list, "10.0.0.$i", 5005, "", i.toLong())
        }

        assertEquals(4, list.size)
    }

    // ── encode / parse ───────────────────────────────────────────────────────

    @Test
    fun `a round trip preserves every field`() {
        val original =
            listOf(entry("10.0.0.1", 100L, identity = "aa", port = 6000), entry("host.local", 90L))

        val restored = RecentConnections.parse(RecentConnections.encode(original))

        assertEquals(original, restored)
    }

    @Test
    fun `an empty or absent value parses to an empty list`() {
        assertTrue(RecentConnections.parse(null).isEmpty())
        assertTrue(RecentConnections.parse("").isEmpty())
    }

    @Test
    fun `a malformed record is dropped rather than defaulted`() {
        // Matching KnownHosts.parseRecords: a preference file written by an older or newer build
        // must not be able to invent an address the user never connected to.
        val good = entry("10.0.0.1", 100L, identity = "aa")
        val encoded =
            listOf(
                    "not-enough-fields",
                    RecentConnections.encode(listOf(good)),
                    listOf("10.0.0.2", "not-a-port", "bb", "50")
                        .joinToString(RecentConnections.FieldSeparator),
                    listOf("10.0.0.3", "5005", "cc", "not-a-time")
                        .joinToString(RecentConnections.FieldSeparator),
                    listOf("", "5005", "dd", "10").joinToString(RecentConnections.FieldSeparator)
                )
                .joinToString(RecentConnections.RecordSeparator)

        assertEquals(listOf(good), RecentConnections.parse(encoded))
    }

    @Test
    fun `a stored list longer than the maximum is trimmed on the way out`() {
        val oversized = (1..9).map { entry("10.0.0.$it", it.toLong(), identity = "pc$it") }

        val parsed = RecentConnections.parse(RecentConnections.encode(oversized))

        assertEquals(RecentConnections.MaxEntries, parsed.size)
    }

    // ── forgetting ───────────────────────────────────────────────────────────

    @Test
    fun `unpairing a machine drops every address it owns`() {
        val list =
            listOf(
                entry("10.0.0.1", 300L, identity = "gone"),
                // Blank identity, still the machine's: recorded before a pin existed for it.
                entry("10.0.0.2", 200L),
                entry("10.0.0.3", 100L, identity = "other")
            )

        val updated = RecentConnections.forgetMachine(list, "gone", listOf("10.0.0.2"))

        assertEquals(listOf("10.0.0.3"), updated.map { it.address })
    }

    @Test
    fun `forgetting nothing in particular leaves the list alone`() {
        val list = listOf(entry("10.0.0.1", 100L))

        assertSame(list, RecentConnections.forgetMachine(list, "  ", emptyList()))
        assertSame(list, RecentConnections.forgetAddress(list, ""))
    }

    @Test
    fun `an address can be dropped on its own`() {
        val list = listOf(entry("10.0.0.1", 200L), entry("10.0.0.2", 100L))

        assertEquals(
            listOf("10.0.0.2"),
            RecentConnections.forgetAddress(list, "10.0.0.1").map { it.address }
        )
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    @Test
    fun `one machine at two addresses gets two rows`() {
        // The whole point of the change. The certificate-keyed list collapsed these into one row
        // showing a single address, and the address the user needed was the hidden one.
        val studio = host("studio", listOf("10.0.0.1", "100.72.0.4"), nickname = "Studio")
        val recent = listOf(entry("10.0.0.1", 200L, "studio"), entry("100.72.0.4", 100L, "studio"))

        val rows = RecentConnections.rows(recent, listOf(studio))

        assertEquals(listOf("10.0.0.1", "100.72.0.4"), rows.map { it.address })
        assertTrue(rows.all { it.nickname == "Studio" })
        assertTrue(rows.all { it.isTrusted })
    }

    @Test
    fun `an address that is no longer paired keeps its row and is marked untrusted`() {
        // The reason this history is stored separately from the pinned-host map: a PC that was
        // reinstalled is precisely the one the user is trying to reach, and a list derived from
        // current trust would drop it at the moment it became useful.
        val recent = listOf(entry("10.0.0.1", 200L, "gone-pc"))

        val rows = RecentConnections.rows(recent, emptyList())

        assertEquals(1, rows.size)
        assertFalse(rows.first().isTrusted)
        assertNull(rows.first().knownHost)
        assertEquals("10.0.0.1", rows.first().displayName)
    }

    @Test
    fun `a machine paired elsewhere names a stale address without vouching for it`() {
        // The pin store is address-keyed, so being paired at the Tailscale address buys the old LAN
        // address nothing. Showing the nickname is helpful; claiming trust would send the user into
        // a connect that cannot complete.
        val studio = host("studio", listOf("100.72.0.4"), nickname = "Studio")
        val recent = listOf(entry("10.0.0.1", 200L, "studio"))

        val row = RecentConnections.rows(recent, listOf(studio)).single()

        assertEquals("Studio", row.nickname)
        assertFalse(row.isTrusted)
        // Still reachable for rename/unpair, because the machine itself is still paired.
        assertEquals(studio, row.knownHost)
    }

    @Test
    fun `a paired PC with no recent address still gets a row`() {
        // Rename and unpair are reached from a row's overflow. A fresh pairing that has not
        // connected yet, or an install predating this list, would otherwise be invisible and
        // unmanageable while still holding a pin.
        val fresh = host("fresh", listOf("10.0.0.7"), nickname = "New PC")

        val rows = RecentConnections.rows(emptyList(), listOf(fresh))

        assertEquals(1, rows.size)
        assertEquals("10.0.0.7", rows.first().address)
        assertTrue(rows.first().isTrusted)
        assertFalse(rows.first().hasEverConnected)
    }

    @Test
    fun `a machine already shown among the recent rows is not repeated at the end`() {
        val studio = host("studio", listOf("10.0.0.1", "100.72.0.4"), nickname = "Studio")
        val recent = listOf(entry("10.0.0.1", 200L, "studio"))

        val rows = RecentConnections.rows(recent, listOf(studio))

        assertEquals(1, rows.size)
    }

    @Test
    fun `trailing rows are ordered most recently connected first`() {
        val older = host("older", listOf("10.0.0.1"), millis = 100L)
        val newer = host("newer", listOf("10.0.0.2"), millis = 200L)

        val rows = RecentConnections.rows(emptyList(), listOf(older, newer))

        assertEquals(listOf("10.0.0.2", "10.0.0.1"), rows.map { it.address })
    }

    @Test
    fun `rows use the port that was recorded with the address`() {
        // Not the machine's, which is the port that last worked SOMEWHERE. A PC reachable on 5005
        // over the LAN and on a forwarded port elsewhere would otherwise have one of its rows dial
        // the other row's port.
        val studio = host("studio", listOf("10.0.0.1"), port = 5005)
        val recent = listOf(entry("10.0.0.1", 200L, "studio", port = 7000))

        assertEquals(7000, RecentConnections.rows(recent, listOf(studio)).single().port)
    }

    @Test
    fun `the row list is deduplicated and capped like the store`() {
        val recent =
            listOf(
                entry("10.0.0.1", 300L, "a"),
                entry("10.0.0.1", 200L, "a"),
                entry("10.0.0.2", 100L, "b")
            )

        val rows = RecentConnections.rows(recent, emptyList())

        assertEquals(listOf("10.0.0.1", "10.0.0.2"), rows.map { it.address })
    }
}
