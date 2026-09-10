package com.clindsay94.remex.ui

import com.clindsay94.remex.security.SpkiFingerprint
import com.clindsay94.remex.ui.screens.CertRepairPrompt
import com.clindsay94.remex.ui.screens.CertRepairState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * What the certificate-change dialog is allowed to offer, and when (RemEx-vnps).
 *
 * Every assertion here is about the same thing: the button that discards a user's certificate pin
 * must appear only when discarding it is actually the situation. The bead exists because it used to
 * appear on a bare tap.
 */
class CertRepairPromptTest {

    private val pinned = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="
    private val different = "Zq3WdE5rT7yU9iO1pA2sD4fG6hJ8kL0zX3cV5bN7mQ4="

    private fun prompt(
        pinnedPin: String? = pinned,
        presentedPin: String? = null,
        probeFinished: Boolean = false
    ) = CertRepairPrompt("192.168.1.50", pinnedPin, presentedPin, probeFinished)

    @Test
    fun `nothing is offered while the probe is still out`() {
        // A user cannot tap through a comparison that has not been made yet. Both pins may already
        // be sitting in the state - it is the unfinished probe that decides, not the data.
        val checking = prompt(presentedPin = different, probeFinished = false)
        assertEquals(CertRepairState.Checking, checking.state)
        assertFalse("no pin may be discarded before the check lands", checking.canRepair)
    }

    @Test
    fun `a genuinely different certificate is what re-pairing is offered for`() {
        val changed = prompt(presentedPin = different, probeFinished = true)
        assertEquals(CertRepairState.Changed, changed.state)
        assertTrue(changed.canRepair)
    }

    @Test
    fun `the same certificate offers nothing, however it is spelled`() {
        // THE TEST THIS CLASS EXISTS FOR. isCertMismatch turns the error card on for ANY failure
        // mentioning SSL - a handshake timeout, a captive portal, a proxy reset - not only a real
        // pin mismatch. Without this state, every one of those became a one-tap way to discard the
        // pin, which is the opposite of what the card is warning about.
        val unchanged = prompt(presentedPin = pinned, probeFinished = true)
        assertEquals(CertRepairState.Unchanged, unchanged.state)
        assertFalse("an unchanged certificate must not offer to clear the pin", unchanged.canRepair)

        // The store and the probe write pins in two spellings. One certificate must not read as two.
        val prefixed = prompt(presentedPin = "sha256/$pinned", probeFinished = true)
        assertEquals(CertRepairState.Unchanged, prefixed.state)
        assertFalse(prefixed.canRepair)
    }

    @Test
    fun `two certificates sharing the displayed prefix are still a change`() {
        // The rendered fingerprints are truncated, so these two LOOK identical on screen. The
        // decision is made on the full value; if it were made on the display form, a certificate
        // swap that matched the first sixteen characters would be reported as "nothing changed".
        val lookalike = pinned.take(16) + "0000000000000000000000000A0="
        val changed = prompt(presentedPin = lookalike, probeFinished = true)

        assertEquals(
            "precondition: these must be indistinguishable on screen",
            SpkiFingerprint.forDisplay(pinned),
            SpkiFingerprint.forDisplay(lookalike)
        )
        assertEquals(CertRepairState.Changed, changed.state)
    }

    @Test
    fun `a host that did not answer is unknown, not safe`() {
        // Nothing answered, so nothing was ruled out. Re-pairing stays available - the user may be
        // rebuilding a PC that is currently off - but the dialog says out loud that it could not
        // look, rather than showing a blank second fingerprint that reads as "no certificate".
        for (nothing in listOf(null, "", "   ")) {
            val unknown = prompt(presentedPin = nothing, probeFinished = true)
            assertEquals(CertRepairState.Unknown, unknown.state)
            assertTrue(unknown.canRepair)
            assertEquals(SpkiFingerprint.Unavailable, unknown.presentedFingerprint)
        }
    }

    @Test
    fun `an unpinned host reads as changed rather than as a match`() {
        // Not reachable from the error card, which only fires for a pinned host - but "no pin" must
        // never fall through to Unchanged, because Unchanged is the branch that suppresses the
        // warning entirely.
        val nothingPinned = prompt(pinnedPin = null, presentedPin = different, probeFinished = true)
        assertEquals(CertRepairState.Changed, nothingPinned.state)
        assertEquals(SpkiFingerprint.Unavailable, nothingPinned.pinnedFingerprint)
    }

    @Test
    fun `both fingerprints render in the grouped comparable form`() {
        // The dialog asks the user to compare these two by eye. They have to be rendered the same
        // way, from the same code, or the comparison is between two different formats.
        val changed = prompt(presentedPin = different, probeFinished = true)
        assertEquals(SpkiFingerprint.forDisplay(pinned), changed.pinnedFingerprint)
        assertEquals(SpkiFingerprint.forDisplay(different), changed.presentedFingerprint)
        assertEquals("n8Kq 2LxV 9dR4 tYbF", changed.pinnedFingerprint)
    }
}
