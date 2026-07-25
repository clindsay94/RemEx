package com.clindsay94.remex.ui

import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.PairingErrors
import com.clindsay94.remex.ui.screens.PairingSurface
import com.clindsay94.remex.ui.screens.PairingViewModel
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Unit tests for [PairingViewModel.parsePairingError] and [PairingViewModel.pairingErrorMessageRes]
 * (RemEx-6gkr): the pure half of turning a native pairing failure into a localized message.
 *
 * These matter because the alternative is a real device. The native exports return
 * `"ERROR: <CODE>: <diagnostic>"`, the UI shows the string mapped from CODE and logs the
 * diagnostic, and the only part that can silently go wrong without anyone noticing is the split —
 * the diagnostics themselves contain colons, so a naive parse mis-reads the code and the user
 * quietly gets the generic fallback for every failure. Runs on the JVM; no native layer, no
 * Compose.
 */
class PairingErrorParseTest {

    private val vm = PairingViewModel()

    // ── the split ────────────────────────────────────────────────────────────────

    @Test
    fun `extracts code and detail from a coded failure`() {
        val f = vm.parsePairingError("ERROR: PIN_REJECTED: Pairing verification failed (incorrect PIN or session expired)")
        assertEquals("PIN_REJECTED", f.code)
        assertEquals("Pairing verification failed (incorrect PIN or session expired)", f.detail)
    }

    @Test
    fun `a diagnostic containing colons does not confuse the split`() {
        // This is the case a naive lastIndexOf or split(":") gets wrong.
        val f = vm.parsePairingError("ERROR: TCP_TIMEOUT: TCP probe to 192.168.1.42:5005 timed out after 10s")
        assertEquals("TCP_TIMEOUT", f.code)
        assertEquals("TCP probe to 192.168.1.42:5005 timed out after 10s", f.detail)
    }

    @Test
    fun `nested exception diagnostics keep their own colons in the detail`() {
        val f = vm.parsePairingError("ERROR: UNEXPECTED: SocketException: No such host is known")
        assertEquals("UNEXPECTED", f.code)
        assertEquals("SocketException: No such host is known", f.detail)
    }

    @Test
    fun `a codeless legacy failure yields no code and keeps the whole detail`() {
        // What an older libRemexCore.so would return. Must not be mistaken for a code.
        val f = vm.parsePairingError("ERROR: Pairing handshake timed out")
        assertNull(f.code)
        assertEquals("Pairing handshake timed out", f.detail)
    }

    @Test
    fun `a lowercase or mixed-case token is not accepted as a code`() {
        val f = vm.parsePairingError("ERROR: Invalid host URL 'x': bad")
        assertNull(f.code)
    }

    @Test
    fun `non-error input is returned untouched`() {
        assertNull(vm.parsePairingError("OK:hostId|spki|secret").code)
        assertEquals("OK:hostId|spki|secret", vm.parsePairingError("OK:hostId|spki|secret").detail)
    }

    @Test
    fun `blank and null are total, not exceptional`() {
        assertNull(vm.parsePairingError("").code)
        assertEquals("", vm.parsePairingError("").detail)
        assertNull(vm.parsePairingError(null).code)
        assertEquals("", vm.parsePairingError(null).detail)
    }

    // ── the mapping ──────────────────────────────────────────────────────────────

    @Test
    fun `network causes all map to the reachability message`() {
        val expected = R.string.pairing_error_reach_failed
        assertEquals(expected, vm.pairingErrorMessageRes("HOST_URL_INVALID"))
        assertEquals(expected, vm.pairingErrorMessageRes("TCP_TIMEOUT"))
        assertEquals(expected, vm.pairingErrorMessageRes("TCP_REFUSED"))
        assertEquals(expected, vm.pairingErrorMessageRes("TLS_TIMEOUT"))
    }

    @Test
    fun `on the pairing screen, a dead session points at Cancel and not at a bare retry`() {
        // Submit stays enabled on a dead session (RemEx-aor9), so a message that only says
        // "try again" sends the user round the same failure forever. All four of these must
        // land on the string that names Cancel.
        //
        // PIN_REJECTED is in here deliberately. It is the most common pairing failure of all, and
        // "Incorrect PIN, try again" is precisely the wording that invites a retype-and-resubmit —
        // which on this surface cannot succeed. If this assertion ever fails because someone gave
        // PIN_REJECTED its own "wrong PIN" string here, that is the regression, not the test.
        val expected = R.string.pairing_error_verify_failed
        assertEquals(expected, vm.pairingErrorMessageRes("PIN_REJECTED"))
        assertEquals(expected, vm.pairingErrorMessageRes("NO_SESSION"))
        assertEquals(expected, vm.pairingErrorMessageRes("SESSION_KEY_LOST"))
        assertEquals(expected, vm.pairingErrorMessageRes("PIN_CONFIRM_TIMEOUT"))
    }

    @Test
    fun `on the connection screen, a dead session must not tell the user to tap a missing Cancel`() {
        // ConnectionScreen has no Cancel button, and its connect() calls StartPairing fresh on
        // every tap — so the pairing screen's "Tap Cancel" advice names a control that is not
        // there AND steers away from the recovery that does work. Different surface, different
        // string, same cause.
        val expected = R.string.pairing_error_bad_pin
        for (code in listOf("PIN_REJECTED", "NO_SESSION", "SESSION_KEY_LOST", "PIN_CONFIRM_TIMEOUT")) {
            assertEquals(
                    "$code must not carry Cancel advice on the inline connect surface",
                    expected,
                    PairingErrors.messageRes(code, PairingSurface.InlineConnect),
            )
        }
    }

    @Test
    fun `causes with surface-independent advice read the same on both surfaces`() {
        // Only the dead-session group differs. Everything else already ends in a neutral "then
        // try again", so a surface split there would be noise — and a divergence would mean
        // someone quietly gave one surface worse advice than the other.
        for (code in
                listOf(
                        "HOST_URL_INVALID",
                        "TCP_TIMEOUT",
                        "TCP_REFUSED",
                        "TLS_TIMEOUT",
                        "PAIR_TIMEOUT",
                        "PAIR_MALFORMED",
                        "PIN_FETCH_TIMEOUT",
                        "PIN_UNAVAILABLE",
                        "ARG_MISSING",
                        "UNEXPECTED",
                        null,
                )) {
            assertEquals(
                    "$code should not depend on the surface",
                    PairingErrors.messageRes(code, PairingSurface.Dedicated),
                    PairingErrors.messageRes(code, PairingSurface.InlineConnect),
            )
        }
    }

    @Test
    fun `no mapped cause silently falls through to the generic message`() {
        // What this guards: delete a branch from PairingErrors.messageRes — say PAIR_MALFORMED —
        // and that cause silently starts producing "Pairing failed for an unexpected reason"
        // instead of its explanation. This test fails instead.
        //
        // What it does NOT guard, deliberately stated so nobody trusts it further than it goes:
        // the list below is a hand-kept mirror of PairingErrorCodes.cs, not a reading of it. A
        // FIFTEENTH code added on the C# side and never mapped here would still slip through. That
        // cross-language check is RemEx-odkk; this test only covers the Kotlin side.
        //
        // Only two causes may legitimately reach the generic message: ARG_MISSING is a caller bug
        // the user cannot act on, and UNEXPECTED is by definition unclassified.
        val allowedToBeGeneric = setOf("ARG_MISSING", "UNEXPECTED")
        val knownCodes =
                listOf(
                        "ARG_MISSING",
                        "HOST_URL_INVALID",
                        "TCP_TIMEOUT",
                        "TCP_REFUSED",
                        "TLS_TIMEOUT",
                        "PAIR_TIMEOUT",
                        "PAIR_MALFORMED",
                        "NO_SESSION",
                        "SESSION_KEY_LOST",
                        "PIN_CONFIRM_TIMEOUT",
                        "PIN_REJECTED",
                        "PIN_FETCH_TIMEOUT",
                        "PIN_UNAVAILABLE",
                        "UNEXPECTED",
                )
        for (code in knownCodes) {
            if (code in allowedToBeGeneric) continue
            for (surface in PairingSurface.entries) {
                assertNotEquals(
                        "$code fell through to the generic fallback on $surface",
                        R.string.pairing_error_unknown,
                        PairingErrors.messageRes(code, surface),
                )
            }
        }
    }

    @Test
    fun `timeouts and missing-PIN causes map to their own messages`() {
        assertEquals(R.string.pairing_error_timeout, vm.pairingErrorMessageRes("PAIR_TIMEOUT"))
        assertEquals(R.string.pairing_error_malformed_response, vm.pairingErrorMessageRes("PAIR_MALFORMED"))
        assertEquals(R.string.pairing_error_empty_response, vm.pairingErrorMessageRes("PIN_FETCH_TIMEOUT"))
        assertEquals(R.string.pairing_error_empty_response, vm.pairingErrorMessageRes("PIN_UNAVAILABLE"))
    }

    @Test
    fun `an unknown code falls back to generic rather than leaking a diagnostic`() {
        // The whole point of the fallback: a future native build can add codes without a
        // coordinated release, and an older app must not regress to showing raw English.
        assertEquals(R.string.pairing_error_unknown, vm.pairingErrorMessageRes("SOMETHING_NEW"))
        assertEquals(R.string.pairing_error_unknown, vm.pairingErrorMessageRes(null))
        assertEquals(R.string.pairing_error_unknown, vm.pairingErrorMessageRes("ARG_MISSING"))
        assertEquals(R.string.pairing_error_unknown, vm.pairingErrorMessageRes("UNEXPECTED"))
    }
}
