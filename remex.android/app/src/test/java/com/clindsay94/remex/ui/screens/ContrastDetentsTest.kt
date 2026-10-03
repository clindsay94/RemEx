package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertEquals
import org.junit.Test

/** The phone contrast slider's detents match the PC's SnapContrastToDetent (RemEx-4kv0g.16). */
class ContrastDetentsTest {

    @Test
    fun `values near a detent land on it`() {
        assertEquals(0f, ContrastDetents.snap(0.04f), 0f)
        assertEquals(0f, ContrastDetents.snap(-0.049f), 0f)
        assertEquals(1f, ContrastDetents.snap(0.96f), 0f)
        assertEquals(-1f, ContrastDetents.snap(-0.97f), 0f)
        assertEquals(1f, ContrastDetents.snap(1f), 0f)
    }

    @Test
    fun `values outside the radius are left alone`() {
        assertEquals(0.06f, ContrastDetents.snap(0.06f), 0f)
        assertEquals(-0.5f, ContrastDetents.snap(-0.5f), 0f)
        assertEquals(0.9f, ContrastDetents.snap(0.9f), 0f)
        assertEquals(-0.93f, ContrastDetents.snap(-0.93f), 0f)
    }

    @Test
    fun `the Personalize contrast slider snaps through it`() {
        // Unit tests run from the module root (remex.android/app). A detent rule the slider never
        // calls would pass every test above while the slider stayed unsnapped.
        val source = java.io.File("src/main/java/com/clindsay94/remex/ui/screens/PersonalizationScreen.kt")
        org.junit.Assert.assertTrue("missing ${source.absolutePath}", source.exists())
        org.junit.Assert.assertTrue(
            "The contrast slider's onValueChange must snap with ContrastDetents.snap.",
            source.readText().contains("onValueChange = { themeContrast = ContrastDetents.snap(it) }"),
        )
    }

    @Test
    fun `the radius is strict, like the PC`() {
        assertEquals(0.05f, ContrastDetents.RADIUS, 0f)
        // Exactly one radius from 0 is not snapped (abs(v - 0) is exactly the radius).
        assertEquals(0.05f, ContrastDetents.snap(0.05f), 0f)
        assertEquals(-0.05f, ContrastDetents.snap(-0.05f), 0f)
    }
}
