package com.clindsay94.remex.ui.files.preview

/** What the preview pane shows for a file (file browser redesign, 2026-10-08). Twin of the PC's `PreviewKind`. */
enum class PreviewKind {
    /** A folder, or a file with nothing to preview: a large icon and the details. */
    None,

    /** An image decoded at full size, with pinch-zoom and pan. */
    Image,

    /** An image this device can't decode: its thumbnail only. Never chosen on the phone, which decodes HEIC itself. */
    ImageThumbnailOnly,

    /** Text, code or a log, in the monospace viewer (and the only kind that can live-tail). */
    Text,

    /** Extension unknown: the first bytes are sniffed, and it is shown as text unless they look binary. */
    Sniff,
}

/**
 * Chooses a [PreviewKind] from a file name. Pure, and the same rules as the PC's `PreviewClassifier`; the
 * shared vectors in `file-preview-vectors.json` hold the two to each other.
 */
object PreviewClassifier {
    private val decodableImages = setOf(".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp")

    // The PC needs the host's thumbnail for these. Android's ImageDecoder reads them directly.
    private val heifImages = setOf(".heic", ".heif")

    // Formats that are text but would otherwise be sniffed: listing them skips the extra read.
    private val textExtensions = setOf(
        ".txt", ".log", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".axaml", ".xaml",
        ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".mjs", ".cs", ".csproj", ".props", ".targets",
        ".sln", ".kt", ".kts", ".java", ".gradle", ".py", ".rb", ".go", ".rs", ".c", ".h", ".cpp", ".hpp",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".ini", ".cfg", ".conf", ".toml",
        ".yaml", ".yml", ".properties", ".env", ".gitignore", ".editorconfig", ".sql", ".srt", ".vtt", ".resx",
    )

    // Formats that are never worth sniffing: archives, media, documents and executables.
    private val knownBinary = setOf(
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".xz", ".bz2", ".tar", ".iso", ".img", ".dmg", ".apk", ".aab",
        ".exe", ".dll", ".so", ".dylib", ".msi", ".bin", ".dat", ".db", ".sqlite", ".pdf", ".doc", ".docx",
        ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".3gp", ".tif", ".tiff", ".raw", ".dng", ".cr2",
        ".nef", ".psd", ".ico", ".ttf", ".otf", ".woff", ".woff2", ".class", ".jar", ".pdb", ".pfx", ".p12",
    )

    /**
     * The kind for [fileName]; a folder is always [PreviewKind.None]. [decodesHeif] is true on the phone, where
     * HEIC/HEIF previews at full size; false gives the PC's answer, which the shared vectors check.
     */
    fun classify(fileName: String, isDirectory: Boolean, decodesHeif: Boolean = true): PreviewKind {
        if (isDirectory || fileName.isBlank()) return PreviewKind.None
        val ext = extensionOf(fileName)
        return when {
            ext in decodableImages -> PreviewKind.Image
            ext in heifImages -> if (decodesHeif) PreviewKind.Image else PreviewKind.ImageThumbnailOnly
            ext in textExtensions -> PreviewKind.Text
            ext in knownBinary -> PreviewKind.None
            else -> PreviewKind.Sniff
        }
    }

    /**
     * The extension with its dot, lower-cased, as .NET's `Path.GetExtension` finds it: from the last '.' of the
     * last path segment, "" when there is none. ".gitignore" is its own extension, which is what both lists expect.
     */
    internal fun extensionOf(fileName: String): String {
        val name = fileName.substringAfterLast('/').substringAfterLast('\\')
        val dot = name.lastIndexOf('.')
        return if (dot < 0 || dot == name.length - 1) "" else name.substring(dot).lowercase()
    }
}
