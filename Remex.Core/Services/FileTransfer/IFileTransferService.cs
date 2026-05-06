using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services.FileTransfer;

public interface IFileTransferService
{
    Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct);
    Task<IReadOnlyList<FileEntry>> BrowseAsync(string rootId, string relativePath, CancellationToken ct);
    Task<Stream> OpenForReadAsync(string rootId, string relativePath, CancellationToken ct);
    Task<Stream> OpenForWriteAsync(string rootId, string relativePath, long expectedBytes, CancellationToken ct);
}
