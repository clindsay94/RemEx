package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.offersMonitorTargets
import com.clindsay94.remex.ui.screens.offersVirtualTarget
import com.clindsay94.remex.ui.screens.supportedCaptureModes
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers which capture targets the picker offers for a given host catalog (RemEx-e1x4).
 *
 * The whole-desktop option was already gated on the host advertising it, but per-monitor options were
 * built unconditionally. A host that supports only VirtualDesktop — which the fallback catalog does,
 * because it could not enumerate its outputs at all — therefore got a monitor picker whose every entry
 * it would reject. The user sees choices that simply fail, with nothing explaining why.
 *
 * WHAT THESE DO NOT COVER, stated rather than implied: the call sites in `handleDisplayCatalog` are
 * one-line applications of these predicates, and nothing here would fail if someone deleted the
 * `offersMonitorTargets` check from the loop. `handleDisplayCatalog` needs an Android `Application`
 * for its labels, so it cannot be driven from a JVM unit test — the predicates are as close to the
 * decision as this test layer reaches.
 */
class CaptureModeOfferingTest {

    private fun catalog(vararg modes: String): JSONObject =
            JSONObject("""{"supportedCaptureModes":[${modes.joinToString(",") { "\"$it\"" }}]}""")

    @Test
    fun `a host that lists both modes offers both`() {
        val modes = supportedCaptureModes(catalog("VirtualDesktop", "Monitor"))

        assertEquals(setOf("VirtualDesktop", "Monitor"), modes)
        assertTrue(offersMonitorTargets(modes))
        assertTrue(offersVirtualTarget(modes, displayCount = 2))
    }

    @Test
    fun `a host that cannot enumerate its displays is not offered a monitor picker`() {
        // THE BEAD. This is exactly what the host's fallback catalog advertises when display
        // enumeration failed: whole-desktop only. Every monitor target built from it would be
        // rejected.
        val modes = supportedCaptureModes(catalog("VirtualDesktop"))

        assertFalse(
                "a host advertising only VirtualDesktop must not be offered per-monitor targets",
                offersMonitorTargets(modes)
        )
    }

    @Test
    fun `a host that only does monitors is not offered the combined view`() {
        val modes = supportedCaptureModes(catalog("Monitor"))

        assertTrue(offersMonitorTargets(modes))
        assertFalse(offersVirtualTarget(modes, displayCount = 3))
    }

    @Test
    fun `the combined view needs something to combine`() {
        // "Both screens" with one screen is the same target under a name that says otherwise.
        val modes = supportedCaptureModes(catalog("VirtualDesktop", "Monitor"))

        assertFalse(offersVirtualTarget(modes, displayCount = 1))
        assertFalse(offersVirtualTarget(modes, displayCount = 0))
        assertTrue(offersVirtualTarget(modes, displayCount = 2))
    }

    @Test
    fun `a catalog that says nothing is treated as supporting nothing`() {
        // FAIL CLOSED. Assuming support because the host was silent is how the picker ends up
        // offering targets that fail — an older or degraded host simply omits the field.
        assertEquals(emptySet<String>(), supportedCaptureModes(JSONObject("{}")))
        assertEquals(emptySet<String>(), supportedCaptureModes(JSONObject("""{"supportedCaptureModes":[]}""")))
        assertEquals(emptySet<String>(), supportedCaptureModes(JSONObject("""{"supportedCaptureModes":"Monitor"}""")))

        val nothing = supportedCaptureModes(JSONObject("{}"))
        assertFalse(offersMonitorTargets(nothing))
        assertFalse(offersVirtualTarget(nothing, displayCount = 4))
    }

    @Test
    fun `blank and unknown mode names are ignored rather than trusted`() {
        val modes = supportedCaptureModes(catalog("", "Monitor", "SomethingNewer"))

        assertEquals(setOf("Monitor", "SomethingNewer"), modes)
        assertTrue(offersMonitorTargets(modes))
        assertFalse(
                "an unrecognised mode name must not stand in for VirtualDesktop",
                offersVirtualTarget(modes, displayCount = 2)
        )
    }

    @Test
    fun `mode names are matched exactly`() {
        // The wire form is serialized by NAME, so a case or spelling difference is a different mode,
        // not the same one written loosely.
        val modes = supportedCaptureModes(catalog("monitor", "virtualdesktop"))

        assertFalse(offersMonitorTargets(modes))
        assertFalse(offersVirtualTarget(modes, displayCount = 2))
    }
}
