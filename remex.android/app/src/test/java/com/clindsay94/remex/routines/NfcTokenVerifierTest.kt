package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.nfc.NfcRoutineTag
import com.clindsay94.remex.routines.nfc.NfcTapVerdict
import com.clindsay94.remex.routines.nfc.NfcTokenVerifier
import java.security.SecureRandom
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Spec §9 T2, T3, R-SYS-17, R-SEC-03, R-SEC-04: the tag checks of §8.3.2. */
class NfcTokenVerifierTest {
    private val routineId = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10"
    private val token = NfcTokens.mint(SecureRandom())
    private val tag = NfcRoutineTag(routineId, token)

    @Test
    fun `a valid tap on an unlocked phone runs`() {
        val verdict = NfcTokenVerifier().verify(tag, token, routineExists = true, deviceLocked = false, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.Run(routineId), verdict)
    }

    @Test
    fun `a wrong token is refused as an unknown tag against the routine`() {
        val verdict = NfcTokenVerifier().verify(tag, NfcTokens.mint(), routineExists = true, deviceLocked = false, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.UnknownTag(routineId), verdict)
    }

    @Test
    fun `the old token stops working after a rotation`() {
        val rotated = NfcTokens.mint()
        assertNotEquals(token, rotated)
        val verdict = NfcTokenVerifier().verify(tag, rotated, routineExists = true, deviceLocked = false, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.UnknownTag(routineId), verdict)
    }

    @Test
    fun `RejectsUnknownRoutine - another phone's or a deleted routine's tag runs nothing`() {
        val verdict = NfcTokenVerifier().verify(tag, null, routineExists = false, deviceLocked = false, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.UnknownTag(null), verdict)
    }

    @Test
    fun `a routine with no token yet refuses every tag`() {
        val verdict = NfcTokenVerifier().verify(tag, null, routineExists = true, deviceLocked = false, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.UnknownTag(routineId), verdict)
    }

    @Test
    fun `a locked phone refuses a valid tag with nfc_device_locked`() {
        val verdict = NfcTokenVerifier().verify(tag, token, routineExists = true, deviceLocked = true, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.DeviceLocked(routineId), verdict)
    }

    @Test
    fun `a locked phone does not tell a wrong token apart from a right one`() {
        val verdict = NfcTokenVerifier().verify(tag, NfcTokens.mint(), routineExists = true, deviceLocked = true, nowElapsedMs = 1_000)
        assertEquals(NfcTapVerdict.UnknownTag(routineId), verdict)
    }

    @Test
    fun `a second tap within 10 s is debounced, and a later one runs`() {
        val verifier = NfcTokenVerifier()
        assertEquals(NfcTapVerdict.Run(routineId), verifier.verify(tag, token, true, false, 50_000))
        assertEquals(NfcTapVerdict.Debounced(routineId), verifier.verify(tag, token, true, false, 59_999))
        assertEquals(NfcTapVerdict.Run(routineId), verifier.verify(tag, token, true, false, 60_000))
    }

    @Test
    fun `a refused tap does not start the debounce window`() {
        val verifier = NfcTokenVerifier()
        verifier.verify(tag, token, true, deviceLocked = true, nowElapsedMs = 10_000)
        assertEquals(NfcTapVerdict.Run(routineId), verifier.verify(tag, token, true, false, 10_500))
    }

    @Test
    fun `tokens are 128-bit base64url, 22 characters, and differ every time`() {
        val tokens = (1..100).map { NfcTokens.mint() }.toSet()
        assertEquals(100, tokens.size)
        assertTrue(tokens.all { NfcTokens.isWellFormed(it) && it.length == 22 })
    }

    @Test
    fun `the tag URI round-trips, and malformed URIs are not tags`() {
        assertEquals(tag, NfcRoutineTag.parse(tag.toUri()))
        assertEquals("remex://routine/$routineId?t=$token", tag.toUri())
        assertNull(NfcRoutineTag.parse(null))
        assertNull(NfcRoutineTag.parse("https://routine/$routineId?t=$token"))
        assertNull(NfcRoutineTag.parse("remex://routine/not-a-uuid?t=$token"))
        assertNull(NfcRoutineTag.parse("remex://routine/$routineId"))
        assertNull(NfcRoutineTag.parse("remex://routine/$routineId?t=short"))
        assertNull(NfcRoutineTag.parse("remex://routine/$routineId?t=$token&t=$token"))
        assertEquals(NfcTapVerdict.NotRoutineTag, NfcTokenVerifier().verify(null, token, true, false, 0))
    }

    @Test
    fun `Test it checks id and token without running anything`() {
        val verifier = NfcTokenVerifier()
        assertTrue(verifier.matches(tag, routineId, token))
        assertFalse(verifier.matches(tag, "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00", token))
        assertFalse(verifier.matches(tag, routineId, NfcTokens.mint()))
        assertFalse(verifier.matches(null, routineId, token))
        // A test tap leaves the debounce window untouched.
        assertEquals(NfcTapVerdict.Run(routineId), verifier.verify(tag, token, true, false, 1))
    }
}
