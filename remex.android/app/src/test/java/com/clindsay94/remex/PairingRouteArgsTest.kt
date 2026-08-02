package com.clindsay94.remex

import com.clindsay94.remex.ui.PairingRouteArgs
import com.clindsay94.remex.ui.PairingRouteResult
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins that a malformed pairing route fails loudly instead of quietly (RemEx-667p).
 *
 * The defect being replaced is a pair of silent fallbacks - `?: ""` for the host and `?: 5005` for
 * the port - which produce a pairing screen that looks operational and cannot possibly succeed. The
 * user sees a pairing attempt time out for no stated reason, which is indistinguishable from their
 * PC being switched off.
 */
class PairingRouteArgsTest {

    @Test
    fun `a well formed route parses`() {
        val result = PairingRouteArgs.parse("192.168.1.50", "5005")

        assertEquals(PairingRouteResult.Valid("192.168.1.50", 5005), result)
    }

    @Test
    fun `a missing host is refused rather than becoming an empty string`() {
        // THE BUG. `?: ""` produces a screen with nothing to connect to, and nothing anywhere says
        // so - the failure surfaces minutes later as a timeout the user attributes to their PC.
        assertTrue(PairingRouteArgs.parse(null, "5005") is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("", "5005") is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("   ", "5005") is PairingRouteResult.Invalid)
    }

    @Test
    fun `a missing port is refused rather than becoming the default`() {
        // Substituting 5005 for a port the caller never supplied hides the caller's bug and, on a
        // machine running on another port, offers a PIN to nothing.
        assertTrue(PairingRouteArgs.parse("192.168.1.50", null) is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("192.168.1.50", "") is PairingRouteResult.Invalid)
    }

    @Test
    fun `a host containing a path separator is refused`() {
        // The route is "pairing/{host}/{port}", so the host occupies ONE path segment. A slash does
        // not merely look wrong - it shifts every segment after it, so the port is read from the
        // middle of the host and the route stops matching at all.
        assertTrue(PairingRouteArgs.parse("192.168.1.50/evil", "5005") is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("host\\share", "5005") is PairingRouteResult.Invalid)
    }

    @Test
    fun `a non-numeric port is refused rather than throwing`() {
        // toInt would throw out of a navigation callback, where nothing can catch it - a crash
        // instead of a wrong screen is not an improvement.
        val result = PairingRouteArgs.parse("192.168.1.50", "not-a-port")

        assertTrue(result is PairingRouteResult.Invalid)
        assertTrue((result as PairingRouteResult.Invalid).reason.contains("not a number"))
    }

    @Test
    fun `an out of range port is refused rather than clamped`() {
        // Clamping to 5005 would connect the user somewhere they did not ask for - which on a
        // pairing screen means offering a PIN to the wrong machine.
        assertTrue(PairingRouteArgs.parse("host", "0") is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("host", "-1") is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("host", "65536") is PairingRouteResult.Invalid)
        assertTrue(PairingRouteArgs.parse("host", "999999") is PairingRouteResult.Invalid)
    }

    @Test
    fun `the boundary ports are accepted`() {
        assertTrue(PairingRouteArgs.parse("host", "1") is PairingRouteResult.Valid)
        assertTrue(PairingRouteArgs.parse("host", "65535") is PairingRouteResult.Valid)
    }

    @Test
    fun `surrounding whitespace is tolerated rather than rejected`() {
        // A caller that interpolated a value with a stray space has a cosmetic bug, not a routing
        // one, and refusing would turn it into a dead screen for no gain.
        assertEquals(
            PairingRouteResult.Valid("192.168.1.50", 5005),
            PairingRouteArgs.parse("  192.168.1.50  ", " 5005 ")
        )
    }

    @Test
    fun `a route that would not parse cannot be built`() {
        // The check happens where the CALLER can still do something about it, rather than on the
        // screen that receives it.
        assertNull(PairingRouteArgs.buildPath("pairing", "", 5005))
        assertNull(PairingRouteArgs.buildPath("pairing", "host/evil", 5005))
        assertNull(PairingRouteArgs.buildPath("pairing", "host", 0))
    }

    @Test
    fun `a built route round-trips back to the same values`() {
        // The property that makes the two halves one rule: anything buildPath emits must parse back
        // to what went in, or a navigation succeeds and the destination disagrees about where it is.
        val path = PairingRouteArgs.buildPath("pairing", "desktop-pc.local", 8338)

        assertEquals("pairing/desktop-pc.local/8338", path)

        val segments = path!!.split("/")
        assertEquals(
            PairingRouteResult.Valid("desktop-pc.local", 8338),
            PairingRouteArgs.parse(segments[1], segments[2])
        )
    }

    @Test
    fun `an invalid result carries a reason a developer can act on`() {
        // The point of failing loudly is the message. "Invalid" with no reason reproduces the
        // silence this replaces, one layer up.
        val reasons = listOf(
            PairingRouteArgs.parse(null, "5005"),
            PairingRouteArgs.parse("host", null),
            PairingRouteArgs.parse("host", "abc"),
            PairingRouteArgs.parse("host", "70000"),
            PairingRouteArgs.parse("a/b", "5005")
        ).filterIsInstance<PairingRouteResult.Invalid>()

        assertEquals(5, reasons.size)
        assertTrue(reasons.all { it.reason.isNotBlank() })
    }
}
