package com.clindsay94.remex.ui.theme

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.RuntimeShader
import android.os.PowerManager
import android.util.Log
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageShader
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.clindsay94.remex.data.BackgroundMotionPolicy
import com.clindsay94.remex.data.BackgroundStyles
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private const val TAG = "AppBackground"

/**
 * The real `background` colour while [SeeThroughBackground] has made it transparent, or null when
 * nothing has. Anything that has to paint a solid backdrop of its own (the remote desktop stream,
 * the splash, the preview swatches) reads this through [opaqueBackgroundColor].
 */
internal val LocalOpaqueBackground = staticCompositionLocalOf<Color?> { null }

/** The theme's solid background colour, even inside a [SeeThroughBackground] subtree. */
@Composable
internal fun opaqueBackgroundColor(): Color = LocalOpaqueBackground.current ?: MaterialTheme.colorScheme.background

/**
 * Lets the app background layer show through. A `Scaffold` (and anything else defaulting to
 * `colorScheme.background`) paints transparent inside this, so the layer behind the nav host shows
 * between cards, while cards and other surfaces keep their own opaque container colours above it.
 * With [active] false (background None) the scheme passes through unchanged, so no screen looks any
 * different. The wrapper is composed either way, so picking or clearing a background never changes
 * the composition's shape and never resets a screen's state (a Settings pane open on Personalize
 * stays open).
 */
@Composable
fun SeeThroughBackground(active: Boolean, content: @Composable () -> Unit) {
    val scheme = MaterialTheme.colorScheme
    val solid = scheme.background
    val effective = if (active) scheme.copy(background = Color.Transparent) else scheme
    CompositionLocalProvider(LocalOpaqueBackground provides (if (active) solid else null)) {
        MaterialTheme(
            colorScheme = effective,
            shapes = MaterialTheme.shapes,
            typography = MaterialTheme.typography,
            motionScheme = MaterialTheme.motionScheme,
            content = content,
        )
    }
}

/**
 * Puts the solid background back for a subtree that must never show the layer (the remote desktop
 * stream, the splash, the full-screen modals). A pass-through outside a [SeeThroughBackground].
 */
@Composable
fun OpaqueBackgroundRoute(content: @Composable () -> Unit) {
    val solid = LocalOpaqueBackground.current
    val scheme = MaterialTheme.colorScheme
    val effective = if (solid != null) scheme.copy(background = solid) else scheme
    CompositionLocalProvider(LocalOpaqueBackground provides null) {
        MaterialTheme(
            colorScheme = effective,
            shapes = MaterialTheme.shapes,
            typography = MaterialTheme.typography,
            motionScheme = MaterialTheme.motionScheme,
            content = content,
        )
    }
}

/**
 * The app-level background (RemEx-pp4cm.17): the solid background colour with a [style] texture or
 * animation over it, filling its parent. Compose it once, behind the nav host. Draws nothing for
 * [BackgroundStyles.None], so it can be left in place unconditionally.
 *
 * [covered] says something opaque is on top (the remote desktop stream), so an animated style stops
 * spending frames on pixels nobody can see.
 */
@Composable
fun AppBackgroundLayer(style: String, intensity: Float, modifier: Modifier = Modifier, covered: Boolean = false) {
    val effective = BackgroundStyles.effective(style)
    if (effective == BackgroundStyles.None) return
    BackgroundCanvas(effective, intensity, covered, modifier.fillMaxSize())
}

/** One style drawn into [modifier]'s box, for the layer and for the picker's swatches. */
@Composable
internal fun BackgroundCanvas(style: String, intensity: Float, covered: Boolean, modifier: Modifier) {
    // Inside a see-through subtree the scheme's background is transparent; the layer paints and
    // measures against the real one.
    val solid = opaqueBackgroundColor()
    val themed = MaterialTheme.colorScheme
    val scheme = if (themed.background == solid) themed else themed.copy(background = solid)
    val colors = BackgroundPalette.colors(style, scheme)
    val alpha = BackgroundPalette.layerAlpha(style, intensity, scheme)
    Box(modifier.background(solid)) {
        if (BackgroundStyles.isAnimated(style)) {
            AnimatedBackground(style, colors, alpha, covered)
        } else {
            StaticBackground(style, colors, alpha)
        }
    }
}

@Composable
private fun StaticBackground(style: String, colors: List<Color>, alpha: Float) {
    val density = LocalDensity.current.density
    // Rasterised off the main thread once per style, colour set and density; the opacity is applied
    // at draw time, so dragging the intensity slider never re-renders it.
    val tile by produceState<ShaderBrush?>(null, style, colors, density) {
        value = withContext(Dispatchers.Default) {
            ShaderBrush(
                ImageShader(BackgroundTiles.render(style, colors, density), TileMode.Repeated, TileMode.Repeated),
            )
        }
    }
    Box(
        Modifier.fillMaxSize().drawBehind {
            tile?.let { drawRect(it, alpha = alpha) }
        },
    )
}

@Composable
private fun AnimatedBackground(style: String, colors: List<Color>, alpha: Float, covered: Boolean) {
    val appVisible = rememberAppVisible()
    val reducedMotion = LocalReducedMotion.current
    val batterySaver = rememberBatterySaver()
    val animating = BackgroundMotionPolicy.shouldAnimate(style, appVisible, reducedMotion, batterySaver, covered)

    val shader = remember(style) {
        val source = BackgroundShaders.source(style)
        if (source == null) {
            null
        } else {
            try {
                RuntimeShader(source)
            } catch (e: RuntimeException) {
                Log.w(TAG, "background shader for $style did not compile; drawing nothing", e)
                null
            }
        }
    }
    val brush = remember(shader) { shader?.let { ShaderBrush(it) } }

    // The shader's clock. It is read only inside the draw lambda below, so a tick redraws this one
    // node and recomposes nothing. A stopped background keeps its last value: a still frame, never blank.
    var seconds by remember { mutableFloatStateOf(BackgroundMotionPolicy.STILL_FRAME_SECONDS) }
    LaunchedEffect(animating) {
        if (!animating) return@LaunchedEffect
        val startSeconds = seconds
        var origin = Long.MIN_VALUE
        var last = Long.MIN_VALUE
        while (true) {
            androidx.compose.runtime.withFrameNanos { now ->
                if (origin == Long.MIN_VALUE) origin = now
                if (BackgroundMotionPolicy.frameDue(now, last)) {
                    last = now
                    seconds = (startSeconds + (now - origin) / 1_000_000_000f) % CLOCK_WRAP_SECONDS
                }
            }
        }
    }

    Box(
        Modifier.fillMaxSize().drawBehind {
            val s = shader ?: return@drawBehind
            val b = brush ?: return@drawBehind
            s.setFloatUniform("uSize", size.width, size.height)
            s.setFloatUniform("uScale", density)
            s.setFloatUniform("uTime", seconds)
            for ((name, index) in SHADER_COLOR_UNIFORMS) {
                val c = colors.getOrElse(index) { colors.last() }
                s.setFloatUniform(name, c.red, c.green, c.blue)
            }
            drawRect(b, alpha = alpha)
        },
    )
}

private val SHADER_COLOR_UNIFORMS = listOf("uA" to 0, "uB" to 1, "uC" to 2)

/** Long enough that nobody sees the loop, short enough to keep float precision in the shader. */
private const val CLOCK_WRAP_SECONDS = 3600f

/** True while the app is on screen (STARTED or later), so a stopped or backgrounded app stops animating. */
@Composable
private fun rememberAppVisible(): Boolean {
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val state by lifecycle.currentStateFlow.collectAsState()
    return state.isAtLeast(Lifecycle.State.STARTED)
}

/** True while Battery Saver is on, kept current by the system broadcast. */
@Composable
private fun rememberBatterySaver(): Boolean {
    val context = LocalContext.current
    val powerManager = remember(context) { context.getSystemService(PowerManager::class.java) }
    var saving by remember(powerManager) { mutableStateOf(powerManager?.isPowerSaveMode == true) }
    DisposableEffect(context, powerManager) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context?, intent: Intent?) {
                saving = powerManager?.isPowerSaveMode == true
            }
        }
        ContextCompat.registerReceiver(
            context,
            receiver,
            IntentFilter(PowerManager.ACTION_POWER_SAVE_MODE_CHANGED),
            ContextCompat.RECEIVER_NOT_EXPORTED,
        )
        saving = powerManager?.isPowerSaveMode == true
        onDispose { context.unregisterReceiver(receiver) }
    }
    return saving
}
