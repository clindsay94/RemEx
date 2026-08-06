package com.clindsay94.remex.diagnostics

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.util.Log
import androidx.core.content.FileProvider
import com.clindsay94.remex.R
import java.io.File
import java.security.MessageDigest

/**
 * Hands the finished bundle to the OS share sheet as a `.txt` attachment (RemEx-0iww).
 *
 * ATTACHED, NOT PASTED. Putting the bundle in `EXTRA_TEXT` would need no file at all, and it is the
 * wrong shape: mail and chat apps treat that as the message body, where hundreds of log lines get
 * wrapped, elided behind a "show trimmed content" fold, or clipped outright by an Intent extra size
 * limit nobody warns you about. A file arrives whole or not at all, which is the property a bug
 * report needs.
 *
 * NO NEW FILEPROVIDER ROOT. `res/xml/file_paths.xml` already exposes the whole cache directory as
 * `internal_cache`, so staging under `cacheDir/diagnostics/` is covered by the existing declaration.
 * A second `<cache-path>` entry for the same tree would grant nothing extra and would quietly widen
 * the surface a reader has to check.
 *
 * THE FILE NAME IS DERIVED FROM THE CONTENT, and it is not a tidiness choice either. A fixed name
 * rewritten in place looks harmless and is not: a mail client that keeps the `content://` URI in a
 * draft rather than copying the bytes immediately will resolve that URI again LATER, and by then the
 * file can hold a different report. Concretely — share with the network toggle off into a draft,
 * come back, turn the toggle on, share again somewhere else, and the untouched draft now points at a
 * file carrying the addresses its recipient was never shown. That is the "what you preview is what
 * leaves" promise breaking through the one seam the preview cannot cover, so the name has to change
 * whenever the content does. (Found in review of RemEx-0iww.)
 *
 * A DIGEST RATHER THAN A CLOCK. [DiagnosticsBundle.build] is deterministic precisely so the
 * previewed text is byte-identical to the sent text, and a timestamp in the name would be the one
 * part of the artifact the preview could not have shown. Hashing the content keeps that: the same
 * report always stages to the same file, so re-sharing an unchanged report reuses it instead of
 * accumulating copies, while a changed report can never land on top of one somebody is still
 * holding.
 */
object DiagnosticsShare {

    private const val TAG = "DiagnosticsShare"

    /** FileProvider authority suffix; combined with the package name at runtime. */
    private const val AuthoritySuffix = ".fileprovider"

    /** Subdirectory of `cacheDir`, already covered by the `internal_cache` FileProvider root. */
    internal const val StagingDirectory = "diagnostics"

    /**
     * Builds the content-addressed file name for [bundle].
     *
     * Eight hex characters of SHA-256. Long enough that two reports a user actually produces will
     * not collide, short enough to read in a mail client's attachment row — and a collision would
     * mean two identical-looking names for reports whose bytes differ, not a leak, because the file
     * that already exists is rewritten with the content being shared right now either way.
     */
    internal fun fileNameFor(bundle: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bundle.toByteArray(Charsets.UTF_8))
        val suffix = digest.take(4).joinToString("") { "%02x".format(it) }
        return "remex-diagnostics-$suffix.txt"
    }

    /**
     * The mail subject, in English on purpose.
     *
     * The same call the bundle's own section headings make (see [DiagnosticsBundle]): this text is
     * read by whoever is debugging the fault, not by the person sending it. The chooser title beside
     * it IS aimed at the sender and is localized normally.
     */
    private const val Subject = "RemEx diagnostics"

    private const val MimeType = "text/plain"

    /**
     * Stages [bundle] and returns an ACTION_SEND chooser for it, or null if the file could not be
     * written.
     *
     * [bundle] must already be the scrubbed output of [DiagnosticsBundle.build]. Nothing here
     * inspects or alters it — this is the delivery step, and re-deriving the text at the point of
     * sending is precisely how the preview and the attachment would come to disagree.
     *
     * WRITES TO DISK, so call it off the main thread. Up to 128KB, on a phone that is by definition
     * already misbehaving — which is the worst moment to block the frame that is drawing the button
     * the user just pressed.
     */
    fun buildSendIntent(context: Context, bundle: String): Intent? {
        val uri = stage(context, bundle) ?: return null
        val send =
                Intent(Intent.ACTION_SEND).apply {
                    type = MimeType
                    putExtra(Intent.EXTRA_STREAM, uri)
                    putExtra(Intent.EXTRA_SUBJECT, Subject)
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
        return Intent.createChooser(send, context.getString(R.string.share_diagnostics_chooser))
                .apply { addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) }
    }

    /** Writes the bundle into the cache and exposes it through the existing FileProvider. */
    private fun stage(context: Context, bundle: String): Uri? =
            try {
                val directory = File(context.cacheDir, StagingDirectory)
                directory.mkdirs()
                val file = File(directory, fileNameFor(bundle))
                file.writeText(bundle)
                FileProvider.getUriForFile(context, context.packageName + AuthoritySuffix, file)
            } catch (e: Exception) {
                // Reported to the user as a failed share rather than swallowed: they asked for a
                // file and would otherwise watch nothing happen.
                Log.w(TAG, "Could not stage the diagnostics bundle for sharing", e)
                null
            }
}
