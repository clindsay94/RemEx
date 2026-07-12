package com.clindsay94.remex.share

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.util.Log
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import com.clindsay94.remex.R
import java.io.File

/**
 * Builds and launches "open this file" intents for open-after-download (plan WP8).
 *
 * A downloaded file's local location is either a SAF `content://` document (the destination the user
 * picked via CreateDocument in the file manager) or a `file://` path inside app storage. `content://`
 * URIs are viewable directly with a temporary read grant; `file://` paths are wrapped through
 * [FileProvider] (authority `${applicationId}.fileprovider`, see `res/xml/file_paths.xml`) because
 * raw file URIs are rejected by other apps on modern Android.
 *
 * MIME is inferred from the file name via [ShareMimeInference], falling back to the platform
 * [MimeTypeMap] and finally the ContentResolver, so the chooser offers the right viewers.
 */
object FileOpener {

    private const val TAG = "FileOpener"

    /** FileProvider authority suffix; combined with the package name at runtime. */
    private const val AUTHORITY_SUFFIX = ".fileprovider"

    /**
     * Builds an ACTION_VIEW intent (wrapped in a chooser) for the file at [localUri], or null when a
     * viewable URI could not be produced. [fileName] drives MIME inference.
     */
    fun buildViewIntent(context: Context, localUri: String, fileName: String): Intent? {
        val viewUri = resolveViewUri(context, localUri) ?: return null
        val mime = resolveMimeType(context, fileName, viewUri)
        val view =
            Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(viewUri, mime)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
        return Intent.createChooser(view, context.getString(R.string.file_transfer_notification_open))
            .apply {
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
    }

    /** Attempts to open [localUri] immediately. Returns true when a viewer was launched. */
    fun open(context: Context, localUri: String, fileName: String): Boolean {
        val chooser = buildViewIntent(context, localUri, fileName) ?: return false
        return try {
            context.startActivity(chooser)
            true
        } catch (e: Exception) {
            Log.w(TAG, "No app could open $fileName", e)
            false
        }
    }

    private fun resolveViewUri(context: Context, localUri: String): Uri? {
        val src =
            try {
                Uri.parse(localUri)
            } catch (e: Exception) {
                null
            } ?: return null
        return when (src.scheme?.lowercase()) {
            "content" -> src
            "file" -> fileProviderUri(context, File(src.path ?: return null))
            null -> fileProviderUri(context, File(localUri)) // bare path
            else -> src
        }
    }

    private fun fileProviderUri(context: Context, file: File): Uri? =
        try {
            FileProvider.getUriForFile(context, context.packageName + AUTHORITY_SUFFIX, file)
        } catch (e: Exception) {
            Log.w(TAG, "FileProvider could not expose ${file.path}", e)
            null
        }

    private fun resolveMimeType(context: Context, fileName: String, uri: Uri): String {
        val inferred = ShareMimeInference.inferMimeType(fileName)
        if (inferred != ShareMimeInference.FALLBACK) return inferred
        val ext = ShareMimeInference.extensionOf(fileName)
        if (ext.isNotEmpty()) {
            MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext)?.let { return it }
        }
        return try {
            context.contentResolver.getType(uri)
        } catch (e: Exception) {
            null
        } ?: ShareMimeInference.FALLBACK
    }
}
