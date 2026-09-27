package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.nfc.NfcHaptic
import com.clindsay94.remex.routines.nfc.NfcRoutineTag
import com.clindsay94.remex.routines.nfc.NfcTagInspection
import com.clindsay94.remex.routines.nfc.NfcTagInspection.Kind
import com.clindsay94.remex.routines.nfc.NfcWriteOutcome
import com.clindsay94.remex.routines.nfc.NfcWriteRules
import com.clindsay94.remex.routines.nfc.NfcWriteRules.Preflight
import com.clindsay94.remex.routines.nfc.NfcWriteState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * `NfcWriteReducerTest` and `NfcExistingTagTest` of spec §13.3 (R-UX-14, R-SYS-41): every row of the
 * 1.5 table is reachable, an existing RemEx tag is named before it is overwritten, and read-only and
 * too-small tags are refused before anything is written.
 */
class NfcWriteFlowTest {
    private val movie = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10"
    private val game = "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00"
    private val names = mapOf(movie to "Movie night", game to "Game night")
    private val payload = 110

    private fun ndef(uri: String? = null, writable: Boolean = true, max: Int = 137) = NfcTagInspection(Kind.NDEF, writable, max, uri)

    private fun preflight(inspection: NfcTagInspection, replaced: String? = null) =
        NfcWriteRules.preflight(inspection, movie, payload, replaced) { names[it] }

    @Test
    fun `the opening state follows the hardware`() {
        assertEquals(NfcWriteState.NoHardware, NfcWriteRules.initial(hasHardware = false, enabled = false))
        assertEquals(NfcWriteState.Off, NfcWriteRules.initial(hasHardware = true, enabled = false))
        assertEquals(NfcWriteState.Waiting, NfcWriteRules.initial(hasHardware = true, enabled = true))
    }

    @Test
    fun `a blank writable tag is written`() {
        assertEquals(Preflight.Write, preflight(ndef()))
        assertEquals(Preflight.Write, preflight(NfcTagInspection(Kind.FORMATABLE, true, -1, null)))
    }

    @Test
    fun `an existing tag for another routine on this phone is named and asked about`() {
        val other = NfcRoutineTag(game, NfcTokens.mint()).toUri()
        assertEquals(Preflight.Stop(NfcWriteState.ExistingTag(game, "Game night")), preflight(ndef(other)))
    }

    @Test
    fun `after Replace the same tag is written`() {
        val other = NfcRoutineTag(game, NfcTokens.mint()).toUri()
        assertEquals(Preflight.Write, preflight(ndef(other), replaced = game))
    }

    @Test
    fun `this routine's own tag, another phone's tag and a foreign tag are written without asking`() {
        assertEquals(Preflight.Write, preflight(ndef(NfcRoutineTag(movie, NfcTokens.mint()).toUri())))
        assertEquals(Preflight.Write, preflight(ndef(NfcRoutineTag("0a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00", NfcTokens.mint()).toUri())))
        assertEquals(Preflight.Write, preflight(ndef("https://example.com/")))
    }

    @Test
    fun `read-only and too-small tags are refused before writing`() {
        assertEquals(Preflight.Stop(NfcWriteState.ReadOnly), preflight(ndef(writable = false)))
        assertEquals(Preflight.Stop(NfcWriteState.ReadOnly), preflight(NfcTagInspection(Kind.UNSUPPORTED, false, 0, null)))
        assertEquals(Preflight.Stop(NfcWriteState.TooSmall), preflight(ndef(max = 46)))
        // A read-only tag is reported as read-only even when it is also another routine's.
        val other = NfcRoutineTag(game, NfcTokens.mint()).toUri()
        assertEquals(Preflight.Stop(NfcWriteState.ReadOnly), preflight(ndef(other, writable = false)))
    }

    @Test
    fun `write outcomes map to their rows`() {
        assertEquals(NfcWriteState.Success, NfcWriteRules.afterWrite(NfcWriteOutcome.WRITTEN))
        assertEquals(NfcWriteState.LostContact, NfcWriteRules.afterWrite(NfcWriteOutcome.LOST_CONTACT))
        assertEquals(NfcWriteState.ReadOnly, NfcWriteRules.afterWrite(NfcWriteOutcome.READ_ONLY))
        assertEquals(NfcWriteState.TooSmall, NfcWriteRules.afterWrite(NfcWriteOutcome.TOO_SMALL))
        assertEquals(NfcWriteState.TestSuccess, NfcWriteRules.afterTestRead(true))
        assertEquals(NfcWriteState.TestFailed, NfcWriteRules.afterTestRead(false))
    }

    @Test
    fun `success confirms and failures reject (haptics)`() {
        assertEquals(NfcHaptic.CONFIRM, NfcWriteRules.haptic(NfcWriteState.Success))
        assertEquals(NfcHaptic.CONFIRM, NfcWriteRules.haptic(NfcWriteState.TestSuccess))
        listOf(NfcWriteState.ReadOnly, NfcWriteState.TooSmall, NfcWriteState.LostContact).forEach {
            assertEquals(NfcHaptic.REJECT, NfcWriteRules.haptic(it))
        }
        listOf(NfcWriteState.Waiting, NfcWriteState.Writing, NfcWriteState.Off, NfcWriteState.NoHardware).forEach {
            assertEquals(NfcHaptic.NONE, NfcWriteRules.haptic(it))
        }
    }

    @Test
    fun `only the waiting states listen for a tag`() {
        assertTrue(NfcWriteRules.readsTags(NfcWriteState.Waiting))
        assertTrue(NfcWriteRules.readsTags(NfcWriteState.TestWaiting))
        assertFalse(NfcWriteRules.readsTags(NfcWriteState.Writing))
        assertFalse(NfcWriteRules.readsTags(NfcWriteState.ExistingTag(game, "Game night")))
        assertFalse(NfcWriteRules.readsTags(NfcWriteState.Success))
    }
}
