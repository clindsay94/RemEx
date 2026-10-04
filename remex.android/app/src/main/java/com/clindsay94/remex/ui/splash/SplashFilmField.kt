package com.clindsay94.remex.ui.splash

import android.graphics.RuntimeShader
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.SplashBrand
import kotlinx.coroutines.delay

/**
 * The world a fixed-length splash plays in: the film field's `uStyle` (RemEx-pp4cm.11). The ids are
 * the shader's, shared with the PC's `SplashFilmStyle`.
 */
enum class FilmStyle(val id: Float) {
    /** RemEx Command: a command deck, a rolling grid floor and falling data columns. */
    Command(0f),

    /** Cosmic Zoom: nebula and three star layers that stretch into a warp. */
    Cosmic(1f),

    /** Pong: a phosphor CRT court that ripples on every contact. */
    Pong(2f),
}

/**
 * A beat: a real moment in a film (an impact, a paddle contact, the session coming up) that sends a
 * shockwave through the field. [at] is seconds on the film's clock; a [strength] of 0 is no beat.
 */
data class FilmBeat(val x: Float, val y: Float, val at: Float, val strength: Float) {
    companion object {
        val None = FilmBeat(0f, 0f, 0f, 0f)
    }
}

/**
 * The film splashes' colours, resolved from the app's M3 scheme so they follow every theming axis
 * (seed x style x mode x contrast, dynamic colour, monochrome) through the scheme itself.
 *
 * The ground and lights are [LiveHandshakePalette]'s: the field is additive light, so a light scheme
 * supplies a night ground through its inverse roles, and the accent is the brand amber unless it
 * fails contrast. [exit] is the app's own background, which every film fades out to so the hand-off
 * into the app is one colour.
 */
data class FilmPalette(
    val bg0: Color,
    val bg1: Color,
    val primary: Color,
    val secondary: Color,
    val accent: Color,
    val ink: Color,
    val muted: Color,
    val exit: Color,
) {
    companion object {
        fun from(scheme: ColorScheme): FilmPalette {
            val lh = LiveHandshakePalette.from(scheme)
            val dark = scheme.surface.luminance() < 0.5f
            return FilmPalette(
                bg0 = lh.bg0,
                bg1 = lh.bg1,
                primary = lh.primary,
                // The second light: the scheme's tertiary on a dark scheme; on a light one, a step from
                // the inverse primary toward the inverse surface's ink, so it still reads on the night.
                secondary = if (dark) scheme.tertiary else lerp(scheme.inversePrimary, scheme.inverseOnSurface, 0.35f),
                accent = lh.accent,
                ink = lh.ink,
                muted = lh.muted,
                exit = scheme.background,
            )
        }
    }
}

/** The compiled field: one [RuntimeShader] per film, compiled when the film starts. */
class FilmField internal constructor(val shader: RuntimeShader) {
    val brush = ShaderBrush(shader)
}

@Composable
fun rememberFilmField(): FilmField {
    val context = LocalContext.current
    return remember { FilmField(RuntimeShader(readRawText(context, R.raw.splash_film_field))) }
}

@Composable
fun rememberFilmPalette(): FilmPalette {
    val scheme = MaterialTheme.colorScheme
    return remember(scheme) { FilmPalette.from(scheme) }
}

/**
 * Draws the film field over the whole draw area (`res/raw/splash_film_field.agsl`, byte-identical to
 * the PC's SkSL). Every size inside the shader is dp x [px] (bd: splash-canvas-density-trap).
 *
 * @param center the style's focal point: the floor's vanishing point, the warp's origin, the net.
 * @param drive the build-up toward the film's beat, 0..1.
 * @param still reduced motion: every moving layer frozen at [t], no beat.
 */
fun DrawScope.drawFilmField(
    field: FilmField,
    style: FilmStyle,
    palette: FilmPalette,
    t: Float,
    px: Float,
    center: Offset,
    drive: Float,
    beat: FilmBeat = FilmBeat.None,
    still: Boolean = false,
) {
    val s = field.shader
    s.setFloatUniform("uRes", size.width, size.height)
    s.setFloatUniform("uTime", t)
    s.setFloatUniform("uPx", px)
    s.setFloatUniform("uCenter", center.x, center.y)
    s.setFloatUniform("uBg0", palette.bg0.red, palette.bg0.green, palette.bg0.blue)
    s.setFloatUniform("uBg1", palette.bg1.red, palette.bg1.green, palette.bg1.blue)
    s.setFloatUniform("uPri", palette.primary.red, palette.primary.green, palette.primary.blue)
    s.setFloatUniform("uSec", palette.secondary.red, palette.secondary.green, palette.secondary.blue)
    s.setFloatUniform("uAcc", palette.accent.red, palette.accent.green, palette.accent.blue)
    s.setFloatUniform("uStyle", style.id)
    if (still || beat.strength <= 0f) s.setFloatUniform("uBeat", 0f, 0f, 0f, 0f)
    else s.setFloatUniform("uBeat", beat.x, beat.y, beat.at, beat.strength)
    s.setFloatUniform("uDrive", drive.coerceIn(0f, 1f))
    s.setFloatUniform("uStill", if (still) 1f else 0f)
    s.setFloatUniform("uAlpha", 1f)
    drawRect(field.brush)
}

/** Reduced motion: how long a film's designed still frame holds before the app takes over. */
const val FilmStillHoldMs = 1350L

/** The hero moment each film's still frame freezes: its clock and its build-up. */
internal fun filmStillPose(style: FilmStyle): Pair<Float, Float> = when (style) {
    FilmStyle.Command -> 1.6f to 0.55f
    FilmStyle.Cosmic -> 1.2f to 0.35f
    FilmStyle.Pong -> 1.0f to 0.7f
}

/**
 * The designed still frame a film shows under reduced motion (animator duration scale 0) instead of
 * the film (RemEx-pp4cm.11): its world frozen at a hero moment with the settled mark and wordmark,
 * held for [FilmStillHoldMs] and then handed to the app. No camera moves, no beat, nothing travels;
 * a tap hands off at once. Replaces the old frame that finished immediately, which read as a blank.
 */
@Composable
fun SplashFilmStill(
    style: FilmStyle,
    onFinished: () -> Unit,
    skipRequested: Boolean,
    onSkipConsumed: () -> Unit,
) {
    val field = rememberFilmField()
    val palette = rememberFilmPalette()
    val density = LocalDensity.current
    val textMeasurer = rememberTextMeasurer(cacheSize = 2)
    val wordmark = remember {
        textMeasurer.measure(SplashBrand.remExAnnotated(), TextStyle(fontFamily = SplashBrand.VictorMonoBold, fontSize = 40.sp))
    }
    var done by remember { mutableStateOf(false) }
    fun finishOnce() {
        if (done) return
        done = true
        onFinished()
    }
    LaunchedEffect(Unit) {
        delay(FilmStillHoldMs)
        finishOnce()
    }
    LaunchedEffect(skipRequested) {
        if (skipRequested) {
            finishOnce()
            onSkipConsumed()
        }
    }
    Canvas(modifier = Modifier.fillMaxSize()) {
        val px = density.density
        val cx = size.width / 2f
        val cy = size.height / 2f
        val (heroT, heroDrive) = filmStillPose(style)
        val markSize = size.minDimension * 0.62f
        val markCy = cy - markSize * 0.28f
        val focus = if (style == FilmStyle.Command) Offset(cx, size.height * 0.56f) else Offset(cx, markCy)
        drawFilmField(field, style, palette, heroT, px, focus, heroDrive, still = true)
        with(SplashBrand) { drawRemexIcon(center = Offset(cx, markCy), sizePx = markSize) }
        drawText(
            wordmark,
            topLeft = Offset(cx - wordmark.size.width / 2f, markCy + markSize * 0.42f),
        )
    }
}
