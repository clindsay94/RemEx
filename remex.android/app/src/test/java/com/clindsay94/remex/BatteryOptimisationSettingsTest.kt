package com.clindsay94.remex

import android.content.Context
import android.content.Intent
import com.clindsay94.remex.ui.screens.openBatteryOptimisationSettings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.mockito.kotlin.mock

/**
 * The battery-settings button crashed the very first run on phones with no screen for the direct
 * request (RemEx-pp4cm.3). Both call sites share one launcher now; this pins its fallback chain with
 * a `start` that throws, so a regression to a bare `startActivity` goes red here instead of on a
 * customer's phone.
 */
class BatteryOptimisationSettingsTest {

    private val context: Context = mock()

    @Test
    fun `direct request failing falls back to the optimisation list`() {
        val started = mutableListOf<Intent>()
        var calls = 0

        val opened = openBatteryOptimisationSettings(context) { intent ->
            calls++
            if (calls == 1) throw IllegalStateException("no activity for the direct request")
            started += intent
        }

        assertTrue("the list screen should count as opened", opened)
        assertEquals("one successful launch, after one failure", 1, started.size)
        assertEquals(2, calls)
    }

    @Test
    fun `both screens missing does not throw and reports failure`() {
        var calls = 0

        val opened = openBatteryOptimisationSettings(context) {
            calls++
            throw IllegalStateException("no activity at all")
        }

        assertFalse(opened)
        assertEquals("tries the direct request, then the list, then stops", 2, calls)
    }

    @Test
    fun `direct request succeeding does not open the second screen`() {
        var calls = 0

        val opened = openBatteryOptimisationSettings(context) { calls++ }

        assertTrue(opened)
        assertEquals(1, calls)
    }
}
