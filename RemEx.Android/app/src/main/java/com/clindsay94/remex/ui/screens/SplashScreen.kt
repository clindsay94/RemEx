package com.clindsay94.remex.ui.screens

import androidx.compose.animation.core.*
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.*
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.sin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// ─── Data classes ─────────────────────────────────────────────────────────────

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
        val sides: Int
)

private data class StreamParticle(
        var t: Float, // 0..1 along Bezier
        var speed: Float,
        var alpha: Float,
        var radius: Float
)

// ─── Font helpers ─────────────────────────────────────────────────────────────

private val victorMonoFamily: FontFamily by lazy {
    FontFamily(
            Font(R.font.victor_mono_bold, FontWeight.Bold),
            Font(R.font.victor_mono_bold, FontWeight.Bold, FontStyle.Italic),
            Font(R.font.victor_mono_bold, FontWeight.Normal, FontStyle.Italic)
    )
}

// ─── Bezier helpers ───────────────────────────────────────────────────────────

/** Evaluate a cubic Bezier at parameter t. */
private fun cubicBezier(p0: Offset, p1: Offset, p2: Offset, p3: Offset, t: Float): Offset {
    val u = 1f - t
    val tt = t * t
    val uu = u * u
    val uuu = uu * u
    val ttt = tt * t
    return Offset(
            uuu * p0.x + 3f * uu * t * p1.x + 3f * u * tt * p2.x + ttt * p3.x,
            uuu * p0.y + 3f * uu * t * p1.y + 3f * u * tt * p2.y + ttt * p3.y
    )
}

// ═════════════════════════════════════════════════════════════════════════════
// SPLASH SCREEN COMPOSABLE
// ═════════════════════════════════════════════════════════════════════════════

@Composable
fun SplashScreen(onFinished: () -> Unit) {
    val context = LocalContext.current
    @Suppress("UNUSED_VARIABLE") val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    @Suppress("UNUSED_VARIABLE") val density = LocalDensity.current

    // ── Material 3 color roles (strategic assignment) ────────────────────────
    val primary = MaterialTheme.colorScheme.primary
    val secondary = MaterialTheme.colorScheme.secondary
    val tertiary = MaterialTheme.colorScheme.tertiary
    val primaryContainer = MaterialTheme.colorScheme.primaryContainer
    val secondaryContainer = MaterialTheme.colorScheme.secondaryContainer
    val onPrimary = MaterialTheme.colorScheme.onPrimary
    val background = MaterialTheme.colorScheme.background
    val onBackground = MaterialTheme.colorScheme.onBackground
    val surface = MaterialTheme.colorScheme.surface
    val surfaceVariant = MaterialTheme.colorScheme.surfaceVariant
    val substrateColor = Color(0xFF050508)

    val textMeasurer = rememberTextMeasurer()

    // ── Text styles (Victor Mono via local font asset) ───────────────────────────
    val brandMainStyle =
            TextStyle(
                    fontFamily = victorMonoFamily,
                    fontWeight = FontWeight.Bold,
                    fontSize = 68.sp,
                    letterSpacing = 4.sp
            )
    val brandCompleteStyle =
            TextStyle(
                    fontFamily = victorMonoFamily,
                    fontWeight = FontWeight.Bold,
                    fontStyle = FontStyle.Italic,
                    fontSize = 48.sp,
                    letterSpacing = 1.5.sp
            )
    val taglineStyle =
            TextStyle(
                    fontFamily = victorMonoFamily,
                    fontWeight = FontWeight.Normal,
                    fontStyle = FontStyle.Italic,
                    fontSize = 20.sp,
                    letterSpacing = 0.8.sp
            )
    val chipTextStyle =
            TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Normal,
                    fontSize = 10.sp
            )

    // ── Pre-measured text ────────────────────────────────────────────────────
    val remMeasured = remember(textMeasurer) { textMeasurer.measure("REM", brandMainStyle) }
    val exMeasured = remember(textMeasurer) { textMeasurer.measure("EX", brandMainStyle) }
    val oteMeasured = remember(textMeasurer) { textMeasurer.measure("(ote)", brandCompleteStyle) }
    val ecuMeasured =
            remember(textMeasurer) { textMeasurer.measure("(ecution)", brandCompleteStyle) }
    val commandMeasured = remember(textMeasurer) { textMeasurer.measure("Command ", taglineStyle) }
    val yourPcMeasured = remember(textMeasurer) { textMeasurer.measure("Your PC", taglineStyle) }
    val cpuLabelMeasured = remember(textMeasurer) { textMeasurer.measure("CPU", chipTextStyle) }

    // ── Skip state ──────────────────────────────────────────────────────────
    var isSkipping by remember { mutableStateOf(false) }
    val skipAlpha = remember { Animatable(1f) }

    // ── Particle system (12 embers) ─────────────────────────────────────────
    val particles = remember {
        val rng = java.util.Random(42L)
        MutableList(12) {
            Particle(
                    x = rng.nextFloat(),
                    y = 0.4f + rng.nextFloat() * 0.6f,
                    vx = (rng.nextFloat() - 0.5f) * 0.016f,
                    vy = -(0.02f + rng.nextFloat() * 0.02f),
                    alpha = rng.nextFloat() * 0.7f,
                    lifetime = rng.nextFloat() * 2.0f,
                    maxLifetime = 1.5f + rng.nextFloat() * 1.5f
            )
        }
    }
    var particleFrame by remember { mutableStateOf(0) }

    // ── Floating geometric shapes ───────────────────────────────────────────
    val floatingShapes = remember {
        val rng = java.util.Random(77L)
        val types = intArrayOf(3, 4, 6)
        MutableList(8) {
            FloatingShape(
                    x = rng.nextFloat(),
                    y = rng.nextFloat(),
                    vx = (rng.nextFloat() - 0.5f) * 0.003f,
                    vy = (rng.nextFloat() - 0.5f) * 0.002f,
                    rotation = rng.nextFloat() * 360f,
                    rotationSpeed = (rng.nextFloat() - 0.5f) * 1.5f,
                    size = 0.03f + rng.nextFloat() * 0.04f,
                    alpha = 0.03f + rng.nextFloat() * 0.05f,
                    sides = types[rng.nextInt(types.size)]
            )
        }
    }

    // ── Stream particles (energy flow along connection curve) ────────────────
    val streamParticles = remember {
        val rng = java.util.Random(200L)
        MutableList(18) {
            StreamParticle(
                    t = rng.nextFloat(),
                    speed = 0.003f + rng.nextFloat() * 0.004f,
                    alpha = 0.3f + rng.nextFloat() * 0.5f,
                    radius = 1.5f + rng.nextFloat() * 2.5f
            )
        }
    }

    // ── Skip helper ─────────────────────────────────────────────────────────
    suspend fun skipSplash() {
        if (isSkipping) return
        isSkipping = true
        skipAlpha.animateTo(0f, tween(300, easing = LinearEasing))
        onFinished()
    }

    // ── Animation state ─────────────────────────────────────────────────────
    val scanProgress = remember { Animatable(-0.2f) } // Phase 1: from phone
    val waveProgress = remember { Animatable(-0.2f) } // Phase 2: from monitor
    val streamOffset = remember { Animatable(0f) } // dash animation
    val connectionGlow = remember { Animatable(0f) } // Phase 3: glow intensity 0->1
    val zoomScale = remember { Animatable(1f) } // Phase 4: pull-in scale
    val zoomProgress = remember { Animatable(0f) } // Phase 4: pull-in translate 0->1
    val fadeOverlay = remember { Animatable(0f) } // Phase 4: final fade 0->1

    // ── Animation orchestration ─────────────────────────────────────────────
    LaunchedEffect(Unit) {
        // Stream offset loop (dashes along connection traces)
        scope.launch {
            while (!isSkipping) {
                streamOffset.animateTo(1f, tween(2000, easing = LinearEasing))
                streamOffset.snapTo(0f)
            }
        }

        // Particle + stream-particle update loop (~60 fps)
        scope.launch {
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
                // Floating shapes
                for (s in floatingShapes) {
                    s.x += s.vx
                    s.y += s.vy
                    s.rotation += s.rotationSpeed
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
                particleFrame++
                delay(16L)
            }
        }

        // Phase 1: Scan radar from phone (bottom-right)
        scope.launch { scanProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing)) }

        // Phase 2: Wave radar from monitor (top-left), delayed 800ms
        delay(800)
        if (!isSkipping) {
            waveProgress.animateTo(1.2f, tween(2000, easing = FastOutLinearInEasing))

            if (!isSkipping) {
                // Phase 3: Connection flash/glow
                connectionGlow.animateTo(1f, tween(400, easing = FastOutSlowInEasing))

                if (!isSkipping) {
                    // Phase 4: Monitor Pull-In
                    scope.launch {
                        zoomScale.animateTo(6f, tween(700, easing = FastOutSlowInEasing))
                    }
                    scope.launch {
                        zoomProgress.animateTo(1f, tween(700, easing = FastOutSlowInEasing))
                    }
                    // Fade overlay starts slightly after zoom begins
                    delay(300)
                    if (!isSkipping) {
                        fadeOverlay.animateTo(1f, tween(400, easing = LinearEasing))
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
                            // Translate so the monitor screen center becomes viewport center
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
            val connEnd = Offset(monitorCx + monitorW * 0.3f, monitorCy + monitorH * 0.3f)
            val connCtrl1 = Offset(phoneCx - width * 0.15f, phoneCy - height * 0.25f)
            val connCtrl2 = Offset(monitorCx + width * 0.2f, monitorCy + height * 0.15f)

            // ─── Text positioning (centered between devices, stacked) ────
            val textBlockCy = height * 0.48f
            val textBlockCx = width * 0.50f

            // Line 1: "REM" + "(ote)"
            val line1W = remMeasured.size.width.toFloat() + oteMeasured.size.width.toFloat()
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
                drawCircle(color = traceColor, radius = 4.dp.toPx(), center = Offset(tx, ty))
            }

            // Network topology nodes — surfaceVariant
            val nodeColor = surfaceVariant.copy(alpha = 0.07f)
            val nodeRng = java.util.Random(123L)
            val netNodes =
                    List(7) { Offset(nodeRng.nextFloat() * width, nodeRng.nextFloat() * height) }
            for (i in netNodes.indices) {
                for (j in i + 1 until netNodes.size) {
                    if (hypot(netNodes[i].x - netNodes[j].x, netNodes[i].y - netNodes[j].y) <
                                    width * 0.35f
                    ) {
                        drawLine(nodeColor, netNodes[i], netNodes[j], 1f)
                    }
                }
            }
            for (n in netNodes) drawCircle(nodeColor, 3.dp.toPx(), n)

            // Floating shapes — tertiary
            @Suppress("UNUSED_EXPRESSION") particleFrame
            for (shape in floatingShapes) {
                val shx = shape.x * width
                val shy = shape.y * height
                val r = shape.size * min(width, height)
                val sp = Path()
                for (i in 0 until shape.sides) {
                    val a = (shape.rotation + i * 360f / shape.sides) * (PI.toFloat() / 180f)
                    val px = shx + r * cos(a)
                    val py = shy + r * sin(a)
                    if (i == 0) sp.moveTo(px, py) else sp.lineTo(px, py)
                }
                sp.close()
                drawPath(sp, tertiary.copy(alpha = shape.alpha), style = Stroke(1f))
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
                        Offset(center.x + maxRad * cos(a), center.y + maxRad * sin(a)),
                        1f
                )
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

            // CPU chips — surface
            val chipColor = surface.copy(alpha = 0.12f)
            val chipStroke = Stroke(1.dp.toPx())
            val pinCnt = 5

            // Top-right chip
            val trCTop = Offset(width - 100.dp.toPx(), 30.dp.toPx())
            val trCSz = Size(70.dp.toPx(), 50.dp.toPx())
            drawRect(chipColor, trCTop, trCSz, style = chipStroke)
            drawText(
                    cpuLabelMeasured,
                    surface.copy(alpha = 0.10f),
                    Offset(
                            trCTop.x + trCSz.width / 2f - cpuLabelMeasured.size.width / 2f,
                            trCTop.y + trCSz.height / 2f - cpuLabelMeasured.size.height / 2f
                    )
            )
            val trPs = trCSz.height / (pinCnt + 1)
            for (i in 1..pinCnt) {
                val py = trCTop.y + i * trPs
                drawLine(chipColor, Offset(trCTop.x - 8.dp.toPx(), py), Offset(trCTop.x, py), 1f)
                drawLine(
                        chipColor,
                        Offset(trCTop.x + trCSz.width, py),
                        Offset(trCTop.x + trCSz.width + 8.dp.toPx(), py),
                        1f
                )
            }
            drawLine(
                    chipColor,
                    Offset(trCTop.x + trCSz.width / 2f, trCTop.y),
                    Offset(trCTop.x + trCSz.width / 2f, trCTop.y - 20.dp.toPx()),
                    1f
            )
            drawLine(
                    chipColor,
                    Offset(trCTop.x + trCSz.width, trCTop.y + trCSz.height / 2f),
                    Offset(trCTop.x + trCSz.width + 30.dp.toPx(), trCTop.y + trCSz.height / 2f),
                    1f
            )

            // Bottom-left chip
            val blCTop = Offset(20.dp.toPx(), height - 90.dp.toPx())
            val blCSz = Size(70.dp.toPx(), 50.dp.toPx())
            drawRect(chipColor, blCTop, blCSz, style = chipStroke)
            drawText(
                    cpuLabelMeasured,
                    surface.copy(alpha = 0.10f),
                    Offset(
                            blCTop.x + blCSz.width / 2f - cpuLabelMeasured.size.width / 2f,
                            blCTop.y + blCSz.height / 2f - cpuLabelMeasured.size.height / 2f
                    )
            )
            val blPs = blCSz.height / (pinCnt + 1)
            for (i in 1..pinCnt) {
                val py = blCTop.y + i * blPs
                drawLine(chipColor, Offset(blCTop.x - 8.dp.toPx(), py), Offset(blCTop.x, py), 1f)
                drawLine(
                        chipColor,
                        Offset(blCTop.x + blCSz.width, py),
                        Offset(blCTop.x + blCSz.width + 8.dp.toPx(), py),
                        1f
                )
            }
            drawLine(
                    chipColor,
                    Offset(blCTop.x + blCSz.width / 2f, blCTop.y + blCSz.height),
                    Offset(blCTop.x + blCSz.width / 2f, blCTop.y + blCSz.height + 20.dp.toPx()),
                    1f
            )
            drawLine(
                    chipColor,
                    Offset(blCTop.x, blCTop.y + blCSz.height / 2f),
                    Offset(blCTop.x - 30.dp.toPx(), blCTop.y + blCSz.height / 2f),
                    1f
            )

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

                // Terminal scan lines inside monitor
                val lFracs = listOf(0.35f, 0.80f, 0.55f, 0.70f, 0.40f)
                for (i in lFracs.indices) {
                    val ly = monScreenY + (i + 1) * monScreenH / 6f
                    drawLine(
                            primary.copy(alpha = 0.25f),
                            Offset(monScreenX + 8.dp.toPx(), ly),
                            Offset(monScreenX + 8.dp.toPx() + monScreenW * lFracs[i], ly),
                            2.dp.toPx()
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
                                        listOf(primaryContainer, secondaryContainer),
                                        Offset(monitorX, monitorY),
                                        Offset(monitorX + monitorW, monitorY + monitorH)
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
                drawRoundRect(onPrimary, Offset(phoneX, phoneY), Size(phoneW, phoneH), phoneCorner)
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
                            strokeWidth = 1.5.dp.toPx() + glowIntensity * 2.dp.toPx()
                            pathEffect =
                                    PathEffect.dashPathEffect(floatArrayOf(10f, 10f), dashPhase)
                            strokeCap = StrokeCap.Round
                        }
                drawContext.canvas.drawPath(connPath, corePaint)

                // Stream particles traveling along the curve
                @Suppress("UNUSED_EXPRESSION") particleFrame
                for (sp in streamParticles) {
                    val pos = cubicBezier(connStart, connCtrl1, connCtrl2, connEnd, sp.t)
                    val a = sp.alpha * (0.5f + glowIntensity * 0.5f)
                    drawCircle(secondary.copy(alpha = a), sp.radius.dp.toPx(), pos)
                    drawCircle(secondary.copy(alpha = a * 0.3f), sp.radius.dp.toPx() * 2.5f, pos)
                }

                // Wi-Fi / signal arcs emanating from phone
                val wifiCenter = Offset(phoneCx, phoneY - 4.dp.toPx())
                for (arcIdx in 0 until 3) {
                    val arcRadius = 20.dp.toPx() + arcIdx * 18.dp.toPx()
                    val arcAlpha = (0.15f - arcIdx * 0.04f) + glowIntensity * 0.2f
                    drawArc(
                            color = tertiary.copy(alpha = arcAlpha.coerceIn(0f, 1f)),
                            startAngle = 210f,
                            sweepAngle = 120f,
                            useCenter = false,
                            topLeft = Offset(wifiCenter.x - arcRadius, wifiCenter.y - arcRadius),
                            size = Size(arcRadius * 2f, arcRadius * 2f),
                            style = Stroke(1.5.dp.toPx(), cap = StrokeCap.Round)
                    )
                }

                // Decorative offset trace
                val tracePaint2 =
                        Paint().apply {
                            color = secondary.copy(alpha = 0.12f + glowIntensity * 0.15f)
                            style = PaintingStyle.Stroke
                            strokeWidth = 1.dp.toPx()
                            pathEffect =
                                    PathEffect.dashPathEffect(
                                            floatArrayOf(6f, 12f),
                                            dashPhase * 0.7f
                                    )
                        }
                val conn2Start = Offset(connStart.x + 12.dp.toPx(), connStart.y - 8.dp.toPx())
                val conn2End = Offset(connEnd.x - 10.dp.toPx(), connEnd.y + 8.dp.toPx())
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
            // RADAR PARAMETERS
            // ═════════════════════════════════════════════════════════════
            val phoneScanCenter = Offset(phoneCx, phoneCy)
            val monitorWaveCenter = Offset(monitorCx, monitorCy)

            val maxRadiusPx = hypot(width, height)
            val scanRadius = maxRadiusPx * scanProgress.value.coerceAtLeast(0f)
            val waveRadius = maxRadiusPx * waveProgress.value.coerceAtLeast(0f)

            // ═════════════════════════════════════════════════════════════
            // PHASE 1: SCAN RADAR + WIREFRAME (from phone)
            // ═════════════════════════════════════════════════════════════
            if (scanRadius > 0f) {
                drawCircle(
                        brush =
                                Brush.radialGradient(
                                        listOf(Color.Transparent, primary.copy(alpha = 0.07f)),
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
                    drawText(remMeasured, color = Color.White, topLeft = Offset(remXPos, remYPos))
                    drawText(exMeasured, color = Color.White, topLeft = Offset(exXPos, exYPos))
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
                drawCircle(primary, scanRadius, phoneScanCenter, style = Stroke(2.5.dp.toPx()))
            }

            // ═════════════════════════════════════════════════════════════
            // PHASE 2: WAVE RADAR + SOLID + COMPLETION (from monitor)
            // ═════════════════════════════════════════════════════════════
            if (waveRadius > 0f) {
                drawCircle(
                        brush =
                                Brush.radialGradient(
                                        listOf(Color.Transparent, secondary.copy(alpha = 0.10f)),
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
                    drawText(remMeasured, color = Color.White, topLeft = Offset(remXPos, remYPos))
                    drawText(exMeasured, color = Color.White, topLeft = Offset(exXPos, exYPos))
                    // Baseline-align completions with their bold counterparts
                    drawText(
                            oteMeasured,
                            color = primary.copy(alpha = 0.95f),
                            topLeft =
                                    Offset(
                                            oteXPos,
                                            remYPos +
                                                    (remMeasured.size.height -
                                                            oteMeasured.size.height)
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
                                                            ecuMeasured.size.height)
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
                drawRect(color = background.copy(alpha = fadeOverlay.value), size = size)
            }
        }
    }
}
