package com.clindsay94.remex.ui.screens

import androidx.compose.animation.core.*
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.*
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.sin
import kotlin.math.PI

private data class Particle(
    var x: Float,
    var y: Float,
    var vx: Float,
    var vy: Float,
    var alpha: Float,
    var lifetime: Float,
    var maxLifetime: Float
)

private data class FloatingShape(
    var x: Float,
    var y: Float,
    var vx: Float,
    var vy: Float,
    var rotation: Float,
    var rotationSpeed: Float,
    var size: Float,
    var alpha: Float,
    val sides: Int // 3=triangle, 4=diamond, 6=hexagon
)

@Composable
fun SplashScreen(onFinished: () -> Unit) {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current

    val primary = MaterialTheme.colorScheme.primary
    val secondary = MaterialTheme.colorScheme.secondary
    val background = MaterialTheme.colorScheme.background
    val substrateColor = Color(0xFF050508)
    val neonColor = primary.copy(alpha = 1f) // Theme-aware: uses Material primary color

    var isSkipping by remember { mutableStateOf(false) }
    val skipAlpha = remember { Animatable(1f) }

    // Particle system state (12 ember particles)
    val particles = remember {
        val rng = java.util.Random(42L)
        List(12) {
            Particle(
                x = rng.nextFloat(),         // normalized 0..1, resolved in canvas
                y = 0.4f + rng.nextFloat() * 0.6f,
                vx = (rng.nextFloat() - 0.5f) * 0.016f,
                vy = -(0.02f + rng.nextFloat() * 0.02f),
                alpha = rng.nextFloat() * 0.7f,
                lifetime = rng.nextFloat() * 2.0f,
                maxLifetime = 1.5f + rng.nextFloat() * 1.5f
            )
        }.toMutableList()
    }
    var particleFrame by remember { mutableStateOf(0) }

    // Floating geometric shapes for ambient depth layer
    val floatingShapes = remember {
        val rng = java.util.Random(77L)
        val shapeTypes = intArrayOf(3, 4, 6)
        List(8) {
            FloatingShape(
                x = rng.nextFloat(),
                y = rng.nextFloat(),
                vx = (rng.nextFloat() - 0.5f) * 0.003f,
                vy = (rng.nextFloat() - 0.5f) * 0.002f,
                rotation = rng.nextFloat() * 360f,
                rotationSpeed = (rng.nextFloat() - 0.5f) * 1.5f,
                size = 0.03f + rng.nextFloat() * 0.04f,
                alpha = 0.03f + rng.nextFloat() * 0.05f,
                sides = shapeTypes[rng.nextInt(shapeTypes.size)]
            )
        }.toMutableList()
    }

    suspend fun skipSplash() {
        if (isSkipping) return
        isSkipping = true
        skipAlpha.animateTo(0f, tween(300, easing = LinearEasing))
        onFinished()
    }

    // Progress 0.0 to 1.0 (Scanline moving down)
    val scanProgress = remember { Animatable(-0.2f) }
    val waveProgress = remember { Animatable(-0.2f) }
    
    // Handshake states
    val logoScale = remember { Animatable(1f) }
    val logoElevation = remember { Animatable(10f) }
    val fillScreenScale = remember { Animatable(0f) }

    LaunchedEffect(Unit) {
        // Particle update loop (~60fps until waveProgress > 0.5)
        scope.launch {
            val rng = java.util.Random(99L)
            while (waveProgress.value < 0.5f && !isSkipping) {
                val dt = 0.016f
                for (p in particles) {
                    p.lifetime += dt
                    p.x += p.vx
                    p.y += p.vy * dt
                    val t = p.lifetime / p.maxLifetime
                    p.alpha = when {
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
                // Update floating shapes
                for (s in floatingShapes) {
                    s.x += s.vx
                    s.y += s.vy
                    s.rotation += s.rotationSpeed
                    // Wrap around screen edges
                    if (s.x < -0.1f) s.x = 1.1f
                    if (s.x > 1.1f) s.x = -0.1f
                    if (s.y < -0.1f) s.y = 1.1f
                    if (s.y > 1.1f) s.y = -0.1f
                }
                particleFrame++
                delay(16L)
            }
        }

        // Phase 2: Scanline
        scope.launch {
            scanProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing))
        }
        
        // Phase 3: Materialization wave (delayed by 200ms)
        delay(800)
        if (!isSkipping) {
            waveProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing))
            
            if (!isSkipping) {
                // Phase 4: Handshake
                // Scale down
                scope.launch {
                    logoElevation.animateTo(20f, tween(300, easing = FastOutSlowInEasing))
                }
                logoScale.animateTo(0.9f, tween(300, easing = FastOutSlowInEasing))
                
                if (!isSkipping) {
                    // Snap & Expand
                    scope.launch {
                        logoElevation.animateTo(0f, tween(150))
                    }
                    logoScale.animateTo(1.1f, spring(dampingRatio = Spring.DampingRatioMediumBouncy, stiffness = Spring.StiffnessMedium))
                    
                    if (!isSkipping) {
                        fillScreenScale.animateTo(1f, tween(500, easing = FastOutSlowInEasing))
                        onFinished()
                    }
                }
            }
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(substrateColor)
            .pointerInput(Unit) {
                detectTapGestures {
                    scope.launch { skipSplash() }
                }
            }
            .alpha(skipAlpha.value),
        contentAlignment = Alignment.Center
    ) {
        Canvas(modifier = Modifier.fillMaxSize()) {
            val width = size.width
            val height = size.height

            // 1. The Dark Substrate (Micro-grid)
            val gridSize = 10.dp.toPx()
            val gridColor = Color.White.copy(alpha = 0.05f)
            for (x in 0..width.toInt() step gridSize.toInt()) {
                drawLine(gridColor, Offset(x.toFloat(), 0f), Offset(x.toFloat(), height), 1f)
            }
            for (y in 0..height.toInt() step gridSize.toInt()) {
                drawLine(gridColor, Offset(0f, y.toFloat()), Offset(width, y.toFloat()), 1f)
            }

            // 1a. Floating geometric shapes (ambient depth layer)
            @Suppress("UNUSED_EXPRESSION")
            particleFrame // trigger recomposition
            for (shape in floatingShapes) {
                val sx = shape.x * width
                val sy = shape.y * height
                val shapeRadius = shape.size * minOf(width, height)
                val shapePath = Path()
                for (i in 0 until shape.sides) {
                    val angle = (shape.rotation + i * 360f / shape.sides) * (PI.toFloat() / 180f)
                    val px = sx + shapeRadius * cos(angle)
                    val py = sy + shapeRadius * sin(angle)
                    if (i == 0) shapePath.moveTo(px, py) else shapePath.lineTo(px, py)
                }
                shapePath.close()
                drawPath(
                    path = shapePath,
                    color = neonColor.copy(alpha = shape.alpha),
                    style = Stroke(width = 1f)
                )
            }

            val scanY = height * scanProgress.value
            val waveY = height * waveProgress.value

            // 1b. Radial grid: 4 concentric circles + 8 radial lines (revealed by scanline)
            val radialColor = neonColor.copy(alpha = 0.03f)
            val maxRadius = minOf(width, height) * 0.45f
            val radialFractions = listOf(0.25f, 0.45f, 0.65f, 0.85f)
            for (rf in radialFractions) {
                val rad = rf * maxRadius
                // Only draw ring if its center region is above scanY
                if (center.y - rad < scanY) {
                    drawCircle(
                        color = radialColor,
                        radius = rad,
                        center = center,
                        style = Stroke(width = 1f)
                    )
                }
            }
            // 8 radial lines at 45° increments
            for (i in 0 until 8) {
                val angle = i * (PI / 4).toFloat()
                val ex = center.x + maxRadius * cos(angle)
                val ey = center.y + maxRadius * sin(angle)
                // Draw if the far endpoint region is above scanY
                if (minOf(center.y, ey) < scanY) {
                    drawLine(
                        color = radialColor,
                        start = center,
                        end = Offset(ex, ey),
                        strokeWidth = 1f
                    )
                }
            }

            // 1c. Particle embers (only while waveProgress is early)
            if (waveProgress.value < 0.5f) {
                @Suppress("UNUSED_EXPRESSION")
                particleFrame // read state so recomposition triggers on update
                for (p in particles) {
                    if (p.alpha > 0.02f) {
                        drawCircle(
                            color = neonColor.copy(alpha = p.alpha),
                            radius = 1.5.dp.toPx(),
                            center = Offset(p.x * width, p.y * height)
                        )
                    }
                }
            }

            val baseLogoSize = 140.dp.toPx()
            val logoSize = baseLogoSize * logoScale.value
            val half = logoSize / 2f
            val logoRectX = center.x - half
            val logoRectY = center.y - half

            // Closure to draw Wireframe
            val drawWireframe = {
                // Monitor Frame
                drawRoundRect(
                    color = neonColor,
                    topLeft = Offset(center.x - logoSize * 0.6f, center.y - logoSize * 0.4f),
                    size = Size(logoSize * 1.2f, logoSize * 0.7f),
                    cornerRadius = CornerRadius(logoSize * 0.1f),
                    style = Stroke(width = 3.dp.toPx())
                )
                // Monitor Stand
                drawLine(
                    color = neonColor,
                    start = Offset(center.x, center.y + logoSize * 0.3f),
                    end = Offset(center.x, center.y + logoSize * 0.5f),
                    strokeWidth = 6.dp.toPx()
                )
                drawLine(
                    color = neonColor,
                    start = Offset(center.x - logoSize * 0.2f, center.y + logoSize * 0.5f),
                    end = Offset(center.x + logoSize * 0.2f, center.y + logoSize * 0.5f),
                    strokeWidth = 4.dp.toPx()
                )
                // Phone
                drawRoundRect(
                    color = neonColor,
                    topLeft = Offset(center.x + logoSize * 0.3f, center.y + logoSize * 0.1f),
                    size = Size(logoSize * 0.35f, logoSize * 0.6f),
                    cornerRadius = CornerRadius(logoSize * 0.08f),
                    style = Stroke(width = 2.dp.toPx())
                )
            }

            // Closure to draw Solid Material logo
            val drawSolid = {
                val elevationOffset = logoElevation.value.dp.toPx()
                
                // Monitor Shadow
                drawRoundRect(
                    color = Color.Black.copy(alpha = 0.4f),
                    topLeft = Offset(center.x - logoSize * 0.6f, center.y - logoSize * 0.4f + elevationOffset),
                    size = Size(logoSize * 1.2f, logoSize * 0.7f),
                    cornerRadius = CornerRadius(logoSize * 0.1f)
                )
                // Phone Shadow
                drawRoundRect(
                    color = Color.Black.copy(alpha = 0.4f),
                    topLeft = Offset(center.x + logoSize * 0.3f, center.y + logoSize * 0.1f + elevationOffset),
                    size = Size(logoSize * 0.35f, logoSize * 0.6f),
                    cornerRadius = CornerRadius(logoSize * 0.08f)
                )

                // Monitor Frame Solid
                drawRoundRect(
                    brush = Brush.linearGradient(
                        colors = listOf(primary, secondary),
                        start = Offset(center.x - logoSize * 0.6f, center.y - logoSize * 0.4f),
                        end = Offset(center.x + logoSize * 0.6f, center.y + logoSize * 0.3f)
                    ),
                    topLeft = Offset(center.x - logoSize * 0.6f, center.y - logoSize * 0.4f),
                    size = Size(logoSize * 1.2f, logoSize * 0.7f),
                    cornerRadius = CornerRadius(logoSize * 0.1f)
                )
                // Monitor Screen Inner (Background color)
                drawRoundRect(
                    color = substrateColor,
                    topLeft = Offset(center.x - logoSize * 0.55f, center.y - logoSize * 0.35f),
                    size = Size(logoSize * 1.1f, logoSize * 0.6f),
                    cornerRadius = CornerRadius(logoSize * 0.05f)
                )

                // Monitor Stand Solid
                drawLine(
                    color = secondary,
                    start = Offset(center.x, center.y + logoSize * 0.3f),
                    end = Offset(center.x, center.y + logoSize * 0.5f),
                    strokeWidth = 6.dp.toPx()
                )
                drawLine(
                    color = secondary,
                    start = Offset(center.x - logoSize * 0.2f, center.y + logoSize * 0.5f),
                    end = Offset(center.x + logoSize * 0.2f, center.y + logoSize * 0.5f),
                    strokeWidth = 4.dp.toPx()
                )

                // Phone Solid
                drawRoundRect(
                    color = primary,
                    topLeft = Offset(center.x + logoSize * 0.3f, center.y + logoSize * 0.1f),
                    size = Size(logoSize * 0.35f, logoSize * 0.6f),
                    cornerRadius = CornerRadius(logoSize * 0.08f)
                )
                // Phone Screen Inner
                drawRoundRect(
                    color = background,
                    topLeft = Offset(center.x + logoSize * 0.33f, center.y + logoSize * 0.13f),
                    size = Size(logoSize * 0.29f, logoSize * 0.54f),
                    cornerRadius = CornerRadius(logoSize * 0.04f)
                )
            }

            // Zone 1: Below scanY -> Substrate (nothing drawn)
            // Zone 2: Between waveY and scanY -> Wireframe
            if (scanY > logoRectY && waveY < logoRectY + logoSize) {
                clipRect(top = waveY, bottom = scanY) {
                    drawWireframe()
                }
            }

            // Zone 3: Above waveY -> Solid
            if (waveY > logoRectY) {
                clipRect(top = 0f, bottom = waveY) {
                    drawSolid()

                    // Connection trace arcs: monitor corners + phone corner → phone center
                    val traceColor = neonColor.copy(alpha = 0.20f)
                    val strokeW = 1.5.dp.toPx()
                    val dashIntervals = floatArrayOf(8f, 8f)
                    val tracePaint = androidx.compose.ui.graphics.Paint().also {
                        it.color = traceColor
                        it.style = androidx.compose.ui.graphics.PaintingStyle.Stroke
                        it.strokeWidth = strokeW
                        it.pathEffect = PathEffect.dashPathEffect(dashIntervals, 0f)
                    }
                    // Three anchor points: monitor top-left, monitor top-right, phone top
                    val monitorTopLeft = Offset(center.x - logoSize * 0.6f, center.y - logoSize * 0.4f)
                    val monitorTopRight = Offset(center.x + logoSize * 0.6f, center.y - logoSize * 0.4f)
                    val phoneTop = Offset(center.x + logoSize * 0.475f, center.y + logoSize * 0.1f)
                    val phoneCenter = Offset(center.x + logoSize * 0.475f, center.y + logoSize * 0.4f)

                    val traceAnchors = listOf(monitorTopLeft, monitorTopRight, phoneTop)
                    drawContext.canvas.nativeCanvas.let { } // ensure canvas context
                    for (anchor in traceAnchors) {
                        val path = androidx.compose.ui.graphics.Path()
                        path.moveTo(anchor.x, anchor.y)
                        // Quadratic bezier toward phone center
                        val ctrlX = (anchor.x + phoneCenter.x) / 2f
                        val ctrlY = anchor.y
                        path.quadraticBezierTo(ctrlX, ctrlY, phoneCenter.x, phoneCenter.y)
                        drawContext.canvas.drawPath(path, tracePaint)
                    }
                }
            }

            // Draw Scanline (Phase 2)
            if (scanY > 0f && scanY < height) {
                drawLine(
                    color = neonColor,
                    start = Offset(0f, scanY),
                    end = Offset(width, scanY),
                    strokeWidth = 3.dp.toPx()
                )
                // Glow trail
                drawRect(
                    brush = Brush.verticalGradient(
                        colors = listOf(Color.Transparent, neonColor.copy(alpha = 0.4f)),
                        startY = scanY - 160.dp.toPx(),
                        endY = scanY
                    ),
                    topLeft = Offset(0f, scanY - 160.dp.toPx()),
                    size = Size(width, 160.dp.toPx())
                )
            }

            // Draw Material Wave line (Phase 3)
            if (waveY > 0f && waveY < height) {
                // Refractive / Glass edge
                drawLine(
                    color = Color.White.copy(alpha = 0.8f),
                    start = Offset(0f, waveY),
                    end = Offset(width, waveY),
                    strokeWidth = 2.dp.toPx()
                )
                drawRect(
                    brush = Brush.verticalGradient(
                        colors = listOf(Color.Transparent, primary.copy(alpha = 0.3f)),
                        startY = waveY - 120.dp.toPx(),
                        endY = waveY
                    ),
                    topLeft = Offset(0f, waveY - 120.dp.toPx()),
                    size = Size(width, 120.dp.toPx())
                )
            }

            // Phase 4: Explode to fill screen
            if (fillScreenScale.value > 0f) {
                val maxRadius = hypot(center.x, center.y) * 1.5f
                val currentRadius = logoSize * 0.25f + (maxRadius * fillScreenScale.value)
                drawCircle(
                    color = background,
                    radius = currentRadius,
                    center = center
                )
            }
        }
    }
}
