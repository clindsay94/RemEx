package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.AnimationVector1D
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.res.stringResource
import androidx.graphics.shapes.CornerRounding
import androidx.graphics.shapes.Morph
import androidx.graphics.shapes.RoundedPolygon
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.R
import kotlin.math.PI
import kotlin.math.sin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * "Signal Pong" splash variant.
 *
 * A playful mini-Pong built from the app icon's own elements: an amber signal comet rallies
 * between a chevron paddle (left) and a cursor-bar paddle (right). Each of the 3 contacts ejects
 * a feature glyph that parks and stays. After the third contact the paddles shape-morph (via the
 * androidx.graphics.shapes Morph/RoundedPolygon API — the same idiom SplashBackgroundFx uses) into
 * the icon's precise chevron/cursor, the signal cross-fades into the white "R", and the whole thing
 * resolves into the full terminal-window brand mark, then the "RemEx" wordmark rises and it zooms
 * out with a fade. Fixed brand colors, Victor Mono — never theme-adaptive. See
 * docs/superpowers/specs/2026-07-06-splash-screen-revamp-design.md → "Pong".
 */
@Composable
fun SplashPong(onFinished: () -> Unit, skipRequested: Boolean, onSkipConsumed: () -> Unit) {
    val scope = rememberCoroutineScope()
    val view = LocalView.current
    val density = LocalDensity.current
    var elapsed by remember { mutableStateOf(0f) }
    var completed by remember { mutableStateOf(false) }
    var particleFrame by remember { mutableStateOf(0) }
    var isSkipping by remember { mutableStateOf(false) }
    val skipAlpha = remember { Animatable(1f) }

    // Phase progress
    val intro = remember { Animatable(0f) }
    val finaleP = remember { Animatable(0f) }
    val completeP = remember { Animatable(0f) }
    val wordmarkP = remember { Animatable(0f) }
    val zoomProgress = remember { Animatable(0f) }
    val zoomScale = remember { Animatable(1f) }
    val fadeOverlay = remember { Animatable(0f) }

    // Per-contact spring recoils + glyph ejections
    val leftRecoil = remember { Animatable(0f) }
    val rightRecoil = remember { Animatable(0f) }
    val glyph1 = remember { Animatable(0f) }
    val glyph2 = remember { Animatable(0f) }
    val glyph3 = remember { Animatable(0f) }

    // Morph shapes (normalized to height 2, y in [-1,1]); built once.
    val paddlePoly = remember {
        RoundedPolygon(floatArrayOf(-0.2f, -1f, 0.2f, -1f, 0.2f, 1f, -0.2f, 1f), rounding = CornerRounding(0.2f))
    }
    val chevronPoly = remember {
        // right-pointing chevron ">" bent bar
        RoundedPolygon(
            floatArrayOf(-0.5f, -1f, 0.1f, -1f, 0.9f, 0f, 0.1f, 1f, -0.5f, 1f, 0.15f, 0f),
            rounding = CornerRounding(0.12f),
        )
    }
    val cursorPoly = remember {
        RoundedPolygon(floatArrayOf(-0.16f, -1f, 0.16f, -1f, 0.16f, 1f, -0.16f, 1f), rounding = CornerRounding(0.1f))
    }
    val morphLeft = remember { Morph(paddlePoly, chevronPoly) }
    val morphRight = remember { Morph(paddlePoly, cursorPoly) }

    // Text
    val textMeasurer = rememberTextMeasurer(cacheSize = 8)
    val wordmarkMeasured = remember {
        textMeasurer.measure(SplashBrand.remExAnnotated(), TextStyle(fontFamily = SplashBrand.VictorMonoBold, fontSize = 46.sp))
    }
    val tagFontSize = with(density) { 13.dp.toSp() }
    val tagTracking = with(density) { 2.dp.toSp() }
    val tagStyle = remember(tagFontSize, tagTracking) {
        TextStyle(color = SplashBrand.OffWhite, fontFamily = SplashBrand.VictorMonoBold, fontSize = tagFontSize, letterSpacing = tagTracking)
    }
    val cmdStr = stringResource(R.string.splash_command_your)
    val pcStr = stringResource(R.string.splash_pc)
    val skipStr = stringResource(R.string.splash_tap_to_skip)
    val versionStr = "v${BuildConfig.VERSION_NAME}"
    val commandMeasured = remember(tagStyle, cmdStr) { textMeasurer.measure(cmdStr, tagStyle) }
    val yourPcMeasured = remember(tagStyle, pcStr) { textMeasurer.measure(pcStr, tagStyle) }
    val skipMeasured = remember(tagStyle, skipStr) { textMeasurer.measure(skipStr, tagStyle) }
    val versionMeasured = remember(tagStyle, versionStr) { textMeasurer.measure(versionStr, tagStyle) }

    fun finishOnce() {
        if (completed) return
        completed = true
        onFinished()
    }

    // Skip: fade then finish — identical contract to the sibling variants.
    LaunchedEffect(skipRequested) {
        if (!skipRequested || isSkipping || completed) return@LaunchedEffect
        isSkipping = true
        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
        skipAlpha.animateTo(0f, tween(200, easing = FastOutSlowInEasing))
        finishOnce()
        onSkipConsumed()
    }

    LaunchedEffect(Unit) {
        // Frame clock (real dt, main thread) — advances elapsed and subscribes the Canvas.
        launch {
            var lastNanos = 0L
            while (!isSkipping && !completed) {
                withFrameNanos { now ->
                    val dt = if (lastNanos == 0L) 0.016f else ((now - lastNanos) / 1_000_000_000f).coerceAtMost(0.05f)
                    lastNanos = now
                    elapsed += dt
                    particleFrame++
                }
            }
        }

        fun contact(recoil: Animatable<Float, AnimationVector1D>, glyph: Animatable<Float, AnimationVector1D>) {
            if (isSkipping) return
            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
            scope.launch { recoil.snapTo(1f); recoil.animateTo(0f, spring(dampingRatio = 0.35f, stiffness = Spring.StiffnessMedium)) }
            scope.launch { glyph.animateTo(1f, tween(420, easing = FastOutSlowInEasing)) }
        }

        intro.animateTo(1f, tween(300, easing = FastOutSlowInEasing))
        delay(450); contact(rightRecoil, glyph1) // contact 1 — right paddle
        delay(450); contact(leftRecoil, glyph2)  // contact 2 — left paddle
        delay(450); contact(rightRecoil, glyph3) // contact 3 — right paddle
        delay(60)
        if (!isSkipping) finaleP.animateTo(1f, tween(560, easing = FastOutSlowInEasing))
        if (!isSkipping) completeP.animateTo(1f, tween(200, easing = FastOutSlowInEasing))
        if (!isSkipping) wordmarkP.animateTo(1f, tween(350, easing = FastOutSlowInEasing))
        if (!isSkipping) delay(200)
        if (!isSkipping) {
            scope.launch { zoomScale.animateTo(3.4f, tween(750, easing = FastOutSlowInEasing)) }
            scope.launch { zoomProgress.animateTo(1f, tween(750, easing = FastOutSlowInEasing)) }
            delay(400)
            if (!isSkipping) {
                fadeOverlay.animateTo(1f, tween(400, easing = LinearEasing))
                finishOnce()
            }
        }
    }

    Box(
        modifier = Modifier.fillMaxSize().background(SplashBrand.WindowFill).graphicsLayer { alpha = skipAlpha.value }
    ) {
        Canvas(
            modifier = Modifier.fillMaxSize().graphicsLayer {
                val s = zoomScale.value
                scaleX = s
                scaleY = s
                val markCx = size.width * 0.5f
                val markCy = size.height * 0.40f
                translationX = (size.width / 2f - markCx) * (s - 1f) * zoomProgress.value
                translationY = (size.height / 2f - markCy) * (s - 1f) * zoomProgress.value
            }
        ) {
            @Suppress("UNUSED_EXPRESSION") particleFrame
            val w = size.width
            val h = size.height
            drawRect(brush = SplashBrand.backdropBrush(size))

            val leftX = w * 0.30f
            val rightX = w * 0.70f
            val midY = h * 0.42f
            val cxMid = (leftX + rightX) / 2f
            val introA = intro.value
            val fin = finaleP.value
            val comp = completeP.value

            // Assembled-mark layout + the icon-space -> screen transform (matches drawRemexIcon).
            val markCenter = Offset(w * 0.5f, h * 0.40f)
            val markSize = w * 0.42f
            val s108 = markSize / 108f
            fun iconPt(px: Float, py: Float) = Offset(
                markCenter.x - markSize / 2f + s108 * (54f + 0.8f * (px - 54f)),
                markCenter.y - markSize / 2f + s108 * (54f + 0.8f * (py - 54f)),
            )
            val chevronSpot = iconPt(34f, 61f)
            val cursorSpot = iconPt(76f, 61f)
            val rSpot = iconPt(58f, 60f)

            // Signal position as a pure function of time (so the trailing comet needs no history state).
            fun signalAt(t: Float): Offset = when {
                t < 0.30f -> Offset(cxMid, midY)
                t < 0.75f -> { val u = ease((t - 0.30f) / 0.45f); Offset(lerpF(cxMid, rightX, u), midY + sin(u * PI.toFloat()) * -h * 0.09f) }
                t < 1.20f -> { val u = ease((t - 0.75f) / 0.45f); Offset(lerpF(rightX, leftX, u), midY + sin(u * PI.toFloat()) * h * 0.11f) }
                t < 1.65f -> { val u = ease((t - 1.20f) / 0.45f); Offset(lerpF(leftX, rightX, u), midY + sin(u * PI.toFloat()) * -h * 0.07f) }
                else -> Offset(rightX, midY)
            }

            val sigY = signalAt(elapsed).y
            val paddleCourtScale = 0.06f * h
            val glyphIconScale = markSize * 0.088f

            // ── Paddles: morph rounded-bar -> icon chevron/cursor while moving to the icon layout ──
            val leftPos = lerpO(Offset(leftX, lerpF(midY, sigY, 0.25f)), chevronSpot, fin)
            val rightPos = lerpO(Offset(rightX, lerpF(midY, sigY, 0.25f)), cursorSpot, fin)
            val paddleScale = lerpF(paddleCourtScale, glyphIconScale, fin)
            val paddleAlpha = introA * (1f - comp)
            drawMorphGlyph(morphLeft, fin, leftPos, paddleScale, leftRecoil.value, paddleAlpha)
            drawMorphGlyph(morphRight, fin, rightPos, paddleScale, rightRecoil.value, paddleAlpha)

            // ── Signal comet (rally) / cross-fade to R (finale) ──
            if (fin <= 0f) {
                val steps = 7
                for (i in steps downTo 0) {
                    val p = signalAt(elapsed - i * 0.016f)
                    val f = 1f - i.toFloat() / steps
                    val a = f * 0.9f * introA
                    if (a <= 0.01f) continue
                    val rad = lerpF(2.2f, 6.5f, f) * (h / 2400f).coerceAtLeast(0.6f)
                    drawCircle(SplashBrand.Amber.copy(alpha = a * 0.35f), rad * 2.4f, p)
                    drawCircle(if (i == 0) SplashBrand.OffWhite else SplashBrand.Amber, rad, p)
                }
            } else {
                // signal streaks to the R spot and fades as the R resolves in
                val sp = lerpO(Offset(rightX, midY), rSpot, fin)
                val sigA = (1f - fin)
                if (sigA > 0.01f) {
                    drawCircle(SplashBrand.Amber.copy(alpha = sigA * 0.35f), 14f, sp)
                    drawCircle(SplashBrand.OffWhite.copy(alpha = sigA), 5f, sp)
                }
                if (comp < 1f) {
                    withTransform({
                        translate(markCenter.x - markSize / 2f, markCenter.y - markSize / 2f)
                        scale(s108, s108, pivot = Offset.Zero)
                        scale(0.8f, 0.8f, pivot = Offset(54f, 54f))
                    }) {
                        with(SplashBrand) { drawRGlyph(opacity = fin * (1f - comp), holeColor = SplashBrand.BackdropStart) }
                    }
                }
            }

            // ── Completion beat: the full brand mark materializes over the assembled elements ──
            if (comp > 0f) {
                with(SplashBrand) { drawRemexIcon(center = markCenter, sizePx = markSize, opacity = comp) }
            }

            // ── Feature glyphs — eject from the contact point, park in the top region, stay ──
            val contactRight = Offset(rightX, midY)
            val contactLeft = Offset(leftX, midY)
            drawParkedGlyph(FeatureGlyph.RemoteDesktop, glyph1.value, contactRight, Offset(w * 0.28f, h * 0.24f), glyphIconScale * 1.35f)
            drawParkedGlyph(FeatureGlyph.Telemetry, glyph2.value, contactLeft, Offset(w * 0.50f, h * 0.19f), glyphIconScale * 1.35f)
            drawParkedGlyph(FeatureGlyph.FileTransfer, glyph3.value, contactRight, Offset(w * 0.72f, h * 0.24f), glyphIconScale * 1.35f)

            // ── Wordmark lockup below the completed mark ──
            val wmA = wordmarkP.value
            if (wmA > 0.01f) {
                val rise = (1f - wmA) * 14.dp.toPx()
                val wy = markCenter.y + markSize * 0.62f
                drawText(wordmarkMeasured, topLeft = Offset(w / 2f - wordmarkMeasured.size.width / 2f, wy + rise), alpha = wmA)
                val tagFullW = commandMeasured.size.width + yourPcMeasured.size.width
                val ty = wy + wordmarkMeasured.size.height + 10.dp.toPx()
                val tx = w / 2f - tagFullW / 2f
                drawText(commandMeasured, color = SplashBrand.SlateLo, topLeft = Offset(tx, ty + rise), alpha = wmA)
                drawText(yourPcMeasured, color = SplashBrand.Amber, topLeft = Offset(tx + commandMeasured.size.width, ty + rise), alpha = wmA)
            }

            // ── Chrome: version (bottom-center) + tap-to-skip hint ──
            val versionT = ((elapsed - 0.2f) / 0.5f).coerceIn(0f, 1f)
            if (versionT > 0f) {
                drawText(versionMeasured, color = SplashBrand.OffWhite.copy(alpha = versionT * 0.4f),
                    topLeft = Offset((w - versionMeasured.size.width) / 2f, h - 32.dp.toPx() - versionMeasured.size.height))
            }
            val skipT = ((elapsed - 0.8f) / 0.5f).coerceIn(0f, 1f)
            if (skipT > 0f && !isSkipping) {
                drawText(skipMeasured, color = SplashBrand.OffWhite.copy(alpha = skipT * 0.5f),
                    topLeft = Offset((w - skipMeasured.size.width) / 2f, h - 56.dp.toPx() - skipMeasured.size.height))
            }

            // ── Exit fade ──
            if (fadeOverlay.value > 0f) {
                drawRect(color = SplashBrand.WindowFill.copy(alpha = fadeOverlay.value), size = size)
            }
        }
    }
}

/** Draw a Morph at [progress] centered at [pos], scaled by [scale], with a horizontal recoil pop. */
private fun DrawScope.drawMorphGlyph(morph: Morph, progress: Float, pos: Offset, scale: Float, recoil: Float, alpha: Float) {
    if (alpha <= 0.01f) return
    val path = Path()
    var first = true
    morph.forEachCubic(progress) { c ->
        if (first) {
            path.moveTo(c.anchor0X, c.anchor0Y)
            first = false
        }
        path.cubicTo(c.control0X, c.control0Y, c.control1X, c.control1Y, c.anchor1X, c.anchor1Y)
    }
    path.close()
    withTransform({
        translate(pos.x, pos.y)
        scale(scale * (1f + recoil * 0.5f), scale, pivot = Offset.Zero)
    }) {
        drawPath(path, SplashBrand.Amber, alpha = alpha)
    }
}

/** Eject a feature glyph from [from] to its parked [park] spot as [p] goes 0->1, then hold. */
private fun DrawScope.drawParkedGlyph(kind: FeatureGlyph, p: Float, from: Offset, park: Offset, sizePx: Float) {
    if (p <= 0.01f) return
    val e = ease(p)
    val pos = lerpO(from, park, e)
    drawFeatureGlyph(kind, pos, sizePx * (0.4f + 0.6f * e), alpha = e)
}

private fun lerpF(a: Float, b: Float, t: Float): Float = a + (b - a) * t
private fun lerpO(a: Offset, b: Offset, t: Float): Offset = Offset(lerpF(a.x, b.x, t), lerpF(a.y, b.y, t))
private fun ease(t: Float): Float {
    val c = t.coerceIn(0f, 1f)
    return c * c * (3f - 2f * c)
}
