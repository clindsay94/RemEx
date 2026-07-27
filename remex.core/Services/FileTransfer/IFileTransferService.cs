using Remex.Core.Models;

namespace Remex.Core.Services.FileTransfer;

public interface IFileTransferService
{
    Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct);
    Task<IReadOnlyList<FileEntry>> BrowseAsync(string rootId, string relativePath, CancellationToken ct);

    /// <summary>
    /// Full-device browse of a mounted <paramref name="volumeAbsolutePath"/> (a real, enumerated volume
    /// root such as <c>C:\</c>). Navigation is bounded within the volume — no escape above it. The CALLER
    /// MUST have verified the client's full-browse consent grant and that the path is a genuine volume
    /// before calling this; the service enforces only path-escape safety and lists read-only.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> BrowseVolumeAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct);

    /// <summary>
    /// Full-device READ of a file under a mounted <paramref name="volumeAbsolutePath"/>, for download/hash/
    /// search/metadata/thumbnail — the read-only counterparts of <see cref="BrowseVolumeAsync"/>. Same
    /// contract: the CALLER must have verified the client's full-browse consent grant and that the path is
    /// a genuine volume. Full browse never exposes write/delete (RemEx-hb1t) — there are deliberately no
    /// volume-mode equivalents of OpenForWriteAsync/DeleteAsync/RenameAsync/CopyAsync/MoveAsync/
    /// CreateDirectoryAsync; those still require the folder to be pinned via <see cref="AddRootFromPathAsync"/>.
    /// </summary>
    Task<Stream> OpenVolumeForReadAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct);

    /// <summary>Volume-mode counterpart of <see cref="ComputeSha256Async"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    Task<string> ComputeVolumeSha256Async(string volumeAbsolutePath, string relativePath, CancellationToken ct);

    /// <summary>Volume-mode counterpart of <see cref="SearchAsync"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    Task<IReadOnlyList<FileSearchEntry>> SearchVolumeAsync(string volumeAbsolutePath, string relativePath, string query, int maxResults, CancellationToken ct);

    /// <summary>Volume-mode counterpart of <see cref="GetMetadataAsync"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    Task<FileMetadata> GetVolumeMetadataAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct);

    /// <summary>Volume-mode counterpart of <see cref="GetThumbnailBase64Async"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    Task<string?> GetVolumeThumbnailBase64Async(string volumeAbsolutePath, string relativePath, int maxDim, CancellationToken ct);

    /// <summary>
    /// Attempts to re-express a full-device volume path as an equivalent configured shared-root reference
    /// (RemEx-hb1t.3). Resolves <paramref name="relativePath"/> within <paramref name="volumeAbsolutePath"/>
    /// FIRST (collapsing '..' and enforcing the restricted-path denylist — raw input is never compared),
    /// then returns the deepest configured root containing the resolved path, with the path re-based onto
    /// that root: exactly the reference the client could have used directly, so the mapping never widens
    /// access and the target root's own permission flags still apply. Null when the resolved path lies
    /// outside every configured root.
    /// </summary>
    Task<(string RootId, string RelativePath)?> TryMapVolumePathToConfiguredRootAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct);

    Task<Stream> OpenForReadAsync(string rootId, string relativePath, CancellationToken ct);
    Task<Stream> OpenForWriteAsync(string rootId, string relativePath, long expectedBytes, CancellationToken ct);
    Task DeleteAsync(string rootId, string relativePath, CancellationToken ct);
    Task RenameAsync(string rootId, string relativePath, string newName, CancellationToken ct);
    Task<string> ComputeSha256Async(string rootId, string relativePath, CancellationToken ct);
    Task<IReadOnlyList<FileSharedRoot>> AddRootFromPathAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct);
    Task<IReadOnlyList<FileSharedRoot>> RemoveRootAsync(string rootId, CancellationToken ct);

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) ──
    // Implemented by the PC host in WP3; the Android host mirrors the same operations over SAF in WP6.

    /// <summary>Copies a file/directory to a destination relative path within the same root.</summary>
    Task CopyAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct);

    /// <summary>
    /// Moves a file/directory to a destination relative path within the same root. A cross-volume move is
    /// realized as copy+delete by the implementation.
    /// </summary>
    Task MoveAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct);

    /// <summary>Creates a new directory at the given relative path within a root.</summary>
    Task CreateDirectoryAsync(string rootId, string relativePath, CancellationToken ct);

    /// <summary>
    /// Recursively searches under <paramref name="relativePath"/> for names matching <paramref name="query"/>.
    /// Results are capped at min(<paramref name="maxResults"/>, <see cref="Models.FileTransferLimits.SearchMaxResults"/>).
    /// </summary>
    Task<IReadOnlyList<FileSearchEntry>> SearchAsync(string rootId, string relativePath, string query, int maxResults, CancellationToken ct);

    /// <summary>Returns detailed metadata for a single file/directory.</summary>
    Task<FileMetadata> GetMetadataAsync(string rootId, string relativePath, CancellationToken ct);

    /// <summary>
    /// Returns a base64-encoded JPEG thumbnail (≤ <see cref="Models.FileTransferLimits.ThumbnailMaxBytes"/>)
    /// for an image/video, or null when a thumbnail is unavailable. Decoding/encoding lives in the host
    /// (SkiaSharp) — this interface stays NativeAOT-safe by returning only a string.
    /// </summary>
    Task<string?> GetThumbnailBase64Async(string rootId, string relativePath, int maxDim, CancellationToken ct);
}
