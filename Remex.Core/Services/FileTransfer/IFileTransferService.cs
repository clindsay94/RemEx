using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services.FileTransfer;

public interface IFileTransferService
{
    Task<IReadOnlyList<FileEntry>> BrowseAsync(string path, CancellationToken ct);
    Task<Stream> OpenForReadAsync(string remotePath, CancellationToken ct);
    Task<Stream> OpenForWriteAsync(string remotePath, long expectedBytes, CancellationToken ct);
}
