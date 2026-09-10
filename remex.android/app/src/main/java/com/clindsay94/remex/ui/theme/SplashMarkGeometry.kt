package com.clindsay94.remex.ui.theme

/**
 * The geometry [SplashExitView][com.clindsay94.remex.SplashExitView] needs to place the accent
 * dots on top of the `ic_launcher_monochrome` mask it draws the mark from. Pure and JVM-testable,
 * split out of the view (RemEx-alwfa.1 review) so the derivation itself — not just the final
 * numbers — is checked.
 *
 * `ic_launcher_monochrome.xml`'s three dots are each drawn as
 * `M<x>,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z`. An SVG/vector-drawable arc path STARTS
 * at a point ON the circle, not at its center — here the leftmost point, since the two arcs sweep
 * out a horizontal diameter of length `4.4` (= 2 * radius) back to the start. The true center is
 * therefore `x + radius`, not `x` (the review's catch: this file previously used the raw path `x`
 * values as centers, drawing the dots one full radius too far left). Centers are then carried
 * through that vector's own `<group scaleX="0.8" scaleY="0.8" pivotX="54" pivotY="54">` transform,
 * since [com.clindsay94.remex.SplashExitView] draws directly onto the mask bitmap's untransformed
 * 108-unit space.
 */
object SplashMarkGeometry {

    /** `ic_launcher_monochrome.xml`'s `viewportWidth`/`viewportHeight`. */
    const val ViewportSize = 108f

    private const val GroupPivot = 54f
    private const val GroupScale = 0.8f
    private const val DotRadius = 2.2f
    private val DotPathStartXs = floatArrayOf(27.3f, 34.3f, 41.3f)
    private const val DotPathY = 36f

    /** `ic_launcher_monochrome.xml`'s own group transform: `t = pivot + (v - pivot) * scale`. */
    internal fun groupTransform(v: Float): Float = GroupPivot + (v - GroupPivot) * GroupScale

    /** The three accent-dot centers, post-transform, in the mask's 108-unit viewport space. */
    val accentDotCenterXs: FloatArray = DotPathStartXs.map { groupTransform(it + DotRadius) }.toFloatArray()

    val accentDotCenterY: Float = groupTransform(DotPathY)

    val accentDotRadius: Float = DotRadius * GroupScale
}
