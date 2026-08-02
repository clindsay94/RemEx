package com.clindsay94.remex.ui.screens

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Matrix
import androidx.compose.ui.graphics.Path
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

/**
 * Subscribes the enclosing draw block to [frame], so it re-runs whenever the frame counter ticks.
 *
 * A Compose `Canvas` re-runs its draw lambda only when a snapshot state it READ is invalidated.
 * The splash particle loops mutate plain remembered lists, which are not observable, so nothing
 * would ever invalidate the draw - the animation is driven by reading a counter that the loop
 * increments. Reading it is the entire point; the value is deliberately discarded.
 *
 * This exists because that bare expression is unobvious enough that each of its four call sites
 * needed a `@Suppress("UNUSED_EXPRESSION")`, and a reader who does not recognise the idiom may
 * tidy away the apparently dead line - which freezes the animation and breaks nothing the build
 * or lint can see (RemEx-si3q). Naming it puts the explanation in one place and makes the call
 * sites say what they mean.
 *
 * The suppression is GONE rather than relocated, which was better than the bead expected: Kotlin
 * does not warn about an unused parameter on a non-private function, so nothing here needs
 * silencing. Four `@Suppress` annotations removed, none added - checked by compiling without one.
 *
 * Call it INSIDE the draw block. The read is what subscribes, so moving it outside - or hoisting
 * the argument into a local computed before the block - silently undoes it.
 */
internal fun DrawScope.redrawOnFrame(frame: Int) {
        // Intentionally empty: evaluating the argument at the call site is the subscription.
}

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

// (drawLightningStrike removed — CosmicZoom no longer cross-fades the hero into a lightning bolt.)
