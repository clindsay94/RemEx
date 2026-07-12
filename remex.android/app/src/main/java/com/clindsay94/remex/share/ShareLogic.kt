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
