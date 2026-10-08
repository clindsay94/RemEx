namespace Remex.Desktop.Services.FilePreview;

/// <summary>What the preview pane shows for a file (2026-10-08 redesign).</summary>
public enum PreviewKind
{
    /// <summary>A folder, or a file with nothing to preview: a large icon and the details.</summary>
    None,

    /// <summary>An image the PC can decode at full size, with zoom and pan.</summary>
    Image,

    /// <summary>An image only the host can decode (HEIC/HEIF): its thumbnail, plus "Download to view full size".</summary>
    ImageThumbnailOnly,

    /// <summary>Text, code or a log, in the monospace viewer (and the only kind that can live-tail).</summary>
    Text,

    /// <summary>Extension unknown: the first bytes are sniffed, and it is shown as text unless they look binary.</summary>
    Sniff,
}

/// <summary>Chooses a <see cref="PreviewKind"/> from a file name. Pure, so the rules are unit-tested.</summary>
public static class PreviewClassifier
{
    private static readonly HashSet<string> DecodableImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
    };

    private static readonly HashSet<string> HostOnlyImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif",
    };

    // Formats that are text but would otherwise be sniffed: listing them skips the extra read.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".axaml", ".xaml",
        ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".mjs", ".cs", ".csproj", ".props", ".targets",
        ".sln", ".kt", ".kts", ".java", ".gradle", ".py", ".rb", ".go", ".rs", ".c", ".h", ".cpp", ".hpp",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".ini", ".cfg", ".conf", ".toml",
        ".yaml", ".yml", ".properties", ".env", ".gitignore", ".editorconfig", ".sql", ".srt", ".vtt", ".resx",
    };

    // Formats that are never worth sniffing: archives, media, documents and executables.
    private static readonly HashSet<string> KnownBinary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".xz", ".bz2", ".tar", ".iso", ".img", ".dmg", ".apk", ".aab",
        ".exe", ".dll", ".so", ".dylib", ".msi", ".bin", ".dat", ".db", ".sqlite", ".pdf", ".doc", ".docx",
        ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".3gp", ".tif", ".tiff", ".raw", ".dng", ".cr2",
        ".nef", ".psd", ".ico", ".ttf", ".otf", ".woff", ".woff2", ".class", ".jar", ".pdb", ".pfx", ".p12",
    };

    /// <summary>The kind for <paramref name="fileName"/>; a folder is always <see cref="PreviewKind.None"/>.</summary>
    public static PreviewKind Classify(string fileName, bool isDirectory)
    {
        if (isDirectory || string.IsNullOrWhiteSpace(fileName))
            return PreviewKind.None;

        var ext = Path.GetExtension(fileName);
        if (DecodableImages.Contains(ext)) return PreviewKind.Image;
        if (HostOnlyImages.Contains(ext)) return PreviewKind.ImageThumbnailOnly;
        if (TextExtensions.Contains(ext)) return PreviewKind.Text;
        if (KnownBinary.Contains(ext)) return PreviewKind.None;
        return PreviewKind.Sniff;
    }
}
