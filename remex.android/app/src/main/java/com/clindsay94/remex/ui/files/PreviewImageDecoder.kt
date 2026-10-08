package com.clindsay94.remex.ui.files

import android.graphics.ImageDecoder
import android.util.DisplayMetrics
import android.util.Log
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import java.nio.ByteBuffer

/**
 * Decodes a previewed image with [ImageDecoder] (JPEG, PNG, GIF, WebP, BMP and HEIC/HEIF), downsampled so its
 * longer side is at most twice the screen's: sharp when pinch-zoomed, without holding a 48-megapixel bitmap.
 */
object PreviewImageDecoder {
    private const val TAG = "PreviewImageDecoder"

    fun decode(bytes: ByteArray, display: DisplayMetrics): ImageBitmap? {
        val limit = 2 * maxOf(display.widthPixels, display.heightPixels, 1080)
        return try {
            ImageDecoder.decodeBitmap(ImageDecoder.createSource(ByteBuffer.wrap(bytes))) { decoder, info, _ ->
                val longest = maxOf(info.size.width, info.size.height)
                if (longest > limit) {
                    val scale = limit.toFloat() / longest
                    decoder.setTargetSize(
                        (info.size.width * scale).toInt().coerceAtLeast(1),
                        (info.size.height * scale).toInt().coerceAtLeast(1),
                    )
                }
            }.asImageBitmap()
        } catch (e: Exception) {
            // Not an image after all, or a format this phone can't read: the pane shows the details instead.
            Log.i(TAG, "Image preview could not be decoded", e)
            null
        }
    }
}
