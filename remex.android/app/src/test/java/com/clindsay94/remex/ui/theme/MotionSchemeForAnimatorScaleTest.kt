package com.clindsay94.remex.ui.theme

import androidx.compose.animation.core.SpringSpec
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * RemEx-tej8: the theme must supply a different (calmer) MotionScheme when the system
 * animator duration scale is 0 ("Remove animations") than when animations are enabled.
 * Scheme instances don't implement equals, so the assertions compare the spring
 * parameters of the schemes' default spatial specs.
 */
class MotionSchemeForAnimatorScaleTest {

    private fun spatialSpring(scale: Float): SpringSpec<Float> =
        motionSchemeForAnimatorScale(scale).defaultSpatialSpec<Float>() as SpringSpec<Float>

    @Test
    fun `scale zero yields a different scheme than scale one`() {
        val reduced = spatialSpring(0f)
        val full = spatialSpring(1f)
        assertTrue(
            "standard and expressive schemes should differ in spring parameters",
            reduced.dampingRatio != full.dampingRatio || reduced.stiffness != full.stiffness,
        )
    }

    @Test
    fun `scale zero spring is calmer than the expressive one`() {
        // Reduced motion gets the standard scheme: more damped (less bounce) than expressive.
        assertTrue(spatialSpring(0f).dampingRatio > spatialSpring(1f).dampingRatio)
    }

    @Test
    fun `nonzero scales keep the expressive scheme`() {
        val half = spatialSpring(0.5f)
        val one = spatialSpring(1f)
        assertEquals(one.dampingRatio, half.dampingRatio, 0f)
        assertEquals(one.stiffness, half.stiffness, 0f)
    }
}
