package com.clindsay94.remex.ui.screens

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Matrix
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp
import androidx.graphics.shapes.Morph
import androidx.graphics.shapes.RoundedPolygon
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.sin

// ─────────────────────────────────────────────────────────────────────────────
// Shared particle/shape models + ambient draw helpers for the splash variants.
// Extracted verbatim from the former monolithic SplashScreen.kt (behavior-identical).
// `Particle` is shared by both variants (CosmicZoom starfield + RemexCommand embers);
// `FloatingShape` is used by RemexCommand's ambient layer.
// ─────────────────────────────────────────────────────────────────────────────

internal class Particle(
        var x: Float,
        var y: Float,
        var vx: Float,
        var vy: Float,
        var lifetime: Float,
        var maxLifetime: Float,
        var alpha: Float
)

internal class FloatingShape(
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

/** Cosmic Starfield: streaking white points radiating from screen center. */
internal fun DrawScope.drawCosmicZoomStarfield(particles: List<Particle>) {
        val width = size.width
        val height = size.height
        for (p in particles) {
                val sx = p.x * width
                val sy = p.y * height
                val dx = p.x - 0.5f
                val dy = p.y - 0.5f
                val dist = kotlin.math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
                val radius = 0.5f + dist * 3.5f

                drawCircle(
                        color = Color.White.copy(alpha = p.alpha),
                        radius = radius.dp.toPx(),
                        center = Offset(sx, sy)
                )
        }
}

/**
 * RemexCommand ambient background layer: circuit-board traces, network topology nodes,
 * floating Material-morph shapes, and the faint radial grid. Not themed here — the
 * colors are passed in by the caller exactly as the monolith supplied them.
 */
internal fun DrawScope.drawRemexCommandAmbientFx(
        floatingShapes: List<FloatingShape>,
        surface: Color,
        surfaceVariant: Color
) {
        val width = size.width
        val height = size.height

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
}

/** Draws the stenciled new "R" logo with glowing accents and optional gradient lightning bolt */
internal fun DrawScope.drawStylizedRLogo(
        w: Float,
        h: Float,
        scale: Float,
        accentColor: Color,
        opacity: Float,
        lightningFade: Float,
        elapsed: Float,
        shudderX: Float = 0f,
        shudderY: Float = 0f,
        yOffset: Float = -30f,
        customPos: Offset? = null
) {
        val tx = if (customPos != null) customPos.x + shudderX else w / 2f - 54f * scale + shudderX
        val ty = if (customPos != null) customPos.y + yOffset * scale + shudderY else h / 2f - 54f * scale + yOffset * scale + shudderY

        val rPath = Path().apply {
                moveTo(28f, 90f)
                lineTo(28f, 18f)
                lineTo(64f, 18f)
                arcTo(Rect(46f, 18f, 82f, 54f), 270f, 180f, false)
                lineTo(28f, 54f)

                moveTo(46f, 54f)
                lineTo(72f, 90f)
        }

        val pulse = kotlin.math.sin(elapsed * 8f) * 1.5f

        // Apply translation and scale transforms
        drawContext.canvas.save()
        drawContext.canvas.translate(tx, ty)
        drawContext.canvas.scale(scale, scale)

        // Glow backing
        drawPath(
                path = rPath,
                color = accentColor.copy(alpha = opacity * 0.35f),
                style = Stroke(
                        width = 8f + pulse,
                        cap = StrokeCap.Round,
                        join = StrokeJoin.Round
                )
        )
        // Main accent line
        drawPath(
                path = rPath,
                color = accentColor.copy(alpha = opacity),
                style = Stroke(
                        width = 4.5f,
                        cap = StrokeCap.Round,
                        join = StrokeJoin.Round
                )
        )
        // Core white line
        drawPath(
                path = rPath,
                color = Color.White.copy(alpha = opacity),
                style = Stroke(
                        width = 1.5f,
                        cap = StrokeCap.Round,
                        join = StrokeJoin.Round
                )
        )

        // Lightning bolt overlay
        if (lightningFade > 0f) {
                val lightningPath = Path().apply {
                        moveTo(31.5f + 24f, 9f)
                        lineTo(31.5f + 24f, 58.5f)
                        lineTo(45f + 24f, 58.5f)
                        lineTo(45f + 24f, 99f)
                        lineTo(76.5f + 24f, 45f)
                        lineTo(58.5f + 24f, 45f)
                        lineTo(76.5f + 24f, 9f)
                        close()
                }

                val goldBrush = Brush.linearGradient(
                        colors = listOf(Color(0xFFFFD700), Color(0xFFFF8C00), Color(0xFFFF4500)),
                        start = Offset(69f, 18f),
                        end = Offset(87f, 90f)
                )

                // Glow backing for lightning
                drawPath(
                        path = lightningPath,
                        color = Color(0xFFFF8C00).copy(alpha = opacity * lightningFade * (120f / 255f)),
                        style = Stroke(
                                width = 6f + kotlin.math.sin(elapsed * 15f) * 2f,
                                cap = StrokeCap.Round,
                                join = StrokeJoin.Round
                        )
                )
                // Fill gold gradient
                drawPath(
                        path = lightningPath,
                        brush = goldBrush,
                        alpha = opacity * lightningFade
                )
                // White outline stroke
                drawPath(
                        path = lightningPath,
                        color = Color.White.copy(alpha = opacity * lightningFade * (180f / 255f)),
                        style = Stroke(
                                width = 1.5f,
                                cap = StrokeCap.Round,
                                join = StrokeJoin.Round
                        )
                )
        }

        drawContext.canvas.restore()
}

/**
 * Draws ONLY the gradient lightning-bolt strike (glow backing + gold-gradient fill + white
 * outline) at the hero's 108-unit transform — the same bolt [drawStylizedRLogo] paints, isolated
 * so a caller can render its own hero art underneath and cross-fade to this identical bolt.
 *
 * The CosmicZoom hero (Task 3) draws the terminal-window brand mark instead of the old "R" but must
 * keep the strike pixel-identical. The few draw lines here are deliberately duplicated from
 * [drawStylizedRLogo] rather than shared back into it: that function must stay byte-for-byte
 * unchanged for its remaining `SplashRemexCommand` callers, and it is deleted in a later task — at
 * which point this becomes the single home for the bolt. The tx/ty/scale math mirrors
 * [drawStylizedRLogo] exactly so the bolt lands in the same place, including impact shudder.
 */
internal fun DrawScope.drawLightningStrike(
        w: Float,
        h: Float,
        scale: Float,
        opacity: Float,
        lightningFade: Float,
        elapsed: Float,
        shudderX: Float = 0f,
        shudderY: Float = 0f,
        yOffset: Float = -30f,
        customPos: Offset? = null
) {
        if (lightningFade <= 0f) return

        val tx = if (customPos != null) customPos.x + shudderX else w / 2f - 54f * scale + shudderX
        val ty = if (customPos != null) customPos.y + yOffset * scale + shudderY else h / 2f - 54f * scale + yOffset * scale + shudderY

        drawContext.canvas.save()
        drawContext.canvas.translate(tx, ty)
        drawContext.canvas.scale(scale, scale)

        val lightningPath = Path().apply {
                moveTo(31.5f + 24f, 9f)
                lineTo(31.5f + 24f, 58.5f)
                lineTo(45f + 24f, 58.5f)
                lineTo(45f + 24f, 99f)
                lineTo(76.5f + 24f, 45f)
                lineTo(58.5f + 24f, 45f)
                lineTo(76.5f + 24f, 9f)
                close()
        }

        val goldBrush = Brush.linearGradient(
                colors = listOf(Color(0xFFFFD700), Color(0xFFFF8C00), Color(0xFFFF4500)),
                start = Offset(69f, 18f),
                end = Offset(87f, 90f)
        )

        // Glow backing for lightning
        drawPath(
                path = lightningPath,
                color = Color(0xFFFF8C00).copy(alpha = opacity * lightningFade * (120f / 255f)),
                style = Stroke(
                        width = 6f + kotlin.math.sin(elapsed * 15f) * 2f,
                        cap = StrokeCap.Round,
                        join = StrokeJoin.Round
                )
        )
        // Fill gold gradient
        drawPath(
                path = lightningPath,
                brush = goldBrush,
                alpha = opacity * lightningFade
        )
        // White outline stroke
        drawPath(
                path = lightningPath,
                color = Color.White.copy(alpha = opacity * lightningFade * (180f / 255f)),
                style = Stroke(
                        width = 1.5f,
                        cap = StrokeCap.Round,
                        join = StrokeJoin.Round
                )
        )

        drawContext.canvas.restore()
}
