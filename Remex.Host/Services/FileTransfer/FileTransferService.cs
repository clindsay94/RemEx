using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Host.Services.FileTransfer;

public sealed class FileTransferService : IFileTransferService
{
    private static readonly string[] RestrictedLinuxPaths = ["/proc", "/sys", "/dev"];
    private const long MaxUploadBytes = 5_000_000_000L;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;

    private sealed record ConfiguredRoot
    {
        public required string RootId { get; init; }
        public required string DisplayName { get; init; }
        public required string AbsolutePath { get; init; }
        public bool IsWritable { get; init; }
        public bool CanRename { get; init; }
        public bool CanMove { get; init; }
        public bool CanDelete { get; init; }
    }

    public FileTransferService()
    {
        var baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        Directory.CreateDirectory(baseFolder);
        _configPath = Path.Combine(baseFolder, "file_transfer_roots.json");
    }

    public Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct)
    {
        var roots = LoadConfiguredRoots()
            .Select(root => new FileSharedRoot
            {
                RootId = root.RootId,
                DisplayName = root.DisplayName,
                IsWritable = root.IsWritable,
                CanRename = root.CanRename,
                CanMove = root.CanMove,
                CanDelete = root.CanDelete,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<FileSharedRoot>>(roots);
    }

    public Task<IReadOnlyList<FileEntry>> BrowseAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var dir = new DirectoryInfo(ResolvePath(rootId, relativePath));
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Directory not found in shared root '{rootId}': {relativePath}");

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

    public Task<Stream> OpenForReadAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var resolved = ResolvePath(rootId, relativePath);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found in shared root '{rootId}': {relativePath}");
        return Task.FromResult<Stream>(new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true));
    }

    public Task<Stream> OpenForWriteAsync(string rootId, string relativePath, long expectedBytes, CancellationToken ct)
    {
        if (expectedBytes > MaxUploadBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), $"File too large ({expectedBytes} bytes). Max is {MaxUploadBytes}.");

        var root = GetConfiguredRoot(rootId);
        if (!root.IsWritable)
            throw new UnauthorizedAccessException($"Shared root '{root.DisplayName}' is read-only.");

        var resolved = ResolvePath(rootId, relativePath);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return Task.FromResult<Stream>(new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true));
    }

    private ConfiguredRoot GetConfiguredRoot(string rootId)
    {
        var root = LoadConfiguredRoots().FirstOrDefault(candidate => candidate.RootId == rootId);
        if (root is null)
            throw new UnauthorizedAccessException($"Unknown shared root '{rootId}'.");

        return root;
    }

    private string ResolvePath(string rootId, string relativePath)
    {
        var root = GetConfiguredRoot(rootId);
        var rootPath = Path.GetFullPath(root.AbsolutePath);
        var trimmedRelativePath = string.IsNullOrWhiteSpace(relativePath) || relativePath is "/" or "\\"
            ? string.Empty
            : relativePath.TrimStart('/', '\\');

        var resolved = string.IsNullOrEmpty(trimmedRelativePath)
            ? rootPath
            : Path.GetFullPath(Path.Combine(rootPath, trimmedRelativePath));

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var isInsideRoot = resolved.Equals(rootPath, pathComparison)
            || resolved.StartsWith(rootPath + Path.DirectorySeparatorChar, pathComparison)
            || resolved.StartsWith(rootPath + Path.AltDirectorySeparatorChar, pathComparison);

        if (!isInsideRoot)
            throw new UnauthorizedAccessException($"Access denied: '{relativePath}' escapes shared root '{root.DisplayName}'.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var restricted in RestrictedLinuxPaths)
            {
                if (resolved.StartsWith(restricted + "/", StringComparison.Ordinal) || resolved == restricted)
                    throw new UnauthorizedAccessException($"Access denied: '{relativePath}' is a restricted system path.");
            }
        }

        return resolved;
    }

    private IReadOnlyList<ConfiguredRoot> LoadConfiguredRoots()
    {
        if (!File.Exists(_configPath))
        {
            var defaults = CreateDefaultRoots();
            SaveConfiguredRoots(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var roots = JsonSerializer.Deserialize<List<ConfiguredRoot>>(json, JsonOptions);
            if (roots is { Count: > 0 })
                return roots
                    .Where(root => Directory.Exists(root.AbsolutePath))
                    .Select(root => root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) })
                    .ToList();
        }
        catch
        {
            // Fall back to defaults when the config is unreadable.
        }

        var fallbackRoots = CreateDefaultRoots();
        SaveConfiguredRoots(fallbackRoots);
        return fallbackRoots;
    }

    private void SaveConfiguredRoots(IReadOnlyList<ConfiguredRoot> roots)
    {
        var json = JsonSerializer.Serialize(roots, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static List<ConfiguredRoot> CreateDefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var transfers = Path.Combine(home, "RemEx Transfers");
        Directory.CreateDirectory(transfers);

        var candidates = new List<ConfiguredRoot>
        {
            new()
            {
                RootId = "transfers",
                DisplayName = "RemEx Transfers",
                AbsolutePath = transfers,
                IsWritable = true,
                CanRename = true,
                CanMove = true,
                CanDelete = true,
            },
            new()
            {
                RootId = "downloads",
                DisplayName = "Downloads",
                AbsolutePath = Path.Combine(home, "Downloads"),
                IsWritable = false,
            },
            new()
            {
                RootId = "desktop",
                DisplayName = "Desktop",
                AbsolutePath = Path.Combine(home, "Desktop"),
                IsWritable = false,
            },
            new()
            {
                RootId = "documents",
                DisplayName = "Documents",
                AbsolutePath = Path.Combine(home, "Documents"),
                IsWritable = false,
            },
            new()
            {
                RootId = "pictures",
                DisplayName = "Pictures",
                AbsolutePath = Path.Combine(home, "Pictures"),
                IsWritable = false,
            },
        };

        return candidates
            .Where(root => Directory.Exists(root.AbsolutePath))
            .Select(root => root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) })
            .ToList();
    }
}
