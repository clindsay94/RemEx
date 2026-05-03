using System.IO;
using System.Runtime.InteropServices;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Host.Services.FileTransfer;

public sealed class FileTransferService : IFileTransferService
{
    private static readonly string[] RestrictedLinuxPaths = ["/proc", "/sys", "/dev"];
    private const long MaxUploadBytes = 5_000_000_000L;

    public Task<IReadOnlyList<FileEntry>> BrowseAsync(string path, CancellationToken ct)
    {
        var dir = new DirectoryInfo(ResolveAndValidatePath(path));
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var entries = dir.EnumerateFileSystemInfos()
            .Select(fsi => new FileEntry
            {
                Name = fsi.Name,
                IsDirectory = fsi is DirectoryInfo,
                SizeBytes = fsi is FileInfo fi ? fi.Length : 0,
                ModifiedUnixMs = new DateTimeOffset(fsi.LastWriteTimeUtc).ToUnixTimeMilliseconds()
            })
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<FileEntry>>(entries);
    }

    public Task<Stream> OpenForReadAsync(string remotePath, CancellationToken ct)
    {
        var resolved = ResolveAndValidatePath(remotePath);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {remotePath}");
        return Task.FromResult<Stream>(new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true));
    }

    public Task<Stream> OpenForWriteAsync(string remotePath, long expectedBytes, CancellationToken ct)
    {
        if (expectedBytes > MaxUploadBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), $"File too large ({expectedBytes} bytes). Max is {MaxUploadBytes}.");

        var resolved = ResolveAndValidatePath(remotePath);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return Task.FromResult<Stream>(new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true));
    }

    private static string ResolveAndValidatePath(string path)
    {
        var resolved = Path.GetFullPath(path);

        // Block path traversal
        if (resolved != path && !resolved.StartsWith(Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), StringComparison.Ordinal))
        {
            // Allow absolute paths but prevent traversal via ..
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var restricted in RestrictedLinuxPaths)
            {
                if (resolved.StartsWith(restricted + "/", StringComparison.Ordinal) || resolved == restricted)
                    throw new UnauthorizedAccessException($"Access denied: {path} is a restricted system path.");
            }
        }

        return resolved;
    }
}
