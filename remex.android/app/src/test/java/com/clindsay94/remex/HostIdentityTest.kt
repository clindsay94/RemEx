package com.clindsay94.remex

import com.clindsay94.remex.security.HostIdentity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the "which PC is this" key (RemEx-9x5i).
 *
 * The failure it prevents is a Known-PCs list that shows one machine three times - once per address
 * it has ever been reached at - each row holding its own third of the user's nickname, preferences
 * and last-connected time.
 */
class HostIdentityTest {

    private val pinA = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="
    private val pinB = "Zx9Lm2QwErTyUiOpAsDfGhJkLzXcVbNm1234567890A="

    @Test
    fun `the same PC reached at three addresses is one entry`() {
        // THE WHOLE POINT. recordAliases already documents that one PC gets re-paired at new
        // addresses over time - LAN then Tailscale, or after a DHCP change - and listPaired is
        // keyed by address, so without this the user sees the same machine three times.
        val paired = mapOf(
            "192.168.1.50" to pinA,
            "100.72.10.4" to pinA,
            "desktop-pc.local" to pinA
        )

        val grouped = HostIdentity.groupByIdentity(paired)

        assertEquals(1, grouped.size)
        assertEquals(
            listOf("192.168.1.50", "100.72.10.4", "desktop-pc.local"),
            grouped.values.single()
        )
    }

    @Test
    fun `two different PCs stay two entries`() {
        // The counterpart. Over-merging would be worse than not merging: a user's laptop settings
        // would silently overwrite their desktop's.
        val paired = mapOf(
            "192.168.1.50" to pinA,
            "192.168.1.51" to pinB
        )

        assertEquals(2, HostIdentity.groupByIdentity(paired).size)
    }

    @Test
    fun `the key does not contain the pin`() {
        // A derived opaque key rather than the pin itself, because this ends up as a literal map
        // key in preference stores that are exported, backed up, and read by whoever the user
        // shares a settings file with. The pin is public certificate material, not a credential -
        // but it still says WHICH PC someone paired with.
        val key = HostIdentity.keyFor(pinA)!!

        assertFalse(pinA.contains(key))
        assertFalse(key.contains(pinA.take(8)))
        assertTrue("expected lowercase hex, got $key", key.matches(Regex("[0-9a-f]{16}")))
    }

    @Test
    fun `the same pin always derives the same key`() {
        // A key that varied per call would orphan every nickname on the next app launch.
        assertEquals(HostIdentity.keyFor(pinA), HostIdentity.keyFor(pinA))
        assertNotEquals(HostIdentity.keyFor(pinA), HostIdentity.keyFor(pinB))
    }

    @Test
    fun `the sha256 prefix and surrounding whitespace do not create a second PC`() {
        // The app writes pins both bare and sha256/-prefixed in different places
        // (FileTransferChannelClient normalizes the same way). Two spellings of one pin must not
        // become two machines with half the user's settings each.
        assertEquals(HostIdentity.keyFor(pinA), HostIdentity.keyFor("sha256/$pinA"))
        assertEquals(HostIdentity.keyFor(pinA), HostIdentity.keyFor("  sha256/$pinA  "))
        assertTrue(HostIdentity.isSameHost(pinA, "sha256/$pinA"))
    }

    @Test
    fun `base64 case is NOT folded, because a different case is a different pin`() {
        // Deliberately unlike the whitespace and prefix handling above. Base64 is case-significant,
        // so folding case would merge two genuinely different certificates into one machine - the
        // over-merge failure, which loses data rather than merely duplicating a row.
        assertNotEquals(HostIdentity.keyFor(pinA), HostIdentity.keyFor(pinA.lowercase()))
        assertFalse(HostIdentity.isSameHost(pinA, pinA.lowercase()))
    }

    @Test
    fun `a host with no usable pin has no identity`() {
        // It has not completed pairing. Inventing an identity would create a Known-PCs row that can
        // never be connected to.
        assertNull(HostIdentity.keyFor(null))
        assertNull(HostIdentity.keyFor(""))
        assertNull(HostIdentity.keyFor("   "))
        assertNull(HostIdentity.keyFor("sha256/"))
    }

    @Test
    fun `unidentifiable hosts are skipped rather than merged into one row`() {
        // A placeholder identity would collapse every unidentifiable host into a single entry
        // claiming to be several machines - worse than omitting them, because it looks correct.
        val paired = mapOf(
            "192.168.1.50" to pinA,
            "192.168.1.60" to "",
            "192.168.1.61" to "   "
        )

        val grouped = HostIdentity.groupByIdentity(paired)

        assertEquals(1, grouped.size)
        assertEquals(listOf("192.168.1.50"), grouped.values.single())
    }

    @Test
    fun `isSameHost is false when either side is unknown`() {
        // Not "unknown equals unknown". Two hosts that both failed to identify are not thereby the
        // same host, and returning true would merge them.
        assertFalse(HostIdentity.isSameHost(null, null))
        assertFalse(HostIdentity.isSameHost(pinA, null))
        assertFalse(HostIdentity.isSameHost(null, pinA))
        assertTrue(HostIdentity.isSameHost(pinA, pinA))
    }

    @Test
    fun `an empty inventory groups to nothing rather than failing`() {
        assertTrue(HostIdentity.groupByIdentity(emptyMap()).isEmpty())
    }
}
