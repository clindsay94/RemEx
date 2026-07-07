package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.R

/**
 * Fixed brand identity for the splash screens.
 *
 * Rendered identically regardless of the active in-app theme — a deliberate, documented
 * exception to the repo's "verify across all four themes" rule: a splash is a brand moment
 * (like a logo screen), not themed UI chrome. Colors and geometry are ported 1:1 from the
 * shipped adaptive launcher icon (res/drawable/ic_launcher_background.xml +
 * ic_launcher_foreground_vector.xml, commit 685b493) — not an approximation.
 *
 * Shared by all three splash variants (CosmicZoom, RemexCommand, Pong).
 */
object SplashBrand {
    // ── Backdrop (ic_launcher_background.xml diagonal gradient) ──
    val BackdropStart = Color(0xFF20242F)
    val BackdropEnd = Color(0xFF39404F)

    // ── Terminal-window card (foreground body) ──
    val WindowFill = Color(0xFF0C0E13)
    val WindowStroke = Color(0xFF7C88A0)
    const val WindowStrokeAlpha = 0.55f

    // ── Accents ──
    val Amber = Color(0xFFFFB63D)
    val SlateLo = Color(0xFF6E7B94)
    val SlateHi = Color(0xFF333B48)
    val OffWhite = Color(0xFFF4F5F9)

    const val ViewportUnits = 108f
    private const val GroupScale = 0.8f // <group android:scaleX/Y="0.8">
    private const val GroupPivot = 54f // pivotX/Y="54"

    val VictorMonoBold = FontFamily(Font(R.font.victor_mono_bold))

    /** Full-bleed diagonal backdrop brush (top-left → bottom-right), matching the icon gradient. */
    fun backdropBrush(size: Size): Brush = Brush.linearGradient(
        colors = listOf(BackdropStart, BackdropEnd),
        start = Offset(0f, 0f),
        end = Offset(size.width, size.height),
    )

    /** Two-color single-line wordmark: "Rem" off-white + "Ex" amber. */
    fun remExAnnotated(opacity: Float = 1f): AnnotatedString = buildAnnotatedString {
        withStyle(SpanStyle(color = OffWhite.copy(alpha = opacity))) { append("Rem") }
        withStyle(SpanStyle(color = Amber.copy(alpha = opacity))) { append("Ex") }
    }

    // ── Icon artwork parsed once; pathData copied verbatim from ic_launcher_foreground_vector.xml ──
    private fun p(d: String): Path = PathParser().parsePathString(d).toPath()

    private val WindowPath =
        p("M33,26 L75,26 A13,13 0 0 1 88,39 L88,69 A13,13 0 0 1 75,82 L33,82 A13,13 0 0 1 20,69 L20,39 A13,13 0 0 1 33,26 Z")
    private val Dot1Path = p("M27.3,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z")
    private val Dot2Path = p("M34.3,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z")
    private val Dot3Path = p("M41.3,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z")

    /** Amber `>` chevron (108-unit space). Exposed for Pong's left paddle + finale morph target. */
    val ChevronPath = p("M30,50 L39,61 L30,72")

    private val RStemPath = p("M45,47 h7 v25 h-7 Z")
    private val RBowlPath = p("M52,47 H60 A8,8 0 0 1 60,63 H52 Z")
    private val RLegPath = p("M53,58.5 L61,58.5 L71.5,73 L63,73 Z")
    private val RHolePath = p("M53.5,53 H58.8 A2.8,2.8 0 0 1 58.8,58.6 H53.5 Z")

    /** Amber cursor block (108-unit space). Exposed for Pong's right paddle + finale morph target. */
    val CursorPath = p("M73,50 h6 v22 h-6 Z")

    /**
     * Draw the terminal-window brand mark centered at [center], sized so the 108-unit artwork
     * spans [sizePx]. [opacity] fades the whole mark.
     */
    fun DrawScope.drawRemexIcon(center: Offset, sizePx: Float, opacity: Float = 1f) {
        val s = sizePx / ViewportUnits
        withTransform({
            translate(center.x - sizePx / 2f, center.y - sizePx / 2f)
            scale(s, s, pivot = Offset(0f, 0f))
            scale(GroupScale, GroupScale, pivot = Offset(GroupPivot, GroupPivot))
        }) {
            drawPath(WindowPath, WindowFill, alpha = opacity)
            drawPath(WindowPath, WindowStroke, alpha = WindowStrokeAlpha * opacity, style = Stroke(width = 1.4f))
            drawPath(Dot1Path, Amber, alpha = opacity)
            drawPath(Dot2Path, SlateLo, alpha = opacity)
            drawPath(Dot3Path, SlateHi, alpha = opacity)
            drawPath(
                ChevronPath,
                Amber,
                alpha = opacity,
                style = Stroke(width = 4.8f, cap = StrokeCap.Round, join = StrokeJoin.Round),
            )
            drawRGlyph(opacity)
            drawPath(CursorPath, Amber, alpha = opacity)
        }
    }

    /**
     * White "R" glyph with its punched counter-hole. The caller must already have applied the
     * 108-unit transform (as [drawRemexIcon] does). When drawing the R standalone over the
     * backdrop (e.g. Pong's finale), pass [holeColor] = the surface behind it.
     */
    fun DrawScope.drawRGlyph(opacity: Float = 1f, holeColor: Color = WindowFill) {
        drawPath(RStemPath, OffWhite, alpha = opacity)
        drawPath(RBowlPath, OffWhite, alpha = opacity)
        drawPath(RLegPath, OffWhite, alpha = opacity)
        drawPath(RHolePath, holeColor, alpha = opacity)
    }
}

@Preview(showBackground = true, backgroundColor = 0xFF20242F)
@Composable
private fun SplashBrandPreview() {
    Canvas(
        Modifier
            .fillMaxSize()
            .background(Brush.linearGradient(listOf(SplashBrand.BackdropStart, SplashBrand.BackdropEnd)))
    ) {
        with(SplashBrand) {
            drawRemexIcon(center = center.copy(y = center.y - 120f), sizePx = 320f)
        }
    }
}
