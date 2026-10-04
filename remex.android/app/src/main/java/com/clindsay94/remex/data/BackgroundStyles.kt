package com.clindsay94.remex.data

import kotlin.math.abs

/**
 * The app background the user can pick under Personalize -> Background (RemEx-pp4cm.17): nothing,
 * a quiet texture, or a slowly moving one. Every style is drawn only from the active colour scheme
 * (see `BackgroundPalette`), so it follows seed, dynamic and static colour, light and dark, every
 * style and every contrast level without a colour of its own.
 *
 * The stored value is the lowercase [id]. An unknown or missing value reads as [None], so a phone
 * that has never picked one (every existing user) keeps the plain background it always had.
 */
object BackgroundStyles {
    const val None = "none"
    const val Grain = "grain"
    const val Dots = "dots"
    const val Grid = "grid"
    const val Topographic = "topographic"
    const val Hexagons = "hexagons"
    const val Carbon = "carbon"
    const val Aurora = "aurora"
    const val Mesh = "mesh"
    const val Starfield = "starfield"

    const val Default = None

    /** The still textures, in the order the picker lists them. */
    val Textures: List<String> = listOf(Grain, Dots, Grid, Topographic, Hexagons, Carbon)

    /** The slowly moving styles, in the order the picker lists them. */
    val Animated: List<String> = listOf(Aurora, Mesh, Starfield)

    /** Every style the picker offers, [None] first. */
    val All: List<String> = listOf(None) + Textures + Animated

    /** The stored style [stored] stands for: itself when known, otherwise [None]. */
    fun effective(stored: String?): String = if (stored != null && stored in All) stored else Default

    fun isAnimated(style: String): Boolean = style in Animated

    /** True when [style] paints anything at all. */
    fun isVisible(style: String): Boolean = effective(style) != None
}

/**
 * The intensity slider: how strongly the background shows, from barely there to clearly visible.
 * It scales the layer's opacity, and the opacity itself is capped per style (see
 * `BackgroundPalette.maxAlpha`) so full intensity still never reduces text contrast.
 */
object BackgroundIntensity {
    const val MIN = 0.1f
    const val MAX = 1.0f
    const val DEFAULT = 0.5f

    /** Detents a drag lands on, so a quarter, half, three-quarter and full are easy to hit. */
    val DETENTS = floatArrayOf(0.25f, 0.5f, 0.75f, 1.0f)
    const val RADIUS = 0.03f

    fun clamp(value: Float): Float = if (value.isNaN()) DEFAULT else value.coerceIn(MIN, MAX)

    /** The same rule as the contrast slider's detents: strictly inside [RADIUS] snaps, never traps. */
    fun snap(value: Float): Float {
        val clamped = clamp(value)
        for (detent in DETENTS) {
            if (abs(clamped - detent) < RADIUS) return detent
        }
        return clamped
    }
}

/**
 * When an animated background may run. A still frame is shown instead (never a blank) whenever any
 * rule below says stop, so the choice stays visible and the phone stops spending battery on it.
 */
object BackgroundMotionPolicy {
    /** The most frames per second an animated background draws. */
    const val MAX_FPS = 30
    const val FRAME_INTERVAL_NANOS = 1_000_000_000L / MAX_FPS

    /** Where the shader's clock sits for a still frame: picked because every style looks balanced there. */
    const val STILL_FRAME_SECONDS = 7.0f

    /**
     * True when an animated [style] should be moving: the app is on screen, the user has not turned
     * animations off (animator scale 0), Battery Saver is off, and nothing opaque covers the layer
     * ([covered], e.g. the remote desktop stream). A still texture never moves, so it is always false.
     */
    fun shouldAnimate(
        style: String,
        appVisible: Boolean,
        reducedMotion: Boolean,
        batterySaver: Boolean,
        covered: Boolean = false,
    ): Boolean =
        BackgroundStyles.isAnimated(style) && appVisible && !reducedMotion && !batterySaver && !covered

    /** True when enough time has passed since [lastFrameNanos] to draw the next frame at [MAX_FPS]. */
    fun frameDue(nowNanos: Long, lastFrameNanos: Long): Boolean =
        lastFrameNanos == Long.MIN_VALUE || nowNanos - lastFrameNanos >= FRAME_INTERVAL_NANOS
}
