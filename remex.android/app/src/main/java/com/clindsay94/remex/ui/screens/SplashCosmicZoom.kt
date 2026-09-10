package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
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
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import kotlin.math.PI
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * CosmicZoom splash variant. Starfield convergence → cinematic zoom into the hero terminal-window
 * brand mark → lightning-strike impact (with camera shake, elastic punch, chromatic bloom, white
 * flash) → "RemEx" wordmark + Command Center tagline reveal → exit fade.
 *
 * Colors are the fixed [SplashBrand] palette (no `MaterialTheme` reads) — the splash is a brand
 * moment rendered identically regardless of the active app theme. Motion/timing/easings are
 * unchanged from the original CosmicZoom; only the hero art, wordmark, and colors were rebranded.
 * The orchestrator hands down [skipRequested]; this variant runs the original skip fade then
 * [onFinished], calling [onSkipConsumed] last.
 */
@Composable
fun SplashCosmicZoom(onFinished: () -> Unit, skipRequested: Boolean, onSkipConsumed: () -> Unit) {
        // Reduce motion: no starfield/zoom choreography — static terminal frame, finish now.
        if (LocalReducedMotion.current) {
                SplashReducedMotionFrame(onFinished, skipRequested, onSkipConsumed)
                return
        }
        val view = LocalView.current
        var elapsed by remember { mutableStateOf(0f) }
        var completed by remember { mutableStateOf(false) }

        // Fixed brand palette comes from SplashBrand (no MaterialTheme reads) — the splash renders
        // identically regardless of the active app theme. Roles: backdrop = diagonal gradient,
        // HUD rings/crosshair/accents = Amber, primary text = OffWhite, muted text = SlateLo.

        // Text Measurement
        val density = LocalDensity.current
        val textMeasurer = rememberTextMeasurer(cacheSize = 16)
        // Raw px-per-dp for canvas art drawn in fixed unit space (the hero brand mark and its
        // companion offsets): dp-sized text scales with density but raw px paths don't, so
        // without this factor the mark renders ~3x too small next to its text on real phones.
        val pixelDensity = density.density

        // Wordmark lockup: two-color "RemEx" in Victor Mono Bold, drawn below the settled hero.
        // Measured once (opacity 1f) purely for geometry; the per-frame draw bakes the fade opacity
        // into the annotated colors via SplashBrand.remExAnnotated(opacity).
        val remExWordmarkStyle = remember {
                TextStyle(fontFamily = SplashBrand.VictorMonoBold, fontSize = 40.sp)
        }
        val remExMeasured = remember(remExWordmarkStyle) {
                textMeasurer.measure(SplashBrand.remExAnnotated(), remExWordmarkStyle)
        }

        // Tagline beneath the wordmark — Victor Mono Bold + muted slate, wording unchanged.
        val cosmicSubFontSize = with(density) { 11.dp.toSp() }
        val cosmicSubTracking = with(density) { 1.dp.toSp() }
        val cosmicSubStyle = remember(cosmicSubFontSize, cosmicSubTracking) {
                TextStyle(
                        color = SplashBrand.SlateLo,
                        fontSize = cosmicSubFontSize,
                        fontWeight = FontWeight.Medium,
                        fontFamily = SplashBrand.VictorMonoBold,
                        letterSpacing = cosmicSubTracking
                )
        }

        // 14.dp.toSp() — shared chrome (version + skip hint)
        val tagFontSize = with(density) { 14.dp.toSp() }
        val tagTracking = with(density) { 3.dp.toSp() }
        val tagStyle = remember(tagFontSize, tagTracking) {
                TextStyle(
                        color = SplashBrand.OffWhite.copy(alpha = 0.7f),
                        fontSize = tagFontSize,
                        fontWeight = FontWeight.Light,
                        fontFamily = SplashBrand.VictorMonoBold,
                        letterSpacing = tagTracking
                )
        }

        val cmdCenterStr = stringResource(R.string.splash_command_center)
        val skipStr = stringResource(R.string.splash_tap_to_skip)
        val versionStr = "v${BuildConfig.VERSION_NAME}"

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
                                .background(SplashBrand.BackdropStart)
                                .graphicsLayer { alpha = skipAlpha.value },
                contentAlignment = Alignment.Center
        ) {
                Canvas(modifier = Modifier.fillMaxSize()) {
                        val width = size.width
                        val height = size.height
                        val cx = width / 2f
                        val cy = height / 2f

                        // ── COSMIC ZOOM ANIMATION ──

                        // Fixed brand backdrop: full-bleed diagonal gradient (replaces the themed fill).
                        drawRect(brush = SplashBrand.backdropBrush(size))

                        // Draw Cosmic Starfield
                        drawCosmicZoomStarfield(particles)

                        // Target rings / circular HUD lines in background
                        val radColor = SplashBrand.Amber.copy(alpha = 0.05f)
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
                                        color = SplashBrand.Amber.copy(alpha = shockOpacity * 0.6f),
                                        radius = shockRadius,
                                        center = Offset(cx, cy),
                                        style = Stroke(width = (3f + (1f - waveT) * 8f).dp.toPx())
                                )
                                // Second gold ring, layered under the brand-amber one above — part of the
                                // splash's fixed, theme-independent palette (see SplashBrand.kt header).
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
                        val restScale = 4.0f
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

                        // Draw the terminal-window brand mark as the hero, using the SAME transform
                        // (scale / shudder / punch / position) the old "R" logo used, so the strike's
                        // camera shake and elastic pop carry over unchanged. pixelDensity keeps the
                        // px-space mark proportional to the dp-sized wordmark below it.
                        val heroScale = zoomScaleVal * pixelDensity
                        // The 108-unit icon center (54,54) maps to (cx + shudderX,
                        // cy + yOffset*scale + shudderY) with yOffset = -30, so the mark lands
                        // centered with the impact shudder. The terminal icon stays the hero the
                        // whole time — no lightning bolt.
                        val heroCenter = Offset(cx + shudderX, cy - 30f * heroScale + shudderY)
                        with(SplashBrand) {
                                drawRemexIcon(
                                        center = heroCenter,
                                        sizePx = 108f * heroScale,
                                        opacity = 1f
                                )
                        }

                        // Icon-above-wordmark lockup: the terminal icon (hero, above) settles, then the
                        // two-color "RemEx" wordmark and its tagline fade + rise in below it — reusing
                        // the exact fade+rise stagger the old 3-line REMOTE / EXECUTION / Command Center
                        // reveal used (FastOutSlowIn over 0.35s, 12dp rise; wordmark at 1.85s, tagline
                        // at 2.15s, so the last element still settles right as the exit fade begins).
                        if (elapsed > 1.8f) {
                                fun lineIn(start: Float) =
                                        FastOutSlowInEasing.transform(
                                                ((elapsed - start) / 0.35f).coerceIn(0f, 1f)
                                        )
                                val wordmarkIn = lineIn(1.85f)
                                val taglineIn = lineIn(2.15f)
                                val rise = 12.dp.toPx()

                                val wordmarkW = remExMeasured.size.width.toFloat()
                                val wordmarkH = remExMeasured.size.height.toFloat()
                                val taglineW = commandCenterCosmicMeasured.size.width.toFloat()

                                val wordmarkX = cx - wordmarkW / 2f
                                val wordmarkY = cy + 50.dp.toPx()
                                val taglineX = cx - taglineW / 2f
                                val taglineY = wordmarkY + wordmarkH * 1.15f

                                // Wordmark: bake the fade opacity into the two brand colors so both
                                // "Rem" (off-white) and "Ex" (amber) dim together and stay on-brand.
                                drawText(
                                        textMeasurer,
                                        SplashBrand.remExAnnotated(opacity = wordmarkIn),
                                        topLeft = Offset(wordmarkX, wordmarkY + (1f - wordmarkIn) * rise),
                                        style = remExWordmarkStyle
                                )
                                drawText(
                                        commandCenterCosmicMeasured,
                                        color = SplashBrand.SlateLo.copy(alpha = taglineIn),
                                        topLeft = Offset(taglineX, taglineY + (1f - taglineIn) * rise)
                                )
                        }

                        // Softened full-screen white arrival flash on impact (no longer a lightning strike).
                        if (flashOverlayVal > 0f) {
                                drawRect(
                                        color = Color.White.copy(alpha = flashOverlayVal * 0.4f),
                                        size = size
                                )
                        }

                        // Chromatic-aberration bloom shockwave on impact: three RGB-split
                        // rings burst outward from the logo and fade in ~0.25s. The red/cyan/green
                        // split is the effect itself, so these three colors are fixed by design —
                        // part of the splash's theme-independent palette (see SplashBrand.kt header).
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
                                        color = SplashBrand.BackdropStart.copy(alpha = fadeOverlayVal),
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
                                        color = SplashBrand.OffWhite.copy(alpha = versionT * 0.4f),
                                        topLeft = Offset((width - versionMeasured.size.width) / 2f, height - 32.dp.toPx() - versionMeasured.size.height)
                                )
                        }

                        // Tap to skip hint at 0.8s
                        val skipT = ((elapsed - 0.8f) / 0.5f).coerceIn(0f, 1f)
                        if (skipT > 0f && !isSkipping) {
                                drawText(
                                        skipMeasured,
                                        color = SplashBrand.OffWhite.copy(alpha = skipT * 0.5f),
                                        topLeft = Offset((width - skipMeasured.size.width) / 2f, height - 56.dp.toPx() - skipMeasured.size.height)
                                )
                        }
                }
        }
}
