package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
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
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Paint
import androidx.compose.ui.graphics.PaintingStyle
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipRect
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
import com.clindsay94.remex.ui.theme.materialShapesList
import androidx.graphics.shapes.Morph
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

        // Animation States — 3-pass sweep reveal (replaces the old scan/wave/connectionGlow radar)
        val sweep = remember { Animatable(0f) } // 0→3 across the three top-to-bottom sweep passes
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

        // Wordmark lockup — "Rem"(off-white)+"Ex"(amber) in Victor Mono, matching CosmicZoom.
        val wordmarkStyle = TextStyle(fontFamily = SplashBrand.VictorMonoBold, fontSize = 40.sp)
        val wordmarkMeasured = remember { textMeasurer.measure(SplashBrand.remExAnnotated(), wordmarkStyle) }

        // Tagline + bottom chrome — Victor Mono (all splash text uses the brand mono face).
        val tagFontSize = with(density) { 13.dp.toSp() }
        val tagTracking = with(density) { 2.dp.toSp() }
        val tagStyle = remember(tagFontSize, tagTracking) {
                TextStyle(
                        color = SplashBrand.OffWhite,
                        fontSize = tagFontSize,
                        fontWeight = FontWeight.Normal,
                        fontFamily = SplashBrand.VictorMonoBold,
                        letterSpacing = tagTracking
                )
        }

        // In-monitor terminal reveal lines — literal command syntax, localized descriptive words.
        val termLine1 = "> ${stringResource(R.string.splash_term_connect)} --host pc"
        val termLine2 = "> ${stringResource(R.string.splash_term_pairing)}... ok"
        val termLine3 = "> ${stringResource(R.string.splash_term_session_ready)}"

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

                // 3 fast top-to-bottom sweep passes stage the reveal (was 2-stage circular radar).
                for (pass in 1..3) {
                        if (isSkipping) break
                        sweep.animateTo(pass.toFloat(), tween(480, easing = FastOutSlowInEasing))
                        if (pass < 3) delay(80)
                }

                // Brief settle so the terminal + wordmark register before the pull-in.
                if (!isSkipping) delay(350)

                if (!isSkipping) {
                        // Monitor pull-in + fade (kept from the original exit).
                        scope.launch { zoomScale.animateTo(6f, tween(700, easing = FastOutSlowInEasing)) }
                        scope.launch { zoomProgress.animateTo(1f, tween(700, easing = FastOutSlowInEasing)) }
                        delay(400)
                        if (!isSkipping) {
                                fadeOverlay.animateTo(1f, tween(400, easing = LinearEasing))
                                finishOnce()
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

                        // ─── 3-pass sweep progress (replaces circular scan/wave radii) ─
                        val p1 = sweep.value.coerceIn(0f, 1f)        // pass 1: device outlines
                        val p2 = (sweep.value - 1f).coerceIn(0f, 1f) // pass 2: solid fills + stream
                        val p3 = (sweep.value - 2f).coerceIn(0f, 1f) // pass 3: terminal + wordmark
                        val glowIntensity = p2 * (1f - 0.8f * p3)    // stream ramps in on pass 2, then dims behind the wordmark on pass 3

                        // Feature glyphs: icon 1 (pass 1), icons 2 & 3 (pass 2); they park + stay.
                        // Center glyph raised into a peak so it clears the centered wordmark below it.
                        val featSpread = width * 0.27f
                        val featSize = width * 0.115f
                        val featCxs = floatArrayOf(width * 0.5f - featSpread, width * 0.5f, width * 0.5f + featSpread)
                        val featCys = floatArrayOf(height * 0.40f, height * 0.33f, height * 0.40f)
                        val featAlphas = floatArrayOf(sstep(0.55f, 1f, p1), sstep(0.15f, 0.6f, p2), sstep(0.55f, 1f, p2))

                        // Wordmark lockup (pass 3): icon above "RemEx", fades + rises in.
                        val wmAlpha = sstep(0.25f, 1f, p3)
                        val wmRise = (1f - wmAlpha) * 14.dp.toPx()
                        val wmCy = height * 0.56f
                        val wmIconSize = width * 0.15f

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
                        if (p2 < 0.5f) {
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
                        // SWEEP REVEAL — 3 top-to-bottom passes stage the scene
                        // ═════════════════════════════════════════════════════════════
                        // Pass 1: device outlines.
                        sweepReveal(p1) { drawWireframe() }
                        // Pass 2: solid devices + connection stream (drawn over the outlines).
                        sweepReveal(p2) {
                                drawSolid()
                                if (glowIntensity > 0f) drawConnectionStream()
                        }

                        // Feature glyphs — icon 1 (pass 1), icons 2 & 3 (pass 2); park + stay.
                        for (i in 0 until 3) {
                                val fa = featAlphas[i]
                                if (fa > 0.01f) {
                                        drawFeatureGlyph(
                                                FeatureGlyph.values()[i],
                                                Offset(featCxs[i], featCys[i]),
                                                featSize * (0.7f + fa * 0.3f),
                                                alpha = fa
                                        )
                                }
                        }

                        // ═════════════════════════════════════════════════════════════
                        // PASS 3: terminal typing (in the monitor) + wordmark lockup
                        // ═════════════════════════════════════════════════════════════
                        if (p3 > 0f) {
                                val termSp = with(density) { (monScreenW / 15f).toSp() }
                                val termStyleR = TextStyle(
                                        color = SplashBrand.OffWhite,
                                        fontFamily = SplashBrand.VictorMonoBold,
                                        fontSize = termSp
                                )
                                val tl = listOf(
                                        textMeasurer.measure(termLine1, termStyleR),
                                        textMeasurer.measure(termLine2, termStyleR),
                                        textMeasurer.measure(termLine3, termStyleR)
                                )
                                val lineLens = intArrayOf(termLine1.length, termLine2.length, termLine3.length)
                                val totalChars = lineLens.sum()
                                val shownChars = p3 * totalChars
                                val pad = monScreenW * 0.06f
                                val lineH = tl[0].size.height.toFloat() * 1.25f
                                var cursorX = monScreenX + pad
                                var cursorY = monScreenY + pad
                                clipRect(monScreenX, monScreenY, monScreenX + monScreenW, monScreenY + monScreenH) {
                                        var consumed = 0
                                        for (idx in 0 until 3) {
                                                val ly = monScreenY + pad + idx * lineH
                                                val revealed = ((shownChars - consumed) / lineLens[idx]).coerceIn(0f, 1f)
                                                if (revealed > 0f) {
                                                        val lw = tl[idx].size.width.toFloat()
                                                        clipRect(monScreenX, ly, monScreenX + pad + lw * revealed, ly + lineH) {
                                                                drawText(tl[idx], topLeft = Offset(monScreenX + pad, ly))
                                                        }
                                                        cursorX = monScreenX + pad + lw * revealed
                                                        cursorY = ly
                                                }
                                                consumed += lineLens[idx]
                                        }
                                        // blinking block cursor at the typing head
                                        if ((elapsed * 2f).toInt() % 2 == 0) {
                                                drawRect(
                                                        SplashBrand.Amber,
                                                        topLeft = Offset(cursorX + 2f, cursorY),
                                                        size = Size(monScreenW * 0.03f, lineH * 0.7f)
                                                )
                                        }
                                }
                        }

                        // Wordmark lockup: brand icon above "RemEx" + kept "Command Your PC" tagline.
                        if (wmAlpha > 0.01f) {
                                with(SplashBrand) {
                                        drawRemexIcon(
                                                Offset(width / 2f, wmCy - wmIconSize * 0.62f + wmRise),
                                                wmIconSize,
                                                opacity = wmAlpha
                                        )
                                }
                                drawText(
                                        wordmarkMeasured,
                                        topLeft = Offset(width / 2f - wordmarkMeasured.size.width / 2f, wmCy + wmRise),
                                        alpha = wmAlpha
                                )
                                val tagFullW = commandMeasured.size.width + yourPcMeasured.size.width
                                val tagY = wmCy + wordmarkMeasured.size.height + 10.dp.toPx() + wmRise
                                val tagX = width / 2f - tagFullW / 2f
                                drawText(commandMeasured, color = SplashBrand.SlateLo, topLeft = Offset(tagX, tagY), alpha = wmAlpha)
                                drawText(yourPcMeasured, color = SplashBrand.Amber, topLeft = Offset(tagX + commandMeasured.size.width, tagY), alpha = wmAlpha)
                        }

                        // Leading amber edge + glow for the active sweep pass.
                        val activeFrac = when {
                                sweep.value <= 0f -> -1f
                                sweep.value < 1f -> p1
                                sweep.value < 2f -> p2
                                sweep.value < 3f -> p3
                                else -> -1f
                        }
                        if (activeFrac in 0.001f..0.999f) {
                                val edgeY = height * activeFrac
                                val trail = 44.dp.toPx()
                                drawRect(
                                        Brush.verticalGradient(
                                                listOf(Color.Transparent, SplashBrand.Amber.copy(alpha = 0.22f)),
                                                startY = edgeY - trail,
                                                endY = edgeY
                                        ),
                                        topLeft = Offset(0f, edgeY - trail),
                                        size = Size(width, trail)
                                )
                                drawRect(
                                        SplashBrand.Amber.copy(alpha = 0.9f),
                                        topLeft = Offset(0f, edgeY - 1.5.dp.toPx()),
                                        size = Size(width, 3.dp.toPx())
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

/** Clamp: 0 at/below [edge0], 1 at/above [edge1], linear between — for staged fade-ins. */
private fun sstep(edge0: Float, edge1: Float, x: Float): Float =
        ((x - edge0) / (edge1 - edge0)).coerceIn(0f, 1f)

/** Reveal [block] under a top-to-bottom band covering [frac] of the height (1 = full). */
private inline fun DrawScope.sweepReveal(frac: Float, block: DrawScope.() -> Unit) {
        if (frac <= 0f) return
        if (frac >= 1f) {
                block()
                return
        }
        clipRect(top = 0f, bottom = size.height * frac, block = block)
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
