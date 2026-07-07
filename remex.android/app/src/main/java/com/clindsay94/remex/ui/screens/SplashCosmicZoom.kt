package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.R
import kotlin.math.PI
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * CosmicZoom splash variant. Starfield convergence → cinematic zoom into the hero "R" →
 * lightning-strike impact (with camera shake, elastic punch, chromatic bloom, white flash)
 * → "REMOTE EXECUTION / Command Center" title reveal → exit fade.
 *
 * Extracted verbatim from the former monolithic SplashScreen.kt (behavior-identical).
 * Owns only the state its code path used. The orchestrator hands down [skipRequested];
 * this variant runs the original skip fade then [onFinished], calling [onSkipConsumed] last.
 */
@Composable
fun SplashCosmicZoom(onFinished: () -> Unit, skipRequested: Boolean, onSkipConsumed: () -> Unit) {
        val view = LocalView.current
        var elapsed by remember { mutableStateOf(0f) }
        var completed by remember { mutableStateOf(false) }

        // Colors from theme
        val background = MaterialTheme.colorScheme.background
        val substrateColor = background.copy(alpha = 1f)
        val onBackground = MaterialTheme.colorScheme.onBackground
        val primary = MaterialTheme.colorScheme.primary
        // Icy accent reads well on dark backgrounds; on light themes fall back to onBackground
        // so the CosmicZoom subtitle stays legible (the splash renders in both themes).
        val cosmicSubColor = if (background.luminance() < 0.5f) Color(0xFFDCF0FF) else onBackground

        // Text Measurement
        val density = LocalDensity.current
        val textMeasurer = rememberTextMeasurer(cacheSize = 16)
        // Raw px-per-dp for canvas art drawn in fixed unit space (the R-logo path and its
        // companion offsets): dp-sized text scales with density but raw px paths don't, so
        // without this factor the logo renders ~3x too small next to its text on real phones.
        val pixelDensity = density.density

        // 38.dp.toSp()
        val cosmicTitleFontSize = with(density) { 38.dp.toSp() }
        val cosmicTitleTracking = with(density) { 4.dp.toSp() }
        val cosmicTitleStyle = remember(cosmicTitleFontSize, cosmicTitleTracking, onBackground) {
                TextStyle(
                        color = onBackground,
                        fontSize = cosmicTitleFontSize,
                        fontWeight = FontWeight.Black,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = cosmicTitleTracking
                )
        }
        val cosmicAccentStyle = remember(cosmicTitleStyle, primary) { cosmicTitleStyle.copy(color = primary) }

        // 11.dp.toSp()
        val cosmicSubFontSize = with(density) { 11.dp.toSp() }
        val cosmicSubTracking = with(density) { 1.dp.toSp() }
        val cosmicSubStyle = remember(cosmicSubFontSize, cosmicSubTracking, cosmicSubColor) {
                TextStyle(
                        color = cosmicSubColor,
                        fontSize = cosmicSubFontSize,
                        fontWeight = FontWeight.Medium,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = cosmicSubTracking
                )
        }

        // 14.dp.toSp() — shared chrome (version + skip hint)
        val tagFontSize = with(density) { 14.dp.toSp() }
        val tagTracking = with(density) { 3.dp.toSp() }
        val tagStyle = remember(tagFontSize, tagTracking, onBackground) {
                TextStyle(
                        color = onBackground.copy(alpha = 0.7f),
                        fontSize = tagFontSize,
                        fontWeight = FontWeight.Light,
                        fontFamily = FontFamily.SansSerif,
                        letterSpacing = tagTracking
                )
        }

        val cmdCenterStr = stringResource(R.string.splash_command_center)
        val skipStr = stringResource(R.string.splash_tap_to_skip)
        val versionStr = "v${BuildConfig.VERSION_NAME}"

        val remoteCosmicMeasured = remember(cosmicTitleStyle) { textMeasurer.measure("REMOTE", cosmicTitleStyle) }
        val executionCosmicMeasured = remember(cosmicAccentStyle) { textMeasurer.measure("EXECUTION", cosmicAccentStyle) }
        val commandCenterCosmicMeasured = remember(cosmicSubStyle, cmdCenterStr) { textMeasurer.measure(cmdCenterStr, cosmicSubStyle) }
        val skipMeasured = remember(tagStyle, skipStr) { textMeasurer.measure(skipStr, tagStyle) }
        val versionMeasured = remember(tagStyle, versionStr) { textMeasurer.measure(versionStr, tagStyle) }

        // Cosmic starfield particles
        val particles = remember {
                val rng = java.util.Random(42L)
                List(25) {
                        Particle(
                                x = rng.nextFloat(),
                                y = rng.nextFloat(),
                                vx = (rng.nextFloat() - 0.5f) * 0.005f,
                                vy = -(0.01f + rng.nextFloat() * 0.02f),
                                lifetime = rng.nextFloat() * 2f,
                                maxLifetime = 2f + rng.nextFloat() * 2f,
                                alpha = 0f
                        )
                }
        }

        var particleFrame by remember { mutableStateOf(0) }
        var isSkipping by remember { mutableStateOf(false) }
        val skipAlpha = remember { Animatable(1f) }

        fun finishOnce() {
                if (completed) return
                completed = true
                onFinished()
        }

        // Skip: fade then finish (ports the monolith's isSkipping/skipAlpha/skipSplash logic).
        LaunchedEffect(skipRequested) {
                if (!skipRequested || isSkipping || completed) return@LaunchedEffect
                isSkipping = true
                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                skipAlpha.animateTo(0f, tween(200, easing = FastOutSlowInEasing))
                finishOnce()
                onSkipConsumed()
        }

        // Haptic "thud" fired once, synced to the CosmicZoom lightning strike at elapsed ≈ 1.8s.
        LaunchedEffect(Unit) {
                snapshotFlow { elapsed >= 1.8f }.first { it }
                if (!isSkipping) {
                        view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                }
        }

        LaunchedEffect(Unit) {
                // Frame-clock-driven update: real dt, main thread (no races), native display rate.
                launch {
                        val rng = java.util.Random(99L)
                        var lastNanos = 0L
                        while (!isSkipping && !completed) {
                                withFrameNanos { now ->
                                        val dt = if (lastNanos == 0L) 0.016f
                                                 else ((now - lastNanos) / 1_000_000_000f).coerceAtMost(0.05f)
                                        lastNanos = now

                                        // Update particles for Cosmic Starfield!
                                        for (p in particles) {
                                                val dx = p.x - 0.5f
                                                val dy = p.y - 0.5f
                                                var dist = kotlin.math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
                                                if (dist < 0.01f) dist = 0.01f

                                                val speed = (0.02f + dist * 0.8f) * dt * 60f
                                                p.x += (dx / dist) * speed
                                                p.y += (dy / dist) * speed

                                                p.alpha = (dist * 3.0f).coerceIn(0f, 0.9f) * ((0.5f - dist) * 4.0f).coerceIn(0f, 1.0f)

                                                if (p.x < 0f || p.x > 1f || p.y < 0f || p.y > 1f || dist > 0.5f) {
                                                        val angle = rng.nextFloat() * Math.PI * 2
                                                        val startDist = 0.01f + rng.nextFloat() * 0.06f
                                                        p.x = (0.5f + kotlin.math.cos(angle) * startDist).toFloat()
                                                        p.y = (0.5f + kotlin.math.sin(angle) * startDist).toFloat()
                                                        p.vx = kotlin.math.cos(angle).toFloat() * 0.1f
                                                        p.vy = kotlin.math.sin(angle).toFloat() * 0.1f
                                                        p.lifetime = 0f
                                                        p.alpha = 0f
                                                }
                                        }
                                        particleFrame++
                                        elapsed += dt
                                }
                        }
                }

                // 100ms past the 3.0s exit-fade completion so a dropped frame can't
                // start navigation with the fade still visibly short of 100%.
                delay(3100L)
                if (!isSkipping) {
                        finishOnce()
                }
        }

        Box(
                modifier =
                        Modifier.fillMaxSize()
                                .background(substrateColor)
                                .graphicsLayer { alpha = skipAlpha.value },
                contentAlignment = Alignment.Center
        ) {
                Canvas(modifier = Modifier.fillMaxSize()) {
                        val width = size.width
                        val height = size.height
                        val cx = width / 2f
                        val cy = height / 2f

                        // ── COSMIC ZOOM ANIMATION ──

                        // Draw Cosmic Starfield
                        drawCosmicZoomStarfield(particles)

                        // Target rings / circular HUD lines in background
                        val radColor = primary.copy(alpha = 0.05f)
                        val maxRad = kotlin.math.min(width, height) * 0.45f
                        for (rf in listOf(0.25f, 0.45f, 0.65f, 0.85f)) {
                                drawCircle(
                                        color = radColor,
                                        radius = rf * maxRad,
                                        center = Offset(cx, cy),
                                        style = Stroke(width = 1.dp.toPx())
                                )
                        }

                        // Draw center crosshair target
                        drawLine(
                                color = radColor,
                                start = Offset(cx - 20.dp.toPx(), cy),
                                end = Offset(cx + 20.dp.toPx(), cy),
                                strokeWidth = 1.dp.toPx()
                        )
                        drawLine(
                                color = radColor,
                                start = Offset(cx, cy - 20.dp.toPx()),
                                end = Offset(cx, cy + 20.dp.toPx()),
                                strokeWidth = 1.dp.toPx()
                        )

                        // Draw expanding energy shockwave circles during lightning strike
                        if (elapsed > 1.8f) {
                                val waveT = ((elapsed - 1.8f) / 0.5f).coerceIn(0f, 1f)
                                val shockRadius = (40f + waveT * 280f).dp.toPx()
                                val shockOpacity = 1f - waveT

                                drawCircle(
                                        color = primary.copy(alpha = shockOpacity * 0.6f),
                                        radius = shockRadius,
                                        center = Offset(cx, cy),
                                        style = Stroke(width = (3f + (1f - waveT) * 8f).dp.toPx())
                                )
                                drawCircle(
                                        color = Color(0xFFFFD700).copy(alpha = shockOpacity * 0.3f),
                                        radius = shockRadius * 0.7f,
                                        center = Offset(cx, cy),
                                        style = Stroke(width = (1.5f + (1f - waveT) * 4f).dp.toPx())
                                )
                        }

                        // Cinematic Zoom-In stages:
                        val zoomScaleVal: Float
                        val shudderX: Float
                        val shudderY: Float
                        val flashOverlayVal: Float

                        // Resting size of the hero "R" — bumped up for real presence.
                        val restScale = 3.4f
                        if (elapsed < 1.8f) {
                                val t = elapsed / 1.8f
                                // Ease-in zoom toward just under rest; the strike pops it the rest of the way.
                                zoomScaleVal = 0.1f + t * t * (restScale - 0.3f)
                                shudderX = 0f
                                shudderY = 0f
                                flashOverlayVal = 0f
                        } else {
                                val strikeElapsed = elapsed - 1.8f
                                val strikeDuration = 0.6f
                                flashOverlayVal = if (strikeElapsed < strikeDuration) 1f - (strikeElapsed / strikeDuration) else 0f

                                // Punchier camera shake on impact.
                                val shakeIntensity = if (strikeElapsed < strikeDuration) (1f - (strikeElapsed / strikeDuration)) * 30f else 0f
                                val rng = java.util.Random((elapsed * 1000f).toLong())
                                shudderX = if (shakeIntensity > 0f) (rng.nextFloat() - 0.5f) * shakeIntensity.dp.toPx() else 0f
                                shudderY = if (shakeIntensity > 0f) (rng.nextFloat() - 0.5f) * shakeIntensity.dp.toPx() else 0f

                                // Impact "pop": big elastic overshoot above rest then settle, plus breathing.
                                val punch = if (strikeElapsed < 0.32f)
                                        kotlin.math.sin((strikeElapsed / 0.32f) * PI.toFloat()) * 0.85f
                                else 0f
                                zoomScaleVal = restScale + punch + kotlin.math.sin((elapsed - 1.8f) * 3f) * 0.05f
                        }

                        // Draw Zoomed Brand Logo in center (Applying global scale & shudder displacement)
                        // pixelDensity keeps the px-space logo proportional to dp-sized text.
                        drawStylizedRLogo(
                                w = width,
                                h = height,
                                scale = zoomScaleVal * pixelDensity,
                                accentColor = primary,
                                opacity = 1.0f,
                                lightningFade = if (elapsed > 1.8f) ((elapsed - 1.8f) / 0.5f).coerceIn(0f, 1f) else 0f,
                                elapsed = elapsed,
                                shudderX = shudderX,
                                shudderY = shudderY,
                                yOffset = -30f
                        )

                        // Fading title text "REMOTE EXECUTION" — staggered line entrance
                        // (fade + 12dp rise per line, 150ms apart; the last line settles at
                        // 2.5s, exactly when the exit fade begins).
                        if (elapsed > 1.8f) {
                                fun lineIn(start: Float) =
                                        FastOutSlowInEasing.transform(
                                                ((elapsed - start) / 0.35f).coerceIn(0f, 1f)
                                        )
                                val remoteIn = lineIn(1.85f)
                                val executionIn = lineIn(2.0f)
                                val subIn = lineIn(2.15f)
                                val rise = 12.dp.toPx()

                                val remoteW = remoteCosmicMeasured.size.width.toFloat()
                                val remoteH = remoteCosmicMeasured.size.height.toFloat()
                                val executionW = executionCosmicMeasured.size.width.toFloat()
                                val executionH = executionCosmicMeasured.size.height.toFloat()
                                val subW = commandCenterCosmicMeasured.size.width.toFloat()

                                val remoteX = cx - remoteW / 2f
                                val remoteY = cy + 50.dp.toPx()
                                val executionX = cx - executionW / 2f
                                val executionY = remoteY + remoteH * 1.05f
                                val subX = cx - subW / 2f
                                val subY = executionY + executionH * 1.1f

                                drawText(
                                        remoteCosmicMeasured,
                                        color = onBackground.copy(alpha = remoteIn),
                                        topLeft = Offset(remoteX, remoteY + (1f - remoteIn) * rise)
                                )
                                drawText(
                                        executionCosmicMeasured,
                                        color = primary.copy(alpha = executionIn),
                                        topLeft = Offset(executionX, executionY + (1f - executionIn) * rise)
                                )
                                drawText(
                                        commandCenterCosmicMeasured,
                                        color = cosmicSubColor.copy(alpha = subIn * 0.6f),
                                        topLeft = Offset(subX, subY + (1f - subIn) * rise)
                                )
                        }

                        // Full-screen White Screen Flash overlay during lightning strike
                        if (flashOverlayVal > 0f) {
                                drawRect(
                                        color = Color(0xFFE6F8FF).copy(alpha = flashOverlayVal),
                                        size = size
                                )
                        }

                        // Chromatic-aberration bloom shockwave on impact: three RGB-split
                        // rings burst outward from the logo and fade in ~0.25s.
                        val bloomT = (elapsed - 1.8f) / 0.25f
                        if (bloomT in 0f..1f) {
                                val bloomAlpha = (1f - bloomT) * 0.55f
                                val bloomRadius = 40.dp.toPx() + bloomT * 360.dp.toPx()
                                val split = (1f - bloomT) * 16.dp.toPx()
                                val bloomStroke = Stroke(width = (2f + (1f - bloomT) * 7f).dp.toPx())
                                val bloomCy = cy - 30f * restScale
                                drawCircle(
                                        color = Color(0xFFFF2D55).copy(alpha = bloomAlpha),
                                        radius = bloomRadius,
                                        center = Offset(cx - split, bloomCy),
                                        style = bloomStroke
                                )
                                drawCircle(
                                        color = Color(0xFF00E5FF).copy(alpha = bloomAlpha),
                                        radius = bloomRadius,
                                        center = Offset(cx + split, bloomCy),
                                        style = bloomStroke
                                )
                                drawCircle(
                                        color = Color(0xFF45FF8F).copy(alpha = bloomAlpha * 0.8f),
                                        radius = bloomRadius,
                                        center = Offset(cx, bloomCy - split),
                                        style = bloomStroke
                                )
                        }

                        // Fade overlay
                        val fadeOverlayVal = if (elapsed > 2.6f) {
                                ((elapsed - 2.6f) / 0.4f).coerceIn(0f, 1f)
                        } else {
                                0f
                        }
                        if (fadeOverlayVal > 0f) {
                                drawRect(
                                        color = background.copy(alpha = fadeOverlayVal),
                                        size = size
                                )
                        }

                        // ═════════════════════════════════════════════════════════════
                        // Text Hints (Version, Tap to Skip) — shared chrome, current positions
                        // ═════════════════════════════════════════════════════════════
                        // Version label at bottom center
                        val versionT = ((elapsed - 0.2f) / 0.5f).coerceIn(0f, 1f)
                        if (versionT > 0f) {
                                drawText(
                                        versionMeasured,
                                        color = onBackground.copy(alpha = versionT * 0.4f),
                                        topLeft = Offset((width - versionMeasured.size.width) / 2f, height - 32.dp.toPx() - versionMeasured.size.height)
                                )
                        }

                        // Tap to skip hint at 0.8s
                        val skipT = ((elapsed - 0.8f) / 0.5f).coerceIn(0f, 1f)
                        if (skipT > 0f && !isSkipping) {
                                drawText(
                                        skipMeasured,
                                        color = onBackground.copy(alpha = skipT * 0.5f),
                                        topLeft = Offset((width - skipMeasured.size.width) / 2f, height - 56.dp.toPx() - skipMeasured.size.height)
                                )
                        }
                }
        }
}
