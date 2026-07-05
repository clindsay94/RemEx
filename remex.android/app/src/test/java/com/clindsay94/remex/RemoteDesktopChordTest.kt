package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.MODIFIER_VIRTUAL_KEY_CODES
import com.clindsay94.remex.ui.screens.ModifierState
import com.clindsay94.remex.ui.screens.VK_ALTGR
import com.clindsay94.remex.ui.screens.VK_CTRL
import com.clindsay94.remex.ui.screens.computeChordApplication
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * JVM unit tests for [computeChordApplication] and the [MODIFIER_VIRTUAL_KEY_CODES] registration
 * that AltGr (RemEx-9krr) depends on. Exercises the pure modifier-wrap decision extracted from
 * [com.clindsay94.remex.ui.screens.RemoteDesktopViewModel.sendKeyPress] — no ViewModel instance,
 * DataStore, or network call is involved.
 */
class RemoteDesktopChordTest {

    // ── Registration ───────────────────────────────────────────────────────────

    @Test
    fun `AltGr is registered as a latching modifier`() {
        assertTrue(VK_ALTGR in MODIFIER_VIRTUAL_KEY_CODES)
    }

    // ── Chord application per modifier state ───────────────────────────────────

    @Test
    fun `AltGr OFF (absent from the map) does not press or release anything`() {
        val result =
                computeChordApplication(
                        modifierStates = emptyMap(),
                        physicallyDownModifiers = emptySet(),
                        keyCode = 0x41, // 'A'
                        modifierVirtualKeyCodes = MODIFIER_VIRTUAL_KEY_CODES
                )

        assertTrue(result.toPress.isEmpty())
        assertTrue(result.toRelease.isEmpty())
        assertTrue(result.nextModifierStates.isEmpty())
    }

    @Test
    fun `AltGr ARMED presses it, releases it after the key, and resets to OFF`() {
        val result =
                computeChordApplication(
                        modifierStates = mapOf(VK_ALTGR to ModifierState.ARMED),
                        physicallyDownModifiers = emptySet(),
                        keyCode = 0x41, // 'A'
                        modifierVirtualKeyCodes = MODIFIER_VIRTUAL_KEY_CODES
                )

        assertEquals(listOf(VK_ALTGR), result.toPress)
        assertEquals(listOf(VK_ALTGR), result.toRelease)
        assertFalse(VK_ALTGR in result.nextModifierStates) // one-shot: removed, not left LOCKED/ARMED
    }

    @Test
    fun `AltGr LOCKED stays LOCKED across multiple keys and is not re-pressed once down`() {
        val locked = mapOf(VK_ALTGR to ModifierState.LOCKED)

        val firstKey =
                computeChordApplication(
                        modifierStates = locked,
                        physicallyDownModifiers = emptySet(),
                        keyCode = 0x41, // 'A'
                        modifierVirtualKeyCodes = MODIFIER_VIRTUAL_KEY_CODES
                )
        assertEquals(listOf(VK_ALTGR), firstKey.toPress) // not physically down yet -> press it
        assertTrue(firstKey.toRelease.isEmpty()) // LOCKED is never one-shot released
        assertEquals(locked, firstKey.nextModifierStates) // state map is untouched

        val secondKey =
                computeChordApplication(
                        modifierStates = locked,
                        physicallyDownModifiers = setOf(VK_ALTGR), // already held from firstKey
                        keyCode = 0x42, // 'B'
                        modifierVirtualKeyCodes = MODIFIER_VIRTUAL_KEY_CODES
                )
        assertTrue(secondKey.toPress.isEmpty()) // already down -> no redundant keyDown
        assertTrue(secondKey.toRelease.isEmpty())
        assertEquals(locked, secondKey.nextModifierStates)
    }

    @Test
    fun `modifier keycodes are never wrapped by themselves`() {
        val result =
                computeChordApplication(
                        modifierStates = mapOf(VK_CTRL to ModifierState.ARMED),
                        physicallyDownModifiers = emptySet(),
                        keyCode = VK_ALTGR, // sending the AltGr key itself, not a letter
                        modifierVirtualKeyCodes = MODIFIER_VIRTUAL_KEY_CODES
                )

        assertTrue(result.toPress.isEmpty())
        assertTrue(result.toRelease.isEmpty())
        assertEquals(mapOf(VK_CTRL to ModifierState.ARMED), result.nextModifierStates)
    }
}
