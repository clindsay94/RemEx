package com.clindsay94.remex.ui

import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.PairingErrors
import com.clindsay94.remex.ui.screens.PairingSurface
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins which pairing failures leave the session usable, and which do not (RemEx-d3z9).
 *
 * **THE ANSWER IS NOT GUESSABLE FROM THE NAMES, WHICH IS THE ENTIRE REASON THIS EXISTS.**
 * `PIN_FETCH_TIMEOUT` reads like something you retry. It is not: bounding the PIN auto-fetch means
 * cancelling a read, and `ClientWebSocket` registers `Abort()` on the token it is handed — so a
 * cancelled read does not stop waiting, it kills the socket. That is measured against a real TLS
 * connection in `CancelledReceiveKillsTheSocketTests` on the C# side, not assumed here.
 *
 * Getting it wrong is not cosmetic. On the dedicated pairing screen the Submit button stays enabled,
 * so "get a fresh PIN and try again" sends the user to submit on a dead socket, which fails as an
 * unknown error and repeats forever.
 */
class PairingDeadSessionGroupingTest {

    @Test
    fun `a fetch timeout kills the session despite reading like a retry`() {
        assertTrue(
            "PIN_FETCH_TIMEOUT must be treated as dead-session: bounding the fetch cancels a read, " +
                "and cancelling a read aborts the socket.",
            PairingErrors.killsSession("PIN_FETCH_TIMEOUT"),
        )
    }

    @Test
    fun `a host that simply has no PIN leaves the session alone`() {
        // The distinction the two used to share an arm over. PIN_UNAVAILABLE is a REPLY — the host
        // declined or has no active PIN — so nothing was cancelled and the socket is fine. Manual
        // entry is exactly the right next step here, and the only case where it still is.
        assertFalse(PairingErrors.killsSession("PIN_UNAVAILABLE"))
        assertEquals(
            R.string.pairing_error_empty_response,
            PairingErrors.messageRes("PIN_UNAVAILABLE", PairingSurface.Dedicated),
        )
    }

    @Test
    fun `abandoning a start is survivable, abandoning a submission is not`() {
        // Start has no session to lose — the next attempt clears whatever it left. A submission has
        // already sent its PairingComplete and never read the answer, so it is torn down.
        assertFalse(PairingErrors.killsSession("PAIRING_ABORTED"))
        assertTrue(PairingErrors.killsSession("PAIRING_ABORTED_SESSION_LOST"))
    }

    @Test
    fun `nothing unknown is assumed fatal`() {
        assertFalse(PairingErrors.killsSession(null))
        assertFalse(PairingErrors.killsSession(""))
        assertFalse(PairingErrors.killsSession("SOME_FUTURE_CODE"))
    }

    @Test
    fun `every session-killing cause gets the advice that matches the surface`() {
        // THE INVARIANT THAT KEEPS THE TWO IN STEP. killsSession and messageRes are separate lists
        // of the same fact, and a code added to one and not the other is invisible: the user just
        // gets advice that does not fit the state they are in. Asserting the agreement is what makes
        // adding a code to only one of them a test failure.
        val killers = listOf(
            "PIN_REJECTED",
            "NO_SESSION",
            "SESSION_KEY_LOST",
            "PIN_CONFIRM_TIMEOUT",
            "PAIRING_ABORTED_SESSION_LOST",
            "PIN_FETCH_TIMEOUT",
        )

        for (code in killers) {
            assertTrue("$code should be in killsSession", PairingErrors.killsSession(code))

            // The dead-session group is the only one whose message depends on the surface, because
            // the recovery differs: Cancel on the dedicated screen, re-Connect on the inline one.
            assertEquals(
                "$code must take the dedicated-surface dead-session advice",
                R.string.pairing_error_verify_failed,
                PairingErrors.messageRes(code, PairingSurface.Dedicated),
            )
            assertEquals(
                "$code must take the inline-surface dead-session advice",
                R.string.pairing_error_bad_pin,
                PairingErrors.messageRes(code, PairingSurface.InlineConnect),
            )
        }
    }

    @Test
    fun `a survivable cause never takes the dead-session advice`() {
        // The other direction, so the two lists cannot agree by both being wrong.
        val survivable = listOf(
            "PIN_UNAVAILABLE",
            "PAIRING_ABORTED",
            "PAIR_TIMEOUT",
            "TCP_TIMEOUT",
            "TLS_TIMEOUT",
            "HOST_URL_INVALID",
        )

        for (code in survivable) {
            assertFalse("$code should not be in killsSession", PairingErrors.killsSession(code))
            assertTrue(
                "$code must not render the dead-session advice",
                PairingErrors.messageRes(code, PairingSurface.Dedicated) !=
                    R.string.pairing_error_verify_failed,
            )
        }
    }
}
