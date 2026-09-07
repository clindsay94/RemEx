package com.clindsay94.remex

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.PorterDuff
import android.graphics.PorterDuffXfermode
import android.graphics.Shader
import android.view.View
import androidx.compose.ui.graphics.toArgb
import androidx.core.content.ContextCompat
import com.clindsay94.remex.ui.theme.SplashMarkGeometry
import com.clindsay94.remex.ui.theme.SplashPalette
import kotlin.math.min

/**
 * The recoloured hand-off between the static system splash and app content (RemEx-alwfa.1): the
 * system splash is drawn from the manifest theme before the Activity exists, so it can never see
 * the stored seed. This view is the seed-coloured exit phase [MainActivity] crossfades through
 * instead — never a second full splash screen, just a brief repaint of the same brand mark.
 *
 * The mark is recoloured at runtime, not checked in as a new asset (RemEx-alwfa.1e): it reuses
 * the checked-in `ic_launcher_monochrome` drawable purely as an ALPHA MASK (its own baked colours
 * are irrelevant — see that drawable's header comment) and repaints the opaque pixels with a
 * primary -> tertiary gradient via `PorterDuff.Mode.SRC_IN`. The three brand dots are then redrawn
 * in the resolved accent colour on top, at [SplashMarkGeometry]'s derived centers.
 *
 * The mask bitmap is built ONCE, in [onSizeChanged], not per frame (RemEx-alwfa.1 review, HIGH-4):
 * `onDraw` runs on every frame of the crossfade, and a fresh full-size `ARGB_8888` bitmap plus a
 * vector rasterize each time was megabytes of avoidable allocation/GC on the startup path. It is
 * recycled in [onDetachedFromWindow].
 */
class SplashExitView(context: Context, private val palette: SplashPalette) : View(context) {

    private val backdropPaint = Paint().apply { color = palette.backdrop.toArgb() }
    private val accentPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = palette.accent.toArgb() }

    private var maskBitmap: Bitmap? = null
    private var maskLeft = 0f
    private var maskTop = 0f
    private var maskSize = 0

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        rebuildMask(w, h)
    }

    private fun rebuildMask(w: Int, h: Int) {
        maskBitmap?.recycle()
        maskBitmap = null
        if (w <= 0 || h <= 0) return

        val size = min(w, h)
        maskSize = size
        maskLeft = (w - size) / 2f
        maskTop = (h - size) / 2f

        val mask = ContextCompat.getDrawable(context, R.drawable.ic_launcher_monochrome) ?: return
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val maskCanvas = Canvas(bitmap)
        mask.setBounds(0, 0, size, size)
        mask.draw(maskCanvas)

        val gradientPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            shader = LinearGradient(
                0f, 0f, size.toFloat(), size.toFloat(),
                palette.markStart.toArgb(), palette.markEnd.toArgb(),
                Shader.TileMode.CLAMP
            )
            xfermode = PorterDuffXfermode(PorterDuff.Mode.SRC_IN)
        }
        maskCanvas.drawRect(0f, 0f, size.toFloat(), size.toFloat(), gradientPaint)
        maskBitmap = bitmap
    }

    override fun onDraw(canvas: Canvas) {
        canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), backdropPaint)
        val bitmap = maskBitmap ?: return
        canvas.drawBitmap(bitmap, maskLeft, maskTop, null)

        val scale = maskSize / SplashMarkGeometry.ViewportSize
        for (dotX in SplashMarkGeometry.accentDotCenterXs) {
            canvas.drawCircle(
                maskLeft + dotX * scale,
                maskTop + SplashMarkGeometry.accentDotCenterY * scale,
                SplashMarkGeometry.accentDotRadius * scale,
                accentPaint
            )
        }
    }

    override fun onDetachedFromWindow() {
        super.onDetachedFromWindow()
        maskBitmap?.recycle()
        maskBitmap = null
    }
}
