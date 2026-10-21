package com.clindsay94.remex.share

/**
 * Pure, Android-free logic for the "Send to PC" share target + open-after-download (plan WP8).
 *
 * Extracted from [ShareToPcActivity] / [FileOpener] so the two decisions that actually need testing —
 * inferring a viewer MIME type from a file name, and resolving where a shared file lands on the peer —
 * can be exercised on the JVM without an Android runtime, a live socket, or a real ContentResolver
 * (mirrors how `FileManagerLogic` / `TransferResumeLogic` isolate their arithmetic).
 *
 * Nothing here touches the wire. The push offer itself is emitted by the already-landed
 * `FileTransferEngine.enqueueUpload(push = true)` (mode `"push"`), whose field names mirror
 * `remex.core` `FileTransferOffer` verbatim; this file only prepares the destination path + display
 * MIME that feed into it.
 */

/**
 * Which shared items the share target accepts (RemEx-172ba). Generic over the item so the rule runs on
 * the JVM without `android.net.Uri`; the activity passes Uris and `Uri::getScheme`.
 *
 * CONTENT URIS ONLY. A `content://` item comes with a read grant from the sending app, so no storage
 * permission is ever needed. A `file://` item would need one (and is refused by Android 7+ anyway), and
 * any other scheme names nothing this screen can read.
 */
object ShareIntentParsing {
    const val ACTION_SEND = "android.intent.action.SEND"
    const val ACTION_SEND_MULTIPLE = "android.intent.action.SEND_MULTIPLE"

    fun <T : Any> sharedItems(
        action: String?,
        single: () -> T?,
        multiple: () -> List<T?>?,
        schemeOf: (T) -> String?,
    ): List<T> {
        val raw =
            when (action) {
                ACTION_SEND -> listOfNotNull(single())
                ACTION_SEND_MULTIPLE -> multiple().orEmpty().filterNotNull()
                else -> emptyList()
            }
        return raw.filter { schemeOf(it).equals("content", ignoreCase = true) }.distinct()
    }
}

/**
 * The share screen's coarse state, decided in one place (RemEx-172ba). SENDING and SENT are owned by the
 * send itself, so this never returns them.
 */
object SharePhaseRules {
    fun phase(
        filesResolved: Boolean,
        fileCount: Int,
        pairedPcsLoaded: Boolean,
        hasPairedPc: Boolean,
        targetChosen: Boolean,
        rootsLoaded: Boolean,
        connectedOrConnecting: Boolean,
    ): SharePhase =
        when {
            filesResolved && fileCount == 0 -> SharePhase.EMPTY
            // A cold launch reads the pairings before it knows anything; that is a moment, not a state.
            !pairedPcsLoaded -> SharePhase.CONNECTING
            !hasPairedPc -> SharePhase.NEEDS_SETUP
            !targetChosen -> SharePhase.CHOOSE_PC
            rootsLoaded -> SharePhase.READY
            connectedOrConnecting -> SharePhase.CONNECTING
            else -> SharePhase.CHOOSE_PC
        }
}

/** A folder something was sent to from the share screen, remembered on this phone only. */
data class ShareRecentFolder(val host: String, val rootId: String, val rootName: String, val path: String)

/** The share screen's recent folders (RemEx-172ba): newest first, a few per PC, encoded as JSON. */
object ShareRecentFolders {
    const val MAX_PER_PC = 3
    private const val MAX_TOTAL = 12

    fun record(existing: List<ShareRecentFolder>, folder: ShareRecentFolder): List<ShareRecentFolder> {
        val newest = folder.copy(path = normalizePath(folder.path))
        val result = mutableListOf(newest)
        var samePc = 1
        for (entry in existing) {
            if (result.size >= MAX_TOTAL) break
            if (entry.sameFolderAs(newest)) continue
            if (entry.host.equals(newest.host, ignoreCase = true)) {
                if (samePc >= MAX_PER_PC) continue
                samePc++
            }
            result += entry
        }
        return result
    }

    /** The recent folders for [host] that still name a root the PC shares, newest first. */
    fun forPc(all: List<ShareRecentFolder>, host: String, sharedRootIds: Set<String>): List<ShareRecentFolder> =
        all.filter { it.host.equals(host, ignoreCase = true) && it.rootId in sharedRootIds }.take(MAX_PER_PC)

    fun encode(entries: List<ShareRecentFolder>): String =
        org.json.JSONArray().apply {
            entries.forEach {
                put(
                    org.json.JSONObject()
                        .put("host", it.host)
                        .put("rootId", it.rootId)
                        .put("rootName", it.rootName)
                        .put("path", it.path)
                )
            }
        }.toString()

    fun parse(encoded: String?): List<ShareRecentFolder> {
        if (encoded.isNullOrBlank()) return emptyList()
        val array = runCatching { org.json.JSONArray(encoded) }.getOrNull() ?: return emptyList()
        return buildList {
            for (i in 0 until array.length()) {
                val o = array.optJSONObject(i) ?: continue
                val host = o.optString("host")
                val rootId = o.optString("rootId")
                if (host.isBlank() || rootId.isBlank()) continue
                add(ShareRecentFolder(host, rootId, o.optString("rootName"), normalizePath(o.optString("path"))))
            }
        }
    }

    private fun normalizePath(path: String): String {
        val trimmed = path.replace('\\', '/').trim().trim('/')
        return if (trimmed.isEmpty()) "/" else "/$trimmed"
    }

    private fun ShareRecentFolder.sameFolderAs(other: ShareRecentFolder): Boolean =
        host.equals(other.host, ignoreCase = true) && rootId == other.rootId && path == other.path
}

/** Infers a best-effort MIME type from a file name's extension, for open-after-download (ACTION_VIEW). */
object ShareMimeInference {

    /** Returned when the extension is unknown; a universally safe "unknown binary" type. */
    const val FALLBACK = "application/octet-stream"

    // Curated, offline extension → MIME map. Intentionally covers the common share/download types so
    // the pure inference works in unit tests; [FileOpener] additionally consults the platform
    // MimeTypeMap at runtime for anything not listed here.
    private val MIME_BY_EXT: Map<String, String> =
        mapOf(
            // documents
            "pdf" to "application/pdf",
            "txt" to "text/plain",
            "md" to "text/markdown",
            "rtf" to "application/rtf",
            "csv" to "text/csv",
            "json" to "application/json",
            "xml" to "text/xml",
            "html" to "text/html",
            "htm" to "text/html",
            "doc" to "application/msword",
            "docx" to
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" to "application/vnd.ms-excel",
            "xlsx" to
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "ppt" to "application/vnd.ms-powerpoint",
            "pptx" to
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "odt" to "application/vnd.oasis.opendocument.text",
            "ods" to "application/vnd.oasis.opendocument.spreadsheet",
            "epub" to "application/epub+zip",
            // images
            "jpg" to "image/jpeg",
            "jpeg" to "image/jpeg",
            "png" to "image/png",
            "gif" to "image/gif",
            "webp" to "image/webp",
            "bmp" to "image/bmp",
            "svg" to "image/svg+xml",
            "heic" to "image/heic",
            "heif" to "image/heif",
            // audio
            "mp3" to "audio/mpeg",
            "wav" to "audio/wav",
            "flac" to "audio/flac",
            "ogg" to "audio/ogg",
            "opus" to "audio/ogg",
            "m4a" to "audio/mp4",
            "aac" to "audio/aac",
            // video
            "mp4" to "video/mp4",
            "m4v" to "video/mp4",
            "mkv" to "video/x-matroska",
            "webm" to "video/webm",
            "mov" to "video/quicktime",
            "avi" to "video/x-msvideo",
            "3gp" to "video/3gpp",
            // archives / packages
            "zip" to "application/zip",
            "7z" to "application/x-7z-compressed",
            "rar" to "application/vnd.rar",
            "tar" to "application/x-tar",
            "gz" to "application/gzip",
            "apk" to "application/vnd.android.package-archive",
        )

    /** Lowercased extension (no dot), or "" when the name has no extension. */
    fun extensionOf(name: String): String {
        val dot = name.lastIndexOf('.')
        if (dot < 0 || dot == name.length - 1) return ""
        return name.substring(dot + 1).lowercase()
    }

    /** Best-effort MIME type from [fileName]'s extension; [FALLBACK] when unknown. */
    fun inferMimeType(fileName: String): String = MIME_BY_EXT[extensionOf(fileName)] ?: FALLBACK
}

/**
 * Resolves the on-peer destination for a shared file: a sanitised file name (no path traversal, no
 * illegal characters) combined with the folder the user picked, producing the `destRelativePath` that
 * the push offer carries.
 */
object ShareDestinationResolver {

    /** Fallback stem when a shared item has no usable display name. */
    const val DEFAULT_FALLBACK = "shared-file"

    // Characters disallowed in a file name on Windows and/or Linux. '/' and '\\' are handled by the
    // path-component strip below, but are included so a stray separator inside a segment is also gone.
    private val ILLEGAL = "<>:\"/\\|?*".toSet()

    /**
     * Sanitises a raw display name into a safe single path segment: drops any directory components
     * (defeats `../` traversal), strips control + illegal characters, and falls back to
     * [fallbackStem] when nothing usable remains (empty, or all dots such as "." / "..").
     */
    fun sanitizeFileName(raw: String?, fallbackStem: String = DEFAULT_FALLBACK): String {
        val lastSegment =
            (raw ?: "").replace('\\', '/').substringAfterLast('/')
        val cleaned =
            buildString {
                for (c in lastSegment) {
                    if (c.code < 0x20 || c.code == 0x7F) continue // control chars
                    if (c in ILLEGAL) continue
                    append(c)
                }
            }.trim()
        if (cleaned.isEmpty() || cleaned.all { it == '.' }) return fallbackStem
        return cleaned
    }

    /**
     * Joins a picked destination [folder] and a shared item's [fileName] into a normalised
     * forward-slash relative path, sanitising the name first. Matches `FileManagerLogic.combinePath`
     * semantics (root "/" or "" yields just the file name).
     */
    fun resolveDestRelativePath(
        folder: String,
        fileName: String?,
        fallbackStem: String = DEFAULT_FALLBACK,
    ): String {
        val safe = sanitizeFileName(fileName, fallbackStem)
        val normalized = folder.replace('\\', '/').trimEnd('/')
        return if (normalized.isEmpty() || normalized == "/") safe else "$normalized/$safe"
    }
}
