using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// Minimal <see cref="IFileTransferService"/> test double: only <see cref="PromoteStagedFileAsync"/>
/// is real (lands the verified partial in a temp destination directory and records the path); every
/// other member throws, since the receiver state machine under test never touches them.
/// </summary>
internal sealed class FakeFileTransferService(string destDir) : IFileTransferService
{
    public string? LastWrittenPath { get; private set; }

    /// <summary>
    /// Mirrors the real service: same destination resolution, and it CONSUMES the staging file the
    /// way a rename does. That second part matters — the manager deletes staging afterwards, so a
    /// fake that left the partial in place would hide a double-delete or a use-after-move.
    /// </summary>
    public Task PromoteStagedFileAsync(string rootId, string relativePath, long expectedBytes, string stagingPath, CancellationToken ct)
    {
        var full = ResolveDestination(relativePath);
        LastWrittenPath = full;
        File.Move(stagingPath, full, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenForWriteAsync(string rootId, string relativePath, long expectedBytes, CancellationToken ct)
    {
        var full = ResolveDestination(relativePath);
        LastWrittenPath = full;
        Stream stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        return Task.FromResult(stream);
    }

    private string ResolveDestination(string relativePath)
    {
        var full = Path.Combine(destDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        return full;
    }

    public Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileEntry>> BrowseAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileEntry>> BrowseVolumeAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<Stream> OpenForReadAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<Stream> OpenVolumeForReadAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task RenameAsync(string rootId, string relativePath, string newName, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> ComputeSha256Async(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> ComputeVolumeSha256Async(string volumeAbsolutePath, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileSharedRoot>> AddRootFromPathAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileSharedRoot>> RemoveRootAsync(string rootId, CancellationToken ct) => throw new NotSupportedException();
    public Task CopyAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
    public Task MoveAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
    public Task CreateDirectoryAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileSearchEntry>> SearchAsync(string rootId, string relativePath, string query, int maxResults, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileSearchEntry>> SearchVolumeAsync(string volumeAbsolutePath, string relativePath, string query, int maxResults, CancellationToken ct) => throw new NotSupportedException();
    public Task<FileMetadata> GetMetadataAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<FileMetadata> GetVolumeMetadataAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> GetThumbnailBase64Async(string rootId, string relativePath, int maxDim, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> GetVolumeThumbnailBase64Async(string volumeAbsolutePath, string relativePath, int maxDim, CancellationToken ct) => throw new NotSupportedException();
    public Task<(string RootId, string RelativePath)?> TryMapVolumePathToConfiguredRootAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct) => Task.FromResult<(string, string)?>(null);
}
