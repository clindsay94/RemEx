package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutLinearInEasing
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Matrix
import androidx.compose.ui.graphics.Paint
import androidx.compose.ui.graphics.PaintingStyle
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.compose.ui.unit.sp
import androidx.graphics.shapes.Morph
import androidx.graphics.shapes.RoundedPolygon
import com.clindsay94.remex.ui.theme.materialShapesList
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.sin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private class Particle(
        var x: Float,
        var y: Float,
        var vx: Float,
        var vy: Float,
        var lifetime: Float,
        var maxLifetime: Float,
        var alpha: Float
)

private class FloatingShape(
        var x: Float,
        var y: Float,
        var size: Float,
        var vx: Float,
        var vy: Float,
        var morph: Morph,
        var currentEndShape: RoundedPolygon,
        var morphProgress: Float,
        var morphSpeed: Float,
        var rotation: Float,
        var rotationSpeed: Float,
        var alpha: Float,
        var color: Color
)

private class StreamParticle(var t: Float, var speed: Float, var radius: Float, var alpha: Float)

/**
 * A highly animated technical splash screen using Jetpack Compose Canvas.
 *
 * Visual Stages:
 * 1. Substrate reveals: Circuit board traces and radial grids appear in the background.
 * 2. The Phone (source) emits a "Scan" radar (primary color).
 * 3. The Scan reveals the "Wireframe" of the system (Monitor, Phone, and partial text).
 * 4. The Monitor (target) emits a "Wave" radar (secondary color).
 * 5. The Wave reveals the "Solid" surfaces, the connection stream, and the full "RemEx" brand.
 * 6. Connection Established: Energy flows from Phone to Monitor.
 * 7. Final Transition: The camera pulls into the Monitor screen to enter the app.
 */
@Composable
fun SplashScreen(onFinished: () -> Unit) {
        val scope = rememberCoroutineScope()
        val view = LocalView.current

        // Colors from theme
        val background = MaterialTheme.colorScheme.background
        val substrateColor = background.copy(alpha = 1f)
        val onBackground = MaterialTheme.colorScheme.onBackground
        val primary = MaterialTheme.colorScheme.primary
        val primaryContainer = MaterialTheme.colorScheme.primaryContainer
        val secondary = MaterialTheme.colorScheme.secondary
        val secondaryContainer = MaterialTheme.colorScheme.secondaryContainer
        val tertiary = MaterialTheme.colorScheme.tertiary
        val onPrimary = MaterialTheme.colorScheme.onPrimary
        val surface = MaterialTheme.colorScheme.surface
        val surfaceVariant = MaterialTheme.colorScheme.surfaceVariant

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
        val textMeasurer = rememberTextMeasurer()
        val remStyle =
                TextStyle(
                        color = Color.White,
                        fontSize = 54.sp,
                        fontWeight = FontWeight.Black,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = 4.sp
                )
        val exStyle = remStyle.copy(color = primary)
        val completionStyle =
                TextStyle(
                        color = primary.copy(alpha = 0.8f),
                        fontSize = 24.sp,
                        fontWeight = FontWeight.Medium,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = 1.sp
                )
        val tagStyle =
                TextStyle(
                        color = onBackground.copy(alpha = 0.7f),
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Light,
                        fontFamily = FontFamily.SansSerif,
                        letterSpacing = 3.sp
                )

        val remMeasured = textMeasurer.measure("REM", remStyle)
        val oteMeasured = textMeasurer.measure("ote", completionStyle)
        val exMeasured = textMeasurer.measure("EX", exStyle)
        val ecuMeasured = textMeasurer.measure("ecution", completionStyle)
        val commandMeasured = textMeasurer.measure("COMMAND ", tagStyle)
        val yourPcMeasured = textMeasurer.measure("YOUR PC", tagStyle)

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
                val colors = listOf(primary, secondary, tertiary)
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

        val scanLineAnimatables = remember { List(5) { Animatable(0f) } }

        var particleFrame by remember { mutableStateOf(0) }
        var isSkipping by remember { mutableStateOf(false) }
        val skipAlpha = remember { Animatable(1f) }

        fun skipSplash() {
                if (isSkipping) return
                isSkipping = true
                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                scope.launch {
                        skipAlpha.animateTo(0f, tween(200, easing = FastOutSlowInEasing))
                        onFinished()
                }
        }

        LaunchedEffect(Unit) {
                // Particle + stream-particle update loop (~60 fps)
                scope.launch(kotlinx.coroutines.Dispatchers.Default) {
                        val rng = java.util.Random(99L)
                        while (!isSkipping) {
                                val dt = 0.016f
                                // Embers
                                for (p in particles) {
                                        p.lifetime += dt
                                        p.x += p.vx
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
                                        s.x += s.vx
                                        s.y += s.vy
                                        s.rotation += s.rotationSpeed
                                        s.morphProgress += s.morphSpeed
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
                                        sp.t += sp.speed
                                        if (sp.t > 1f) sp.t -= 1f
                                }
                                // We increment the frame trigger on the main thread to trigger
                                // recomposition
                                kotlinx.coroutines.withContext(
                                        kotlinx.coroutines.Dispatchers.Main
                                ) { particleFrame++ }
                                delay(16L)
                        }
                }

                // Phase 1: Scan radar from phone (bottom-right)
                scope.launch {
                        scanProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing))
                }

                // Scan line spring animations — stagger each line with bouncy spring physics
                scanLineAnimatables.forEachIndexed { index, anim ->
                        scope.launch {
                                delay(200L + index * 180L) // stagger each line
                                anim.animateTo(
                                        targetValue = 1f,
                                        animationSpec =
                                                spring(
                                                        dampingRatio =
                                                                Spring.DampingRatioMediumBouncy,
                                                        stiffness = Spring.StiffnessLow
                                                )
                                )
                        }
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
                                        // Fade overlay starts slightly after zoom begins
                                        delay(300)
                                        if (!isSkipping) {
                                                fadeOverlay.animateTo(
                                                        1f,
                                                        tween(400, easing = LinearEasing)
                                                )
                                                onFinished()
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
                                .pointerInput(Unit) {
                                        detectTapGestures { scope.launch { skipSplash() } }
                                }
                                .alpha(skipAlpha.value),
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

                        // Line 1: "REM" + "(ote)"
                        val line1W =
                                remMeasured.size.width.toFloat() + oteMeasured.size.width.toFloat()
                        val remXPos = textBlockCx - line1W / 2f
                        val remYPos = textBlockCy - remMeasured.size.height * 1.1f
                        val oteXPos = remXPos + remMeasured.size.width

                        // Line 2: "EX" + "(ecution)" — indented right
                        val indent = remMeasured.size.width * 0.3f
                        val exXPos = remXPos + indent
                        val exYPos = remYPos + remMeasured.size.height * 0.95f
                        val ecuXPos = exXPos + exMeasured.size.width

                        // Line 3: "Command Your PC" — centered
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

                        // Circuit board traces — surface color
                        val traceColor = surface.copy(alpha = 0.06f)
                        val traceRng = java.util.Random(55L)
                        repeat(20) {
                                val sx = traceRng.nextFloat() * width
                                val sy = traceRng.nextFloat() * height
                                val seg1 = 30f + traceRng.nextFloat() * 80f
                                val seg2 = 20f + traceRng.nextFloat() * 60f
                                val horiz = traceRng.nextBoolean()
                                val d1 = if (traceRng.nextBoolean()) 1f else -1f
                                val d2 = if (traceRng.nextBoolean()) 1f else -1f
                                val tx: Float
                                val ty: Float
                                val ex: Float
                                val ey: Float
                                if (horiz) {
                                        tx = sx + seg1 * d1
                                        ty = sy
                                        ex = tx
                                        ey = ty + seg2 * d2
                                } else {
                                        tx = sx
                                        ty = sy + seg1 * d1
                                        ex = tx + seg2 * d2
                                        ey = ty
                                }
                                val p =
                                        Path().apply {
                                                moveTo(sx, sy)
                                                lineTo(tx, ty)
                                                lineTo(ex, ey)
                                        }
                                drawPath(p, color = traceColor, style = Stroke(width = 1.5f))
                                drawCircle(
                                        color = traceColor,
                                        radius = 4.dp.toPx(),
                                        center = Offset(tx, ty)
                                )
                        }

                        // Network topology nodes — surfaceVariant
                        val nodeColor = surfaceVariant.copy(alpha = 0.07f)
                        val nodeRng = java.util.Random(123L)
                        val netNodes =
                                List(7) {
                                        Offset(
                                                nodeRng.nextFloat() * width,
                                                nodeRng.nextFloat() * height
                                        )
                                }
                        for (i in netNodes.indices) {
                                for (j in i + 1 until netNodes.size) {
                                        if (hypot(
                                                        netNodes[i].x - netNodes[j].x,
                                                        netNodes[i].y - netNodes[j].y
                                                ) < width * 0.35f
                                        ) {
                                                drawLine(nodeColor, netNodes[i], netNodes[j], 1f)
                                        }
                                }
                        }
                        for (n in netNodes) drawCircle(nodeColor, 3.dp.toPx(), n)

                        // Floating shapes — Material Morphing
                        @Suppress("UNUSED_EXPRESSION") particleFrame
                        for (shape in floatingShapes) {
                                val shx = shape.x * width
                                val shy = shape.y * height
                                val r = shape.size * min(width, height)

                                val morphPath = Path()
                                var first = true
                                shape.morph.forEachCubic(shape.morphProgress) { bezier ->
                                        if (first) {
                                                morphPath.moveTo(bezier.anchor0X, bezier.anchor0Y)
                                                first = false
                                        }
                                        morphPath.cubicTo(
                                                bezier.control0X,
                                                bezier.control0Y,
                                                bezier.control1X,
                                                bezier.control1Y,
                                                bezier.anchor1X,
                                                bezier.anchor1Y
                                        )
                                }
                                morphPath.close()

                                // Scale and translate the morphPath
                                val bounds = morphPath.getBounds()
                                val scale = r / maxOf(bounds.width, bounds.height)

                                val matrix = Matrix()
                                matrix.translate(shx, shy)
                                matrix.rotateZ(shape.rotation)
                                matrix.scale(scale, scale)
                                matrix.translate(-bounds.center.x, -bounds.center.y)
                                morphPath.transform(matrix)

                                drawPath(
                                        morphPath,
                                        shape.color.copy(alpha = shape.alpha),
                                        style = Stroke(2.dp.toPx())
                                )
                                // Subtle fill to make them feel more "Material"
                                drawPath(morphPath, shape.color.copy(alpha = shape.alpha * 0.3f))
                        }

                        // Radial grid — surfaceVariant
                        val radColor = surfaceVariant.copy(alpha = 0.03f)
                        val maxRad = min(width, height) * 0.45f
                        for (rf in listOf(0.25f, 0.45f, 0.65f, 0.85f)) drawCircle(
                                radColor,
                                rf * maxRad,
                                center,
                                style = Stroke(1f)
                        )
                        for (i in 0 until 8) {
                                val a = i * (PI / 4).toFloat()
                                drawLine(
                                        radColor,
                                        center,
                                        Offset(
                                                center.x + maxRad * cos(a),
                                                center.y + maxRad * sin(a)
                                        ),
                                        1f
                                )
                        }

                        // ─── Platform Logos ──────────────────────────────────────────
                        val baseLogoAlpha = 0.08f // Lowered for better reveal contrast

                        fun getLogoEffect(pos: Offset): Float {
                                val sDist =
                                        hypot(pos.x - phoneScanCenter.x, pos.y - phoneScanCenter.y)
                                val wDist =
                                        hypot(
                                                pos.x - monitorWaveCenter.x,
                                                pos.y - monitorWaveCenter.y
                                        )
                                val thickness = 90.dp.toPx()

                                // Scan impact: peak as scan line passes, then settle into
                                // persistent glow
                                val sDiff = abs(sDist - scanRadius)
                                val sImpact =
                                        if (sDiff < thickness) (1f - sDiff / thickness) else 0f
                                val sPersistent = if (scanRadius > sDist) 0.30f else 0f
                                val sEffect = maxOf(sImpact, sPersistent)

                                // Wave impact: stronger peak and higher persistent glow
                                val wDiff = abs(wDist - waveRadius)
                                val wImpact =
                                        if (wDiff < thickness) (1f - wDiff / thickness) * 1.6f
                                        else 0f
                                val wPersistent = if (waveRadius > wDist) 0.75f else 0f
                                val wEffect = maxOf(wImpact, wPersistent)

                                // Total effect is the strongest of either, allowing wave to
                                // overtake scan
                                return maxOf(sEffect, wEffect).coerceIn(0f, 2f)
                        }

                        // 1. Windows Logo (Top-Right)
                        val winSize = 64.dp.toPx()
                        val winTop = Offset(width - winSize - 40.dp.toPx(), 40.dp.toPx())
                        val winCenter = Offset(winTop.x + winSize / 2f, winTop.y + winSize / 2f)
                        val winEffect = getLogoEffect(winCenter)
                        val winAlpha = (baseLogoAlpha + winEffect * 0.55f).coerceAtMost(1f)
                        val winStrokeWidth = 2.0.dp.toPx() + winEffect * 2.2.dp.toPx()
                        val winColor = primary.copy(alpha = winAlpha)

                        scale(1f + winEffect * 0.12f, 1f + winEffect * 0.12f, winCenter) {
                                val winGap = winSize * 0.08f
                                val winHalf = winSize / 2f
                                listOf(
                                                Rect(
                                                        winTop.x,
                                                        winTop.y,
                                                        winTop.x + winHalf - winGap,
                                                        winTop.y + winHalf - winGap
                                                ),
                                                Rect(
                                                        winTop.x + winHalf + winGap,
                                                        winTop.y - 2.dp.toPx(),
                                                        winTop.x + winSize,
                                                        winTop.y + winHalf - winGap
                                                ),
                                                Rect(
                                                        winTop.x,
                                                        winTop.y + winHalf + winGap,
                                                        winTop.x + winHalf - winGap,
                                                        winTop.y + winSize
                                                ),
                                                Rect(
                                                        winTop.x + winHalf + winGap,
                                                        winTop.y + winHalf + winGap,
                                                        winTop.x + winSize,
                                                        winTop.y + winSize + 2.dp.toPx()
                                                )
                                        )
                                        .forEach { r ->
                                                drawRect(
                                                        winColor,
                                                        r.topLeft,
                                                        r.size,
                                                        style = Stroke(winStrokeWidth)
                                                )
                                                if (winEffect > 0.1f) {
                                                        drawRect(
                                                                winColor.copy(
                                                                        alpha =
                                                                                (winEffect * 0.25f)
                                                                                        .coerceAtMost(
                                                                                                1f
                                                                                        )
                                                                ),
                                                                r.topLeft,
                                                                r.size,
                                                                style =
                                                                        Stroke(
                                                                                winStrokeWidth *
                                                                                        3.5f
                                                                        )
                                                        )
                                                        if (winEffect > 0.8f
                                                        ) { // Extra glow layer for wave phase
                                                                drawRect(
                                                                        winColor.copy(
                                                                                alpha =
                                                                                        (winEffect *
                                                                                                        0.1f)
                                                                                                .coerceAtMost(
                                                                                                        1f
                                                                                                )
                                                                        ),
                                                                        r.topLeft,
                                                                        r.size,
                                                                        style =
                                                                                Stroke(
                                                                                        winStrokeWidth *
                                                                                                7f
                                                                                )
                                                                )
                                                        }
                                                }
                                        }
                        }

                        // 2. Linux Penguin Silhouette (Top-Right, next to Windows)
                        val tuxSize = 56.dp.toPx()
                        val tuxTop = Offset(winTop.x - tuxSize - 35.dp.toPx(), 50.dp.toPx())
                        val tuxCenter = Offset(tuxTop.x + tuxSize / 2f, tuxTop.y + tuxSize / 2f)
                        val tuxEffect = getLogoEffect(tuxCenter)
                        val tuxAlpha = (baseLogoAlpha + tuxEffect * 0.55f).coerceAtMost(1f)
                        val tuxStrokeWidth = 2.0.dp.toPx() + tuxEffect * 2.2.dp.toPx()
                        val tuxColor = primary.copy(alpha = tuxAlpha)

                        scale(1f + tuxEffect * 0.12f, 1f + tuxEffect * 0.12f, tuxCenter) {
                                val tuxPath =
                                        Path().apply {
                                                addOval(
                                                        Rect(
                                                                tuxTop.x + tuxSize * 0.3f,
                                                                tuxTop.y,
                                                                tuxTop.x + tuxSize * 0.7f,
                                                                tuxTop.y + tuxSize * 0.35f
                                                        )
                                                )
                                                addOval(
                                                        Rect(
                                                                tuxTop.x + tuxSize * 0.15f,
                                                                tuxTop.y + tuxSize * 0.3f,
                                                                tuxTop.x + tuxSize * 0.85f,
                                                                tuxTop.y + tuxSize * 0.9f
                                                        )
                                                )
                                                moveTo(
                                                        tuxTop.x + tuxSize * 0.15f,
                                                        tuxTop.y + tuxSize * 0.5f
                                                )
                                                lineTo(tuxTop.x, tuxTop.y + tuxSize * 0.7f)
                                                moveTo(
                                                        tuxTop.x + tuxSize * 0.85f,
                                                        tuxTop.y + tuxSize * 0.5f
                                                )
                                                lineTo(
                                                        tuxTop.x + tuxSize,
                                                        tuxTop.y + tuxSize * 0.7f
                                                )
                                        }
                                drawPath(
                                        tuxPath,
                                        tuxColor,
                                        style =
                                                Stroke(
                                                        tuxStrokeWidth,
                                                        cap = StrokeCap.Round,
                                                        join = StrokeJoin.Round
                                                )
                                )
                                if (tuxEffect > 0.1f) {
                                        drawPath(
                                                tuxPath,
                                                tuxColor.copy(
                                                        alpha = (tuxEffect * 0.25f).coerceAtMost(1f)
                                                ),
                                                style = Stroke(tuxStrokeWidth * 3.5f)
                                        )
                                        if (tuxEffect > 0.8f) {
                                                drawPath(
                                                        tuxPath,
                                                        tuxColor.copy(
                                                                alpha =
                                                                        (tuxEffect * 0.1f)
                                                                                .coerceAtMost(1f)
                                                        ),
                                                        style = Stroke(tuxStrokeWidth * 7f)
                                                )
                                        }
                                }
                        }

                        // 3. Android Logo (Bottom-Left)
                        val andSize = 72.dp.toPx()
                        val andTop = Offset(40.dp.toPx(), height - andSize - 120.dp.toPx())
                        val andCenter = Offset(andTop.x + andSize / 2f, andTop.y + andSize / 2f)
                        val andEffect = getLogoEffect(andCenter)
                        val andAlpha = (baseLogoAlpha + andEffect * 0.55f).coerceAtMost(1f)
                        val andStrokeWidth = 2.0.dp.toPx() + andEffect * 2.2.dp.toPx()
                        val andColor = secondary.copy(alpha = andAlpha)

                        scale(1f + andEffect * 0.12f, 1f + andEffect * 0.12f, andCenter) {
                                val andPath =
                                        Path().apply {
                                                addArc(
                                                        Rect(
                                                                andTop.x,
                                                                andTop.y,
                                                                andTop.x + andSize,
                                                                andTop.y + andSize
                                                        ),
                                                        180f,
                                                        180f
                                                )
                                                moveTo(
                                                        andTop.x + andSize * 0.25f,
                                                        andTop.y + andSize * 0.1f
                                                )
                                                lineTo(
                                                        andTop.x + andSize * 0.1f,
                                                        andTop.y - andSize * 0.15f
                                                )
                                                moveTo(
                                                        andTop.x + andSize * 0.75f,
                                                        andTop.y + andSize * 0.1f
                                                )
                                                lineTo(
                                                        andTop.x + andSize * 0.9f,
                                                        andTop.y - andSize * 0.15f
                                                )
                                        }
                                drawPath(
                                        andPath,
                                        andColor,
                                        style = Stroke(andStrokeWidth, cap = StrokeCap.Round)
                                )
                                if (andEffect > 0.1f) {
                                        drawPath(
                                                andPath,
                                                andColor.copy(
                                                        alpha = (andEffect * 0.25f).coerceAtMost(1f)
                                                ),
                                                style = Stroke(andStrokeWidth * 3.5f)
                                        )
                                        if (andEffect > 0.8f) {
                                                drawPath(
                                                        andPath,
                                                        andColor.copy(
                                                                alpha =
                                                                        (andEffect * 0.1f)
                                                                                .coerceAtMost(1f)
                                                        ),
                                                        style = Stroke(andStrokeWidth * 7f)
                                                )
                                        }
                                }
                        }

                        // 4. RemEx App Logo (Bottom-Left, under Android)
                        val rxSize = 54.dp.toPx()
                        val rxTop = Offset(45.dp.toPx(), height - rxSize - 40.dp.toPx())
                        val rxCenter = Offset(rxTop.x + rxSize * 0.75f, rxTop.y + rxSize / 2f)
                        val rxEffect = getLogoEffect(rxCenter)
                        val rxAlpha = (baseLogoAlpha + rxEffect * 0.55f).coerceAtMost(1f)
                        val rxStrokeWidth = 2.0.dp.toPx() + rxEffect * 2.2.dp.toPx()
                        val rxColor = secondary.copy(alpha = rxAlpha)

                        scale(1f + rxEffect * 0.12f, 1f + rxEffect * 0.12f, rxCenter) {
                                val rxPath =
                                        Path().apply {
                                                moveTo(rxTop.x, rxTop.y + rxSize)
                                                lineTo(rxTop.x, rxTop.y)
                                                arcTo(
                                                        Rect(
                                                                rxTop.x,
                                                                rxTop.y,
                                                                rxTop.x + rxSize * 0.7f,
                                                                rxTop.y + rxSize * 0.5f
                                                        ),
                                                        270f,
                                                        180f,
                                                        false
                                                )
                                                lineTo(rxTop.x + rxSize * 0.6f, rxTop.y + rxSize)

                                                // Stylized 'X'
                                                moveTo(rxTop.x + rxSize * 0.9f, rxTop.y)
                                                lineTo(rxTop.x + rxSize * 1.5f, rxTop.y + rxSize)
                                                moveTo(rxTop.x + rxSize * 1.5f, rxTop.y)
                                                lineTo(rxTop.x + rxSize * 0.9f, rxTop.y + rxSize)

                                                // Terminal cursor underscore
                                                moveTo(
                                                        rxTop.x + rxSize * 0.9f,
                                                        rxTop.y + rxSize + 3.dp.toPx()
                                                )
                                                lineTo(
                                                        rxTop.x + rxSize * 1.3f,
                                                        rxTop.y + rxSize + 3.dp.toPx()
                                                )
                                        }
                                drawPath(
                                        rxPath,
                                        rxColor,
                                        style =
                                                Stroke(
                                                        rxStrokeWidth,
                                                        cap = StrokeCap.Round,
                                                        join = StrokeJoin.Round
                                                )
                                )
                                if (rxEffect > 0.1f) {
                                        drawPath(
                                                rxPath,
                                                rxColor.copy(
                                                        alpha = (rxEffect * 0.25f).coerceAtMost(1f)
                                                ),
                                                style = Stroke(rxStrokeWidth * 3.5f)
                                        )
                                        if (rxEffect > 0.8f) {
                                                drawPath(
                                                        rxPath,
                                                        rxColor.copy(
                                                                alpha =
                                                                        (rxEffect * 0.1f)
                                                                                .coerceAtMost(1f)
                                                        ),
                                                        style = Stroke(rxStrokeWidth * 7f)
                                                )
                                        }
                                }
                        }

                        // Particle embers — tertiary
                        if (waveProgress.value < 0.5f) {
                                @Suppress("UNUSED_EXPRESSION") particleFrame
                                for (p in particles) {
                                        if (p.alpha > 0.02f)
                                                drawCircle(
                                                        tertiary.copy(alpha = p.alpha),
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
                                        color = primary,
                                        topLeft = Offset(monitorX, monitorY),
                                        size = Size(monitorW, monitorH),
                                        cornerRadius = monitorCorner,
                                        style = Stroke(3.dp.toPx())
                                )
                                // Monitor stand
                                drawLine(
                                        primary,
                                        Offset(monitorCx, monitorY + monitorH),
                                        Offset(monitorCx, monitorY + monitorH + monitorH * 0.18f),
                                        5.dp.toPx()
                                )
                                val baseW = monitorW * 0.35f
                                val baseY = monitorY + monitorH + monitorH * 0.18f
                                drawLine(
                                        primary,
                                        Offset(monitorCx - baseW / 2f, baseY),
                                        Offset(monitorCx + baseW / 2f, baseY),
                                        4.dp.toPx()
                                )

                                // Terminal scan lines inside monitor — curved paths with spring
                                // motion
                                val lFracs = listOf(0.35f, 0.80f, 0.55f, 0.70f, 0.40f)
                                val curveMagnitudes = listOf(0.12f, -0.08f, 0.10f, -0.06f, 0.09f)
                                for (i in lFracs.indices) {
                                        val progress = scanLineAnimatables[i].value
                                        val ly = monScreenY + (i + 1) * monScreenH / 6f
                                        val lineStartX = monScreenX + 8.dp.toPx()
                                        val lineEndX =
                                                lineStartX + monScreenW * lFracs[i] * progress
                                        val curvePeak = monScreenH * curveMagnitudes[i] * progress

                                        val scanLinePath =
                                                Path().apply {
                                                        moveTo(lineStartX, ly)
                                                        val midX = (lineStartX + lineEndX) / 2f
                                                        quadraticTo(
                                                                midX,
                                                                ly + curvePeak,
                                                                lineEndX,
                                                                ly
                                                        )
                                                }
                                        drawPath(
                                                scanLinePath,
                                                primary.copy(alpha = 0.25f * progress),
                                                style =
                                                        Stroke(
                                                                width = 2.dp.toPx(),
                                                                cap = StrokeCap.Round
                                                        )
                                        )
                                }

                                // Shield + check on monitor screen
                                val shX = monitorCx
                                val shY = monScreenY + monScreenH / 2f
                                val shR = min(monScreenW, monScreenH) * 0.18f
                                val shield =
                                        Path().apply {
                                                moveTo(shX - shR, shY - shR * 0.8f)
                                                lineTo(shX + shR, shY - shR * 0.8f)
                                                lineTo(shX + shR, shY + shR * 0.2f)
                                                lineTo(shX, shY + shR)
                                                lineTo(shX - shR, shY + shR * 0.2f)
                                                close()
                                        }
                                drawPath(shield, primary, style = Stroke(2.dp.toPx()))
                                val check =
                                        Path().apply {
                                                moveTo(shX - shR * 0.4f, shY)
                                                lineTo(shX - shR * 0.1f, shY + shR * 0.3f)
                                                lineTo(shX + shR * 0.4f, shY - shR * 0.3f)
                                        }
                                drawPath(check, primary, style = Stroke(2.dp.toPx()))

                                // Phone frame
                                drawRoundRect(
                                        color = secondary,
                                        topLeft = Offset(phoneX, phoneY),
                                        size = Size(phoneW, phoneH),
                                        cornerRadius = phoneCorner,
                                        style = Stroke(2.dp.toPx())
                                )

                                // Checkmark on phone screen
                                val pChkCx = phoneCx
                                val pChkCy = phoneCy
                                val pChkR = min(pScreenW, pScreenH) * 0.18f
                                drawCircle(
                                        secondary,
                                        pChkR * 1.3f,
                                        Offset(pChkCx, pChkCy),
                                        style = Stroke(1.5.dp.toPx())
                                )
                                val pCheck =
                                        Path().apply {
                                                moveTo(pChkCx - pChkR * 0.5f, pChkCy)
                                                lineTo(pChkCx - pChkR * 0.1f, pChkCy + pChkR * 0.4f)
                                                lineTo(pChkCx + pChkR * 0.5f, pChkCy - pChkR * 0.4f)
                                        }
                                drawPath(pCheck, secondary, style = Stroke(2.dp.toPx()))
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
                                                                primaryContainer,
                                                                secondaryContainer
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
                                        secondaryContainer,
                                        Offset(monitorCx, monitorY + monitorH),
                                        Offset(monitorCx, monitorY + monitorH + monitorH * 0.18f),
                                        5.dp.toPx()
                                )
                                val stBaseY = monitorY + monitorH + monitorH * 0.18f
                                val stBaseW = monitorW * 0.35f
                                drawLine(
                                        secondaryContainer,
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
                                        onPrimary,
                                        Offset(phoneX, phoneY),
                                        Size(phoneW, phoneH),
                                        phoneCorner
                                )
                                // Phone screen
                                drawRoundRect(
                                        background,
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
                                        secondary.copy(alpha = glowAlpha),
                                        style = Stroke(glowWidth, cap = StrokeCap.Round)
                                )

                                // Core dashed line
                                val dashPhase = streamOffset.value * 80f
                                val coreAlpha = 0.25f + glowIntensity * 0.6f
                                val corePaint =
                                        Paint().apply {
                                                color = secondary.copy(alpha = coreAlpha)
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
                                                secondary.copy(alpha = a),
                                                sp.radius.dp.toPx(),
                                                pos
                                        )
                                        drawCircle(
                                                secondary.copy(alpha = a * 0.3f),
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
                                                        tertiary.copy(
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
                                                        secondary.copy(
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
                                                                primary.copy(alpha = 0.07f)
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
                                                remMeasured,
                                                color = Color.White,
                                                topLeft = Offset(remXPos, remYPos)
                                        )
                                        drawText(
                                                exMeasured,
                                                color = Color.White,
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
                                        primary.copy(alpha = 0.22f),
                                        scanRadius,
                                        phoneScanCenter,
                                        style = Stroke(18.dp.toPx())
                                )
                                drawCircle(
                                        primary,
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
                                                                secondary.copy(alpha = 0.10f)
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
                                        // Overwrite REM/EX with bright white + add completions
                                        drawText(
                                                remMeasured,
                                                color = Color.White,
                                                topLeft = Offset(remXPos, remYPos)
                                        )
                                        drawText(
                                                exMeasured,
                                                color = Color.White,
                                                topLeft = Offset(exXPos, exYPos)
                                        )
                                        // Baseline-align completions with their bold counterparts
                                        drawText(
                                                oteMeasured,
                                                color = primary.copy(alpha = 0.95f),
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
                                                color = primary.copy(alpha = 0.95f),
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
                                        secondary.copy(alpha = 0.18f),
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
                        // PHASE 4: Fade overlay (drawn on top of everything)
                        // ═════════════════════════════════════════════════════════════
                        if (fadeOverlay.value > 0f) {
                                drawRect(
                                        color = background.copy(alpha = fadeOverlay.value),
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

@Preview(showBackground = true)
@Composable
private fun SplashScreenPreview() {
    RemExTheme {
        SplashScreen(onFinished = {})
    }
}
