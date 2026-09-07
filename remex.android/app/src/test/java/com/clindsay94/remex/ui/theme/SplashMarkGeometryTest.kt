package com.clindsay94.remex.ui.theme

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The accent-dot center derivation (RemEx-alwfa.1 review, MEDIUM): a vector-drawable arc path
 * starts ON the circle, not at its center, so the raw path `x` values are NOT the centers — see
 * [SplashMarkGeometry]'s class doc for the full derivation this pins down.
 */
class SplashMarkGeometryTest {

    @Test
    fun `accent dot centers add the radius before the group transform, not after`() {
        // x + radius = 27.3+2.2, 34.3+2.2, 41.3+2.2 = 29.5, 36.5, 43.5, THEN
        // t = 54 + (v - 54) * 0.8 = 34.4, 40.0, 45.6.
        val centers = SplashMarkGeometry.accentDotCenterXs

        assertEquals(3, centers.size)
        assertEquals(34.4f, centers[0], 0.01f)
        assertEquals(40.0f, centers[1], 0.01f)
        assertEquals(45.6f, centers[2], 0.01f)
    }

    @Test
    fun `accent dot center y is the path y carried through the same group transform`() {
        // t(36) = 54 + (36 - 54) * 0.8 = 39.6
        assertEquals(39.6f, SplashMarkGeometry.accentDotCenterY, 0.01f)
    }

    @Test
    fun `accent dot radius is scaled by the group's scale factor only`() {
        assertEquals(1.76f, SplashMarkGeometry.accentDotRadius, 0.001f)
    }

    @Test
    fun `group transform is the identity at the pivot`() {
        assertEquals(54f, SplashMarkGeometry.groupTransform(54f), 0.0001f)
    }
}
