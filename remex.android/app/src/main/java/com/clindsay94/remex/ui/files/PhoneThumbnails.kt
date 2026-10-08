package com.clindsay94.remex.ui.files

import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asAndroidBitmap

/**
 * Decoded thumbnails of phone images for the File Transfer list (file browser redesign, 2026-10-08), keyed by
 * folder URI and path. Outlives a row scrolled off screen, so scrolling back doesn't ask the system again.
 * Bounded by decoded bytes, like the PC thumbnails' cache.
 */
object PhoneThumbnails {
    /** The size asked of `ContentResolver.loadThumbnail`; the system's own thumbnails are about this big. */
    const val SIZE_PX = 256
    private const val MAX_BYTES = 8 * 1024 * 1024

    private val cache = object : LruCache<String, ImageBitmap>(MAX_BYTES) {
        override fun sizeOf(key: String, value: ImageBitmap): Int = value.asAndroidBitmap().allocationByteCount
    }

    fun get(key: String): ImageBitmap? = cache.get(key)

    fun put(key: String, bitmap: ImageBitmap) {
        cache.put(key, bitmap)
    }
}
