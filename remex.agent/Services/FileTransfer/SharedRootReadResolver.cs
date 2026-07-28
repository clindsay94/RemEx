using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// The single place that decides what a client-supplied <c>rootId</c> means for a <b>read</b> operation:
/// either a pinned/configured shared root, or — under an active full-browse grant — a genuine mounted
/// volume reached through full-device browsing (RemEx-39jw).
///
/// <para><b>Why this type exists.</b> That decision used to be duplicated: <c>FileTransferHandler</c> had a
/// private copy and the v3 sender in <see cref="TransferSessionManager"/> had none at all, so extending
/// full-device browsing to downloads (RemEx-39jw / f8d2fac) fixed only the legacy v2 path while every real
/// Android download — which goes through the v3 <c>file_transfer_offer</c> flow — kept failing with
/// "Unknown shared root" (RemEx-hb1t.6). Both paths now resolve here, so the two cannot drift again.</para>
///
/// <para><b>Read-only by construction.</b> There is deliberately no write counterpart: full browse never
/// exposes write/delete on a bare volume (RemEx-hb1t). Callers needing a writable target must reject an
/// unconfigured rootId, or re-express it against a pinned root via
/// <see cref="IFileTransferService.TryMapVolumePathToConfiguredRootAsync"/> (RemEx-hb1t.3).</para>
/// </summary>
public sealed class SharedRootReadResolver
{
    private readonly IFileTransferService _fileTransferService;
    private readonly IFileTrustService _fileTrustService;
    private readonly VolumeEnumerator _volumeEnumerator;

    public SharedRootReadResolver(
        IFileTransferService fileTransferService,
        IFileTrustService fileTrustService,
        VolumeEnumerator volumeEnumerator)
    {
        _fileTransferService = Guard.NotNull(fileTransferService);
        _fileTrustService = Guard.NotNull(fileTrustService);
        _volumeEnumerator = Guard.NotNull(volumeEnumerator);
    }

    /// <summary>True when <paramref name="rootId"/> matches one of the pinned/configured shared roots.</summary>
    public async Task<bool> IsConfiguredRootAsync(string rootId, CancellationToken ct)
        => (await _fileTransferService.ListRootsAsync(ct)).Any(r => r.RootId == rootId);

    /// <summary>
    /// Resolves a rootId that is NOT a configured shared root to a genuine, consent-granted mounted volume.
    /// Re-verifies the full-browse grant here — the rootId alone is never trusted, since it arrives from the
    /// client — mirroring the check the browse path already performs.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// The device has no full-browse grant, or the rootId is not a real enumerated volume.
    /// </exception>
    public async Task<FileVolumeInfo> ResolveConsentedVolumeAsync(string rootId, string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !await _fileTrustService.IsFullBrowseGrantedAsync(clientId, ct))
            throw new UnauthorizedAccessException("Full-device browsing has not been granted for this device.");
        return _volumeEnumerator.Enumerate().FirstOrDefault(v => v.Id == rootId)
            ?? throw new UnauthorizedAccessException($"Unknown shared root '{rootId}'.");
    }

    /// <summary>
    /// Opens a file for reading, accepting either a pinned shared root or a consented bare volume. This is
    /// the entry point every download path must use; calling
    /// <see cref="IFileTransferService.OpenForReadAsync"/> directly silently drops full-device browsing.
    /// </summary>
    public async Task<Stream> OpenForReadAsync(string rootId, string relativePath, string? clientId, CancellationToken ct)
    {
        if (await IsConfiguredRootAsync(rootId, ct))
            return await _fileTransferService.OpenForReadAsync(rootId, relativePath, ct);

        var volume = await ResolveConsentedVolumeAsync(rootId, clientId, ct);
        return await _fileTransferService.OpenVolumeForReadAsync(volume.Path, relativePath, ct);
    }
}
