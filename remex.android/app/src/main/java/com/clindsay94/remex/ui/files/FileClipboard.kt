package com.clindsay94.remex.ui.files

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context

/**
 * Puts plain text (a path, a SHA-256 fingerprint) on the clipboard for the File Transfer screen. Guarded because a
 * clip is a binder transaction that can fail; false means nothing was copied, so the caller says so instead of
 * claiming it was.
 */
object FileClipboard {
    fun copy(context: Context, label: String, text: String): Boolean {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: return false
        return runCatching { clipboard.setPrimaryClip(ClipData.newPlainText(label, text)) }.isSuccess
    }
}
