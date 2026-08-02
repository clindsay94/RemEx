package com.clindsay94.remex

import com.clindsay94.remex.security.SpkiFingerprint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins how a certificate fingerprint is shown and compared (RemEx-vnps).
 *
 * This sits behind a dialog that offers to CLEAR the user's pin, so the direction of every error
 * matters: telling someone nothing changed, next to a button that discards their protection, is the
 * failure this exists to prevent.
 */
class SpkiFingerprintTest {

    private val pinA = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="
    private val pinB = "n8Kq2LxV9dR4tYbF0000000000000000000000000A0="

    @Test
    fun `the display form is short and grouped so a person can actually check it`() {
        // An unbroken run of sixteen characters is not comparable by eye: a person checks the first
        // three and the last two and calls it a match. Four-character groups are why card numbers
        // and licence keys are grouped.
        assertEquals("n8Kq 2LxV 9dR4 tYbF", SpkiFingerprint.forDisplay(pinA))
    }

    @Test
    fun `two certificates sharing a prefix are NOT reported as the same`() {
        // THE TEST THIS CLASS EXISTS FOR. These two pins are identical for the first 16 characters
        // - exactly what the display shows - and different afterwards. Comparing the DISPLAY form
        // would call them the same certificate, tell the user nothing changed, and leave them
        // looking at a button that clears their pin anyway.
        assertEquals(SpkiFingerprint.forDisplay(pinA), SpkiFingerprint.forDisplay(pinB))
        assertFalse("truncated display must not drive the comparison",
            SpkiFingerprint.isSameCertificate(pinA, pinB))
    }

    @Test
    fun `the same certificate is recognised whichever way it is spelled`() {
        // The app writes pins both bare and sha256/-prefixed in different places. Two spellings of
        // ONE pin must not read as a certificate change - that would send the user to a frightening
        // dialog about a machine that did nothing.
        assertTrue(SpkiFingerprint.isSameCertificate(pinA, "sha256/$pinA"))
        assertTrue(SpkiFingerprint.isSameCertificate(pinA, "  sha256/$pinA  "))
    }

    @Test
    fun `base64 case is significant, so a case difference is a different certificate`() {
        // Folding case would treat two genuinely different certificates as one - the same
        // over-merge that would silently suppress a real warning.
        assertFalse(SpkiFingerprint.isSameCertificate(pinA, pinA.lowercase()))
    }

    @Test
    fun `an absent pin shows a marker rather than a blank`() {
        // A dialog comparing old against new must never render an empty cell beside a real value:
        // it reads as "this side is missing" rather than "there is no pin", and the two lead a user
        // to opposite conclusions about whether something is wrong.
        assertEquals(SpkiFingerprint.Unavailable, SpkiFingerprint.forDisplay(null))
        assertEquals(SpkiFingerprint.Unavailable, SpkiFingerprint.forDisplay(""))
        assertEquals(SpkiFingerprint.Unavailable, SpkiFingerprint.forDisplay("   "))
        assertEquals(SpkiFingerprint.Unavailable, SpkiFingerprint.forDisplay("sha256/"))
    }

    @Test
    fun `an unknown pin is never the same as anything, including another unknown`() {
        // "Unknown equals unknown" would report no certificate change when in fact nothing is known
        // about either side - suppressing the warning in exactly the case with least information.
        assertFalse(SpkiFingerprint.isSameCertificate(null, null))
        assertFalse(SpkiFingerprint.isSameCertificate(pinA, null))
        assertFalse(SpkiFingerprint.isSameCertificate(null, pinA))
    }

    @Test
    fun `a pin shorter than the display length is shown in full rather than padded`() {
        // Defensive: a truncated or malformed pin should render what it has, not crash and not
        // invent characters that a user might then try to compare.
        assertEquals("abcd ef", SpkiFingerprint.forDisplay("abcdef"))
    }

    @Test
    fun `the same pin always renders identically`() {
        // The user is comparing two strings on one screen. Any instability at all defeats that.
        assertEquals(SpkiFingerprint.forDisplay(pinA), SpkiFingerprint.forDisplay("sha256/$pinA"))
    }
}
