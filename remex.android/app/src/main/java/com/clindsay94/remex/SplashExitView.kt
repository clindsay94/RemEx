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
 * primary -> tertiary gradient via `PorterDuff.Mode.SRC_IN`. The three brand dots along the top
 * of the hex casing are then redrawn in the resolved accent colour on top, at the coordinates
 * `ic_launcher_monochrome.xml` places them at (27.3/34.3/41.3, 36 in its 108-unit viewport),
 * corrected for that file's own `scale=0.8, pivot=(54,54)` group transform.
 */
class SplashExitView(context: Context, private val palette: SplashPalette) : View(context) {

    private val backdropPaint = Paint().apply { color = palette.backdrop.toArgb() }
    private val accentPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = palette.accent.toArgb() }

    override fun onDraw(canvas: Canvas) {
        canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), backdropPaint)
        if (width <= 0 || height <= 0) return

        val size = min(width, height)
        val left = (width - size) / 2
        val top = (height - size) / 2

        val mask = ContextCompat.getDrawable(context, R.drawable.ic_launcher_monochrome) ?: return
        val maskBitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val maskCanvas = Canvas(maskBitmap)
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
        canvas.drawBitmap(maskBitmap, left.toFloat(), top.toFloat(), null)
        maskBitmap.recycle()

        val scale = size / ViewportSize
        for (dotX in AccentDotXs) {
            canvas.drawCircle(
                left + dotX * scale,
                top + AccentDotY * scale,
                AccentDotRadius * scale,
                accentPaint
            )
        }
    }

    private companion object {
        /** `ic_launcher_monochrome.xml`'s `viewportWidth`/`viewportHeight`. */
        const val ViewportSize = 108f

        /**
         * The three brand-dot centers from `ic_launcher_monochrome.xml` (27.3/34.3/41.3, 36),
         * pre-multiplied through that file's own `<group scaleX="0.8" scaleY="0.8" pivotX="54"
         * pivotY="54">` transform (`t = 54 + (v - 54) * 0.8`) since this view draws directly onto
         * the mask bitmap's untransformed 108-unit space.
         */
        val AccentDotXs = floatArrayOf(32.64f, 38.24f, 43.84f)
        const val AccentDotY = 39.6f
        const val AccentDotRadius = 1.76f
    }
}
