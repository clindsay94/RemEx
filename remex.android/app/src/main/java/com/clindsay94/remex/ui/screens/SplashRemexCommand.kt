package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutLinearInEasing
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Paint
import androidx.compose.ui.graphics.PaintingStyle
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.materialShapesList
import androidx.graphics.shapes.Morph
import kotlin.math.hypot
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private class StreamParticle(var t: Float, var speed: Float, var radius: Float, var alpha: Float)

/**
 * A highly animated technical splash screen using Jetpack Compose Canvas — the default
 * ("RemexCommand") variant.
 *
 * Visual Stages:
 * 1. Substrate reveals: Circuit board traces and radial grids appear in the background.
 * 2. The Phone (source) emits a "Scan" radar (amber).
 * 3. The Scan reveals the "Wireframe" of the system (Monitor, Phone, and partial text).
 * 4. The Monitor (target) emits a "Wave" radar (off-white).
 * 5. The Wave reveals the "Solid" surfaces, the connection stream, and the full "RemEx" brand.
 * 6. Connection Established: Energy flows from Phone to Monitor.
 * 7. Final Transition: The camera pulls into the Monitor screen to enter the app.
 *
 * Extracted verbatim from the former monolithic SplashScreen.kt (behavior-identical). The
 * orchestrator hands down [skipRequested]; this variant runs the original skip fade then
 * [onFinished], calling [onSkipConsumed] last.
 */
@Composable
fun SplashRemexCommand(onFinished: () -> Unit, skipRequested: Boolean, onSkipConsumed: () -> Unit) {
        val scope = rememberCoroutineScope()
        val view = LocalView.current
        var elapsed by remember { mutableStateOf(0f) }
        var completed by remember { mutableStateOf(false) }

        // ── Fixed brand palette — the splash is a brand moment, never theme-adaptive.
        // Ported 1:1 from the launcher icon via SplashBrand; ZERO MaterialTheme reads. ──
        val substrateColor = SplashBrand.WindowFill   // darkest base: device screens, box bg, fade target
        val onBackground = SplashBrand.OffWhite       // primary text
        val deviceLight = SplashBrand.SlateLo         // device outlines, stand, ambient embers
        val deviceDark = SplashBrand.SlateHi          // darker slate — ambient floating shapes
        val accent = SplashBrand.Amber                // scan reveal, connection energy, "Ex"/completion text
        val waveAccent = SplashBrand.OffWhite         // wave reveal glow (paired with its kept white edge)

        // Animation States
        val scanProgress = remember { Animatable(0f) }
        val waveProgress = remember { Animatable(0f) }
        val connectionGlow = remember { Animatable(0f) }
        val zoomScale = remember { Animatable(1f) }
        val zoomProgress = remember { Animatable(0f) }
        val fadeOverlay = remember { Animatable(0f) }

        // Dash/Stream offset
        val infiniteTransition = rememberInfiniteTransition(label = "stream")
        val streamOffset =
                infiniteTransition.animateFloat(
                        initialValue = 0f,
                        targetValue = 1f,
                        animationSpec =
                                infiniteRepeatable(animation = tween(1500, easing = LinearEasing)),
                        label = "offset"
                )

        // Text Measurement
        val density = LocalDensity.current
        val textMeasurer = rememberTextMeasurer(cacheSize = 16)
        // Raw px-per-dp for canvas art drawn in fixed unit space (the R-logo path and its
        // companion offsets): dp-sized text scales with density but raw px paths don't, so
        // without this factor the logo renders ~3x too small next to its text on real phones.
        val pixelDensity = density.density

        // 54.dp.toSp()
        val remFontSize = with(density) { 54.dp.toSp() }
        val remTracking = with(density) { 4.dp.toSp() }
        val remStyle = remember(remFontSize, remTracking, onBackground) {
                TextStyle(
                        color = onBackground,
                        fontSize = remFontSize,
                        fontWeight = FontWeight.Black,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = remTracking
                )
        }
        val exStyle = remember(remStyle, accent) { remStyle.copy(color = accent) }

        // 24.dp.toSp()
        val completionFontSize = with(density) { 24.dp.toSp() }
        val completionTracking = with(density) { 1.dp.toSp() }
        val completionStyle = remember(completionFontSize, completionTracking, accent) {
                TextStyle(
                        color = accent.copy(alpha = 0.8f),
                        fontSize = completionFontSize,
                        fontWeight = FontWeight.Medium,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = completionTracking
                )
        }

        // 14.dp.toSp()
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

        val remMeasured = remember(remStyle) { textMeasurer.measure("REM", remStyle) }
        val emMeasured = remember(remStyle) { textMeasurer.measure("EM", remStyle) }
        val oteMeasured = remember(completionStyle) { textMeasurer.measure("ote", completionStyle) }
        val exMeasured = remember(exStyle) { textMeasurer.measure("EX", exStyle) }
        val ecuMeasured = remember(completionStyle) { textMeasurer.measure("ecution", completionStyle) }

        val cmdStr = stringResource(R.string.splash_command_your)
        val pcStr = stringResource(R.string.splash_pc)
        val skipStr = stringResource(R.string.splash_tap_to_skip)
        val versionStr = "v${BuildConfig.VERSION_NAME}"

        val commandMeasured = remember(tagStyle, cmdStr) { textMeasurer.measure(cmdStr, tagStyle) }
        val yourPcMeasured = remember(tagStyle, pcStr) { textMeasurer.measure(pcStr, tagStyle) }
        val skipMeasured = remember(tagStyle, skipStr) { textMeasurer.measure(skipStr, tagStyle) }
        val versionMeasured = remember(tagStyle, versionStr) { textMeasurer.measure(versionStr, tagStyle) }

        // Random background elements
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

        val floatingShapes = remember {
                val rng = java.util.Random(77L)
                val shapes = materialShapesList
                val colors = listOf(deviceLight, deviceDark, SplashBrand.WindowStroke)
                List(18) {
                        val startIdx = rng.nextInt(shapes.size)
                        var endIdx = rng.nextInt(shapes.size)
                        while (endIdx == startIdx) endIdx = rng.nextInt(shapes.size)

                        FloatingShape(
                                x = rng.nextFloat(),
                                y = rng.nextFloat(),
                                size = 0.06f + rng.nextFloat() * 0.12f,
                                vx = (rng.nextFloat() - 0.5f) * 0.002f,
                                vy = (rng.nextFloat() - 0.5f) * 0.002f,
                                morph = Morph(shapes[startIdx], shapes[endIdx]),
                                currentEndShape = shapes[endIdx],
                                morphProgress = rng.nextFloat(),
                                morphSpeed = 0.003f + rng.nextFloat() * 0.007f,
                                rotation = rng.nextFloat() * 360f,
                                rotationSpeed = (rng.nextFloat() - 0.5f) * 1.8f,
                                alpha = 0.08f + rng.nextFloat() * 0.12f,
                                color = colors[rng.nextInt(colors.size)]
                        )
                }
        }

        val streamParticles = remember {
                val rng = java.util.Random(111L)
                List(12) {
                        StreamParticle(
                                t = rng.nextFloat(),
                                speed = 0.008f + rng.nextFloat() * 0.012f,
                                radius = 1f + rng.nextFloat() * 2.5f,
                                alpha = 0.3f + rng.nextFloat() * 0.6f
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

                                        // Embers
                                        for (p in particles) {
                                                p.lifetime += dt
                                                p.x += p.vx * (dt * 60f)
                                                p.y += p.vy * dt
                                                val t = p.lifetime / p.maxLifetime
                                                p.alpha =
                                                        when {
                                                                t < 0.2f -> (t / 0.2f) * 0.7f
                                                                t < 0.8f -> 0.7f
                                                                else -> (1f - (t - 0.8f) / 0.2f) * 0.7f
                                                        }
                                                if (p.lifetime >= p.maxLifetime) {
                                                        p.x = rng.nextFloat()
                                                        p.y = 0.4f + rng.nextFloat() * 0.6f
                                                        p.vx = (rng.nextFloat() - 0.5f) * 0.016f
                                                        p.vy = -(0.02f + rng.nextFloat() * 0.02f)
                                                        p.lifetime = 0f
                                                        p.maxLifetime = 1.5f + rng.nextFloat() * 1.5f
                                                }
                                        }
                                        // Floating shapes morphing
                                        for (s in floatingShapes) {
                                                s.x += s.vx * (dt * 60f)
                                                s.y += s.vy * (dt * 60f)
                                                s.rotation += s.rotationSpeed * (dt * 60f)
                                                s.morphProgress += s.morphSpeed * (dt * 60f)
                                                if (s.morphProgress > 1f) {
                                                        s.morphProgress = 0f
                                                        val shapes = materialShapesList
                                                        val startShape = s.currentEndShape
                                                        val nextIdx = rng.nextInt(shapes.size)
                                                        val endShape = shapes[nextIdx]
                                                        s.morph = Morph(startShape, endShape)
                                                        s.currentEndShape = endShape
                                                }
                                                if (s.x < -0.1f) s.x = 1.1f
                                                if (s.x > 1.1f) s.x = -0.1f
                                                if (s.y < -0.1f) s.y = 1.1f
                                                if (s.y > 1.1f) s.y = -0.1f
                                        }
                                        // Stream particles along Bezier
                                        for (sp in streamParticles) {
                                                sp.t += sp.speed * (dt * 60f)
                                                if (sp.t > 1f) sp.t -= 1f
                                        }
                                        particleFrame++
                                        elapsed += dt
                                }
                        }
                }

                // Phase 1: Scan radar from phone (bottom-right)
                scope.launch {
                        scanProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing))
                }

                // Phase 2: Wave radar from monitor (top-left), delayed 800ms
                delay(800)
                if (!isSkipping) {
                        waveProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing))

                        if (!isSkipping) {
                                // Phase 3: Connection flash/glow
                                connectionGlow.animateTo(
                                        1f,
                                        tween(400, easing = FastOutSlowInEasing)
                                )

                                if (!isSkipping) {
                                        // Phase 4: Monitor Pull-In
                                        scope.launch {
                                                zoomScale.animateTo(
                                                        6f,
                                                        tween(700, easing = FastOutSlowInEasing)
                                                )
                                        }
                                        scope.launch {
                                                zoomProgress.animateTo(
                                                        1f,
                                                        tween(700, easing = FastOutSlowInEasing)
                                                )
                                        }
                                        // Let the zoom payoff land before covering it:
                                        // 350ms of the 700ms pull-in stays visible, then
                                        // a quick 400ms fade — same total as before.
                                        delay(350)
                                        if (!isSkipping) {
                                                fadeOverlay.animateTo(
                                                        1f,
                                                        tween(400, easing = LinearEasing)
                                                )
                                                finishOnce()
                                        }
                                }
                        }
                }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // UI
        // ═════════════════════════════════════════════════════════════════════════

        Box(
                modifier =
                        Modifier.fillMaxSize()
                                .background(substrateColor)
                                .graphicsLayer { alpha = skipAlpha.value },
                contentAlignment = Alignment.Center
        ) {
                Canvas(
                        modifier =
                                Modifier.fillMaxSize().graphicsLayer {
                                        val s = zoomScale.value
                                        scaleX = s
                                        scaleY = s
                                        // Translate so the monitor screen center becomes viewport
                                        // center
                                        val w = size.width
                                        val h = size.height
                                        val monCx = w * 0.22f
                                        val monCy = h * 0.20f
                                        val targetDx = (w / 2f - monCx) * (s - 1f)
                                        val targetDy = (h / 2f - monCy) * (s - 1f)
                                        translationX = targetDx * zoomProgress.value
                                        translationY = targetDy * zoomProgress.value
                                }
                ) {
                        val width = size.width
                        val height = size.height

                        // ─── Device geometry ─────────────────────────────────────────
                        // Monitor: upper-left quadrant, large
                        val monitorW = width * 0.52f
                        val monitorH = monitorW * 0.62f
                        val monitorX = width * 0.22f - monitorW / 2f
                        val monitorY = height * 0.20f - monitorH / 2f
                        val monitorCx = monitorX + monitorW / 2f
                        val monitorCy = monitorY + monitorH / 2f
                        val monitorCorner = CornerRadius(monitorW * 0.06f)

                        // Phone: lower-right quadrant, smaller
                        val phoneW = width * 0.22f
                        val phoneH = phoneW * 1.8f
                        val phoneX = width * 0.75f - phoneW / 2f
                        val phoneY = height * 0.72f - phoneH / 2f
                        val phoneCx = phoneX + phoneW / 2f
                        val phoneCy = phoneY + phoneH / 2f
                        val phoneCorner = CornerRadius(phoneW * 0.15f)

                        // Monitor screen inset
                        val screenInset = monitorW * 0.04f
                        val monScreenX = monitorX + screenInset
                        val monScreenY = monitorY + screenInset
                        val monScreenW = monitorW - screenInset * 2f
                        val monScreenH = monitorH - screenInset * 2f

                        // Phone screen inset
                        val pScreenInset = phoneW * 0.08f
                        val pScreenX = phoneX + pScreenInset
                        val pScreenY = phoneY + pScreenInset * 1.5f
                        val pScreenW = phoneW - pScreenInset * 2f
                        val pScreenH = phoneH - pScreenInset * 2.5f

                        // ─── Connection Bezier control points (phone -> monitor) ─────
                        val connStart = Offset(phoneCx, phoneCy - phoneH * 0.35f)
                        val connEnd =
                                Offset(monitorCx + monitorW * 0.3f, monitorCy + monitorH * 0.3f)
                        val connCtrl1 = Offset(phoneCx - width * 0.15f, phoneCy - height * 0.25f)
                        val connCtrl2 = Offset(monitorCx + width * 0.2f, monitorCy + height * 0.15f)

                        // ─── Text positioning (centered between devices, stacked) ────
                        val textBlockCy = height * 0.48f
                        val textBlockCx = width * 0.50f

                        // Line 1: [R Logo] + "EM" + "(ote)"
                        // Offsets are design-space px (1x) — scaled by pixelDensity to track the
                        // density-scaled R logo and text on every screen.
                        val emXOffset = 88f * pixelDensity // clears the R logo loop
                        val rBarOffset = 22f * pixelDensity // aligns with the R logo vertical bar

                        val line1W = emXOffset + emMeasured.size.width.toFloat() + oteMeasured.size.width.toFloat()
                        val remXPos = textBlockCx - line1W / 2f
                        val remYPos = textBlockCy - remMeasured.size.height * 1.1f
                        val emXPos = remXPos + emXOffset
                        val oteXPos = emXPos + emMeasured.size.width.toFloat()

                        // Line 2: "EX" + "(ecution)" — aligned under R vertical bar
                        val exXPos = remXPos + rBarOffset
                        val exYPos = remYPos + remMeasured.size.height * 0.95f
                        val ecuXPos = exXPos + exMeasured.size.width.toFloat()

                        // Line 3: "Command Your PC" — centered relative to the text block
                        val tagFullW = commandMeasured.size.width + yourPcMeasured.size.width
                        val tagXCmd = textBlockCx - tagFullW / 2f
                        val tagXYpc = tagXCmd + commandMeasured.size.width
                        val tagY = exYPos + exMeasured.size.height + 12.dp.toPx()

                        // ═════════════════════════════════════════════════════════════
                        // RADAR PARAMETERS
                        // ═════════════════════════════════════════════════════════════
                        val phoneScanCenter = Offset(phoneCx, phoneCy)
                        val monitorWaveCenter = Offset(monitorCx, monitorCy)

                        val maxRadiusPx = hypot(width, height)
                        val scanRadius = maxRadiusPx * scanProgress.value.coerceAtLeast(0f)
                        val waveRadius = maxRadiusPx * waveProgress.value.coerceAtLeast(0f)

                        // ═════════════════════════════════════════════════════════════
                        // BACKGROUND LAYER (always visible, subtle)
                        // ═════════════════════════════════════════════════════════════
                        // Fixed brand backdrop — full-bleed diagonal gradient (the first draw).
                        drawRect(brush = SplashBrand.backdropBrush(size))
                        // particleFrame read here subscribes the draw to the frame clock so
                        // the floating morph-shapes (drawn inside the helper) animate.
                        @Suppress("UNUSED_EXPRESSION") particleFrame
                        drawRemexCommandAmbientFx(floatingShapes, SplashBrand.WindowStroke, SplashBrand.SlateLo)

                        // Particle embers
                        if (waveProgress.value < 0.5f) {
                                @Suppress("UNUSED_EXPRESSION") particleFrame
                                for (p in particles) {
                                        if (p.alpha > 0.02f)
                                                drawCircle(
                                                        deviceLight.copy(alpha = p.alpha),
                                                        1.5.dp.toPx(),
                                                        Offset(p.x * width, p.y * height)
                                                )
                                }
                        }

                        // ═════════════════════════════════════════════════════════════
                        // WIREFRAME CLOSURE (neon outlines — revealed by scan 1)
                        // ═════════════════════════════════════════════════════════════
                        val drawWireframe = {
                                // Monitor frame
                                drawRoundRect(
                                        color = deviceLight,
                                        topLeft = Offset(monitorX, monitorY),
                                        size = Size(monitorW, monitorH),
                                        cornerRadius = monitorCorner,
                                        style = Stroke(3.dp.toPx())
                                )
                                // Monitor stand
                                drawLine(
                                        deviceLight,
                                        Offset(monitorCx, monitorY + monitorH),
                                        Offset(monitorCx, monitorY + monitorH + monitorH * 0.18f),
                                        5.dp.toPx()
                                )
                                val baseW = monitorW * 0.35f
                                val baseY = monitorY + monitorH + monitorH * 0.18f
                                drawLine(
                                        deviceLight,
                                        Offset(monitorCx - baseW / 2f, baseY),
                                        Offset(monitorCx + baseW / 2f, baseY),
                                        4.dp.toPx()
                                )

                                // Phone frame
                                drawRoundRect(
                                        color = deviceLight,
                                        topLeft = Offset(phoneX, phoneY),
                                        size = Size(phoneW, phoneH),
                                        cornerRadius = phoneCorner,
                                        style = Stroke(2.dp.toPx())
                                )
                        }

                        // ═════════════════════════════════════════════════════════════
                        // SOLID CLOSURE (filled devices — revealed by scan 2)
                        // ═════════════════════════════════════════════════════════════
                        val drawSolid = {
                                // Monitor shadow
                                drawRoundRect(
                                        Color.Black.copy(alpha = 0.35f),
                                        Offset(monitorX + 4.dp.toPx(), monitorY + 6.dp.toPx()),
                                        Size(monitorW, monitorH),
                                        monitorCorner
                                )
                                // Monitor body gradient
                                drawRoundRect(
                                        brush =
                                                Brush.linearGradient(
                                                        listOf(
                                                                SplashBrand.SlateLo,
                                                                SplashBrand.WindowStroke
                                                        ),
                                                        Offset(monitorX, monitorY),
                                                        Offset(
                                                                monitorX + monitorW,
                                                                monitorY + monitorH
                                                        )
                                                ),
                                        topLeft = Offset(monitorX, monitorY),
                                        size = Size(monitorW, monitorH),
                                        cornerRadius = monitorCorner
                                )
                                // Monitor screen (dark)
                                drawRoundRect(
                                        substrateColor,
                                        Offset(monScreenX, monScreenY),
                                        Size(monScreenW, monScreenH),
                                        CornerRadius(monitorW * 0.03f)
                                )
                                // Monitor stand (solid)
                                drawLine(
                                        deviceLight,
                                        Offset(monitorCx, monitorY + monitorH),
                                        Offset(monitorCx, monitorY + monitorH + monitorH * 0.18f),
                                        5.dp.toPx()
                                )
                                val stBaseY = monitorY + monitorH + monitorH * 0.18f
                                val stBaseW = monitorW * 0.35f
                                drawLine(
                                        deviceLight,
                                        Offset(monitorCx - stBaseW / 2f, stBaseY),
                                        Offset(monitorCx + stBaseW / 2f, stBaseY),
                                        4.dp.toPx()
                                )

                                // Phone shadow
                                drawRoundRect(
                                        Color.Black.copy(alpha = 0.35f),
                                        Offset(phoneX + 3.dp.toPx(), phoneY + 5.dp.toPx()),
                                        Size(phoneW, phoneH),
                                        phoneCorner
                                )
                                // Phone body
                                drawRoundRect(
                                        SplashBrand.WindowStroke,
                                        Offset(phoneX, phoneY),
                                        Size(phoneW, phoneH),
                                        phoneCorner
                                )
                                // Phone screen
                                drawRoundRect(
                                        substrateColor,
                                        Offset(pScreenX, pScreenY),
                                        Size(pScreenW, pScreenH),
                                        CornerRadius(phoneW * 0.08f)
                                )
                        }

                        // ═════════════════════════════════════════════════════════════
                        // CONNECTION STREAM (energy flow phone -> monitor)
                        // ═════════════════════════════════════════════════════════════
                        val glowIntensity = connectionGlow.value

                        val drawConnectionStream = {
                                // Bezier curve path
                                val connPath =
                                        Path().apply {
                                                moveTo(connStart.x, connStart.y)
                                                cubicTo(
                                                        connCtrl1.x,
                                                        connCtrl1.y,
                                                        connCtrl2.x,
                                                        connCtrl2.y,
                                                        connEnd.x,
                                                        connEnd.y
                                                )
                                        }

                                // Glow layer (wide, translucent)
                                val glowAlpha = 0.08f + glowIntensity * 0.25f
                                val glowWidth = 6.dp.toPx() + glowIntensity * 18.dp.toPx()
                                drawPath(
                                        connPath,
                                        accent.copy(alpha = glowAlpha),
                                        style = Stroke(glowWidth, cap = StrokeCap.Round)
                                )

                                // Core dashed line
                                val dashPhase = streamOffset.value * 80f
                                val coreAlpha = 0.25f + glowIntensity * 0.6f
                                val corePaint =
                                        Paint().apply {
                                                color = accent.copy(alpha = coreAlpha)
                                                style = PaintingStyle.Stroke
                                                strokeWidth =
                                                        1.5.dp.toPx() + glowIntensity * 2.dp.toPx()
                                                pathEffect =
                                                        PathEffect.dashPathEffect(
                                                                floatArrayOf(10f, 10f),
                                                                dashPhase
                                                        )
                                                strokeCap = StrokeCap.Round
                                        }
                                drawContext.canvas.drawPath(connPath, corePaint)

                                // Stream particles traveling along the curve
                                @Suppress("UNUSED_EXPRESSION") particleFrame
                                for (sp in streamParticles) {
                                        val pos =
                                                cubicBezier(
                                                        connStart,
                                                        connCtrl1,
                                                        connCtrl2,
                                                        connEnd,
                                                        sp.t
                                                )
                                        val a = sp.alpha * (0.5f + glowIntensity * 0.5f)
                                        drawCircle(
                                                accent.copy(alpha = a),
                                                sp.radius.dp.toPx(),
                                                pos
                                        )
                                        drawCircle(
                                                accent.copy(alpha = a * 0.3f),
                                                sp.radius.dp.toPx() * 2.5f,
                                                pos
                                        )
                                }

                                // Wi-Fi / signal arcs emanating from phone
                                val wifiCenter = Offset(phoneCx, phoneY - 4.dp.toPx())
                                for (arcIdx in 0 until 3) {
                                        val arcRadius = 20.dp.toPx() + arcIdx * 18.dp.toPx()
                                        val arcAlpha =
                                                (0.15f - arcIdx * 0.04f) + glowIntensity * 0.2f
                                        drawArc(
                                                color =
                                                        accent.copy(
                                                                alpha = arcAlpha.coerceIn(0f, 1f)
                                                        ),
                                                startAngle = 210f,
                                                sweepAngle = 120f,
                                                useCenter = false,
                                                topLeft =
                                                        Offset(
                                                                wifiCenter.x - arcRadius,
                                                                wifiCenter.y - arcRadius
                                                        ),
                                                size = Size(arcRadius * 2f, arcRadius * 2f),
                                                style = Stroke(1.5.dp.toPx(), cap = StrokeCap.Round)
                                        )
                                }

                                // Decorative offset trace
                                val tracePaint2 =
                                        Paint().apply {
                                                color =
                                                        accent.copy(
                                                                alpha =
                                                                        0.12f +
                                                                                glowIntensity *
                                                                                        0.15f
                                                        )
                                                style = PaintingStyle.Stroke
                                                strokeWidth = 1.dp.toPx()
                                                pathEffect =
                                                        PathEffect.dashPathEffect(
                                                                floatArrayOf(6f, 12f),
                                                                dashPhase * 0.7f
                                                        )
                                        }
                                val conn2Start =
                                        Offset(
                                                connStart.x + 12.dp.toPx(),
                                                connStart.y - 8.dp.toPx()
                                        )
                                val conn2End =
                                        Offset(connEnd.x - 10.dp.toPx(), connEnd.y + 8.dp.toPx())
                                val conn2Path =
                                        Path().apply {
                                                moveTo(conn2Start.x, conn2Start.y)
                                                cubicTo(
                                                        connCtrl1.x + 20f,
                                                        connCtrl1.y - 20f,
                                                        connCtrl2.x - 20f,
                                                        connCtrl2.y + 20f,
                                                        conn2End.x,
                                                        conn2End.y
                                                )
                                        }
                                drawContext.canvas.drawPath(conn2Path, tracePaint2)
                        }

                        // ═════════════════════════════════════════════════════════════
                        // PHASE 1: SCAN RADAR + WIREFRAME (from phone)
                        // ═════════════════════════════════════════════════════════════
                        if (scanRadius > 0f) {
                                drawCircle(
                                        brush =
                                                Brush.radialGradient(
                                                        listOf(
                                                                Color.Transparent,
                                                                accent.copy(alpha = 0.07f)
                                                        ),
                                                        phoneScanCenter,
                                                        scanRadius.coerceAtLeast(1f)
                                                ),
                                        radius = scanRadius,
                                        center = phoneScanCenter
                                )
                        }

                        val scanClip =
                                Path().apply {
                                        addOval(
                                                Rect(
                                                        phoneScanCenter.x - scanRadius,
                                                        phoneScanCenter.y - scanRadius,
                                                        phoneScanCenter.x + scanRadius,
                                                        phoneScanCenter.y + scanRadius
                                                )
                                        )
                                }
                        if (scanRadius > 0f) {
                                clipPath(scanClip) {
                                        drawWireframe()
                                        drawText(
                                                emMeasured,
                                                color = onBackground,
                                                topLeft = Offset(emXPos, remYPos)
                                        )
                                        drawText(
                                                exMeasured,
                                                color = onBackground,
                                                topLeft = Offset(exXPos, exYPos)
                                        )
                                        drawText(
                                                commandMeasured,
                                                color = onBackground.copy(alpha = 0.85f),
                                                topLeft = Offset(tagXCmd, tagY)
                                        )
                                }
                        }

                        // Scan circle stroke
                        if (scanRadius > 0f && scanRadius < maxRadiusPx * 1.2f) {
                                drawCircle(
                                        accent.copy(alpha = 0.22f),
                                        scanRadius,
                                        phoneScanCenter,
                                        style = Stroke(18.dp.toPx())
                                )
                                drawCircle(
                                        accent,
                                        scanRadius,
                                        phoneScanCenter,
                                        style = Stroke(2.5.dp.toPx())
                                )
                        }

                        // ═════════════════════════════════════════════════════════════
                        // PHASE 2: WAVE RADAR + SOLID + COMPLETION (from monitor)
                        // ═════════════════════════════════════════════════════════════
                        if (waveRadius > 0f) {
                                drawCircle(
                                        brush =
                                                Brush.radialGradient(
                                                        listOf(
                                                                Color.Transparent,
                                                                waveAccent.copy(alpha = 0.10f)
                                                        ),
                                                        monitorWaveCenter,
                                                        waveRadius.coerceAtLeast(1f)
                                                ),
                                        radius = waveRadius,
                                        center = monitorWaveCenter
                                )
                        }

                        val waveClip =
                                Path().apply {
                                        addOval(
                                                Rect(
                                                        monitorWaveCenter.x - waveRadius,
                                                        monitorWaveCenter.y - waveRadius,
                                                        monitorWaveCenter.x + waveRadius,
                                                        monitorWaveCenter.y + waveRadius
                                                )
                                        )
                                }
                        if (waveRadius > 0f) {
                                clipPath(waveClip) {
                                        drawSolid()
                                        drawConnectionStream()
                                        drawText(
                                                emMeasured,
                                                color = onBackground,
                                                topLeft = Offset(emXPos, remYPos)
                                        )
                                        drawText(
                                                exMeasured,
                                                color = onBackground,
                                                topLeft = Offset(exXPos, exYPos)
                                        )
                                        // Baseline-align completions with their bold counterparts
                                        drawText(
                                                oteMeasured,
                                                color = accent.copy(alpha = 0.95f),
                                                topLeft =
                                                        Offset(
                                                                oteXPos,
                                                        remYPos +
                                                                        (remMeasured.size.height -
                                                                                oteMeasured
                                                                                        .size
                                                                                        .height)
                                                        )
                                        )
                                        drawText(
                                                ecuMeasured,
                                                color = accent.copy(alpha = 0.95f),
                                                topLeft =
                                                        Offset(
                                                                ecuXPos,
                                                                exYPos +
                                                                        (exMeasured.size.height -
                                                                                ecuMeasured
                                                                                        .size
                                                                                        .height)
                                                        )
                                        )
                                        drawText(
                                                commandMeasured,
                                                color = onBackground.copy(alpha = 0.85f),
                                                topLeft = Offset(tagXCmd, tagY)
                                        )
                                        drawText(
                                                yourPcMeasured,
                                                color = onBackground.copy(alpha = 0.85f),
                                                topLeft = Offset(tagXYpc, tagY)
                                        )
                                }
                        }

                        // Wave circle stroke
                        if (waveRadius > 0f && waveRadius < maxRadiusPx * 1.2f) {
                                drawCircle(
                                        waveAccent.copy(alpha = 0.18f),
                                        waveRadius,
                                        monitorWaveCenter,
                                        style = Stroke(14.dp.toPx())
                                )
                                drawCircle(
                                        Color.White.copy(alpha = 0.75f),
                                        waveRadius,
                                        monitorWaveCenter,
                                        style = Stroke(2.dp.toPx())
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
                        // ═════════════════════════════════════════════════════════════
                        // PHASE 4: Fade overlay (drawn on top of everything)
                        // ═════════════════════════════════════════════════════════════
                        if (fadeOverlay.value > 0f) {
                                drawRect(
                                        color = substrateColor.copy(alpha = fadeOverlay.value),
                                        size = size
                                )
                        }
                }
        }
}

/** Cubic Bezier point calculation */
private fun cubicBezier(p0: Offset, p1: Offset, p2: Offset, p3: Offset, t: Float): Offset {
        val u = 1 - t
        val tt = t * t
        val uu = u * u
        val uuu = uu * u
        val ttt = tt * t

        val x = uuu * p0.x + 3 * uu * t * p1.x + 3 * u * tt * p2.x + ttt * p3.x
        val y = uuu * p0.y + 3 * uu * t * p1.y + 3 * u * tt * p2.y + ttt * p3.y
        return Offset(x, y)
}
