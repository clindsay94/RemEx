package com.clindsay94.remex.routines

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The `remex_routine_secrets` store (spec §6.8): tokens rotate, keys are stable, blobs are bound to their key. */
class RoutineSecretStoreTest {
    private val kv = FakeKeyValueStore()
    private val store = RoutineSecretStore(kv, FakeCipherSource())
    private val routine = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10"

    @Test
    fun `a committed token is read back, and a rotation replaces it`() = runTest {
        assertNull(store.nfcBinding(routine))
        val first = NfcTokens.mint()
        store.commitNfcToken(routine, first, 100)
        assertEquals(RoutineNfcBinding(first, 100), store.nfcBinding(routine))
        val second = NfcTokens.mint()
        store.commitNfcToken(routine, second, 200)
        assertEquals(RoutineNfcBinding(second, 200), store.nfcBinding(routine))
    }

    @Test
    fun `deleting the routine forgets its token`() = runTest {
        store.commitNfcToken(routine, NfcTokens.mint(), 1)
        store.removeRoutine(routine)
        assertNull(store.nfcBinding(routine))
    }

    @Test
    fun `values are sealed, never stored in the clear, and bound to their key`() = runTest {
        val token = NfcTokens.mint()
        store.commitNfcToken(routine, token, 1)
        val sealed = kv.map.getValue(RoutineSecretStore.nfcKey(routine))
        assertFalse(sealed.contains(token))
        // The same blob under another routine's key does not open.
        val other = "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00"
        kv.map[RoutineSecretStore.nfcKey(other)] = sealed
        assertNull(store.nfcBinding(other))
    }

    @Test
    fun `the shortcut and home keys are 256-bit, stable and distinct`() = runTest {
        val shortcut = store.shortcutKey()
        assertEquals(32, shortcut.size)
        assertArrayEquals(shortcut, store.shortcutKey())
        assertArrayEquals(shortcut, RoutineSecretStore(kv, FakeCipherSource()).shortcutKey())
        assertFalse(shortcut.contentEquals(store.homeKey()))
    }

    @Test
    fun `widget bindings are kept per widget and forgotten when it is removed`() = runTest {
        store.setWidgetRoutine(7, routine)
        assertEquals(routine, store.widgetRoutine(7))
        assertNull(store.widgetRoutine(8))
        store.removeWidget(7)
        assertNull(store.widgetRoutine(7))
        assertTrue(kv.map.isEmpty())
    }
}
