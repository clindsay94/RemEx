package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.manual.RoutineShortcutSignature
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Spec §9 T1, R-SEC-02: a shortcut runs only with RemEx's own `sig` for exactly that routine. */
class ShortcutSignatureTest {
    private val key = ByteArray(32) { it.toByte() }
    private val otherKey = ByteArray(32) { (it + 1).toByte() }
    private val routine = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10"
    private val otherRoutine = "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00"

    @Test
    fun `a signature made with the key verifies`() {
        assertTrue(RoutineShortcutSignature.verify(key, routine, RoutineShortcutSignature.sign(key, routine)))
    }

    @Test
    fun `a missing or empty sig never verifies`() {
        assertFalse(RoutineShortcutSignature.verify(key, routine, null))
        assertFalse(RoutineShortcutSignature.verify(key, routine, ""))
        assertFalse(RoutineShortcutSignature.verify(key, null, RoutineShortcutSignature.sign(key, routine)))
    }

    @Test
    fun `a sig for another routine is refused`() {
        assertFalse(RoutineShortcutSignature.verify(key, otherRoutine, RoutineShortcutSignature.sign(key, routine)))
    }

    @Test
    fun `a sig made with another key is refused (a reset keystore invalidates old shortcuts)`() {
        assertFalse(RoutineShortcutSignature.verify(key, routine, RoutineShortcutSignature.sign(otherKey, routine)))
    }

    @Test
    fun `a tampered or oversized sig is refused`() {
        val sig = RoutineShortcutSignature.sign(key, routine)
        val flipped = (if (sig[0] == 'A') 'B' else 'A') + sig.substring(1)
        assertFalse(RoutineShortcutSignature.verify(key, routine, flipped))
        assertFalse(RoutineShortcutSignature.verify(key, routine, sig + "A".repeat(64)))
    }

    @Test
    fun `the signature is deterministic, url-safe and distinct per routine`() {
        val sig = RoutineShortcutSignature.sign(key, routine)
        assertEquals(sig, RoutineShortcutSignature.sign(key, routine))
        assertEquals(43, sig.length)
        assertTrue(sig.all { it.isLetterOrDigit() || it == '-' || it == '_' })
        assertNotEquals(sig, RoutineShortcutSignature.sign(key, otherRoutine))
    }
}
