using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
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

    private readonly ILogger<FileTransferService> _logger;
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
        public bool CanRemoveRoot { get; init; }
    }

    public FileTransferService(ILogger<FileTransferService> logger)
    {
        _logger = logger;
        // Host-only store. Relocated to machine-wide ProgramData on Windows (unchanged elsewhere)
        // so configured shared roots survive the host running as the LocalSystem service.
        var legacyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
        RemexDataPaths.TryMigrateWindowsFile("file_transfer_roots.json");
        _configPath = Path.Combine(baseFolder, "file_transfer_roots.json");
    }

    public Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct)
        => Task.FromResult(MapToSharedRoots(LoadConfiguredRoots()));

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

    public Task DeleteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.CanDelete)
            throw new UnauthorizedAccessException($"Deletions are not permitted in '{root.DisplayName}'.");

        var resolved = ResolvePath(rootId, relativePath);

        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
        else if (File.Exists(resolved))
            File.Delete(resolved);
        else
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");

        return Task.CompletedTask;
    }

    public Task RenameAsync(string rootId, string relativePath, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("New name is invalid.");

        var root = GetConfiguredRoot(rootId);
        if (!root.CanRename)
            throw new UnauthorizedAccessException($"Renames are not permitted in '{root.DisplayName}'.");

        var resolved = ResolvePath(rootId, relativePath);
        var parentDir = Path.GetDirectoryName(resolved)
            ?? throw new InvalidOperationException("Cannot rename a root path.");
        var destination = Path.Combine(parentDir, newName);

        if (Directory.Exists(resolved))
            Directory.Move(resolved, destination);
        else if (File.Exists(resolved))
            File.Move(resolved, destination, overwrite: false);
        else
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");

        return Task.CompletedTask;
    }

    public async Task<string> ComputeSha256Async(string rootId, string relativePath, CancellationToken ct)
    {
        var resolved = ResolvePath(rootId, relativePath);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found in shared root '{rootId}': {relativePath}");

        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToBase64String(hash);
    }

    public Task<IReadOnlyList<FileSharedRoot>> AddRootFromPathAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct)
    {
        var absolutePath = ResolvePath(sourceRootId, sourceRelativePath);
        if (!Directory.Exists(absolutePath))
            throw new DirectoryNotFoundException($"Directory does not exist: {absolutePath}");

        var roots = LoadConfiguredRoots().ToList();
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (roots.Any(r => r.AbsolutePath.Equals(absolutePath, pathComparison)))
            throw new InvalidOperationException("This folder is already a shared root.");

        var displayName = Path.GetFileName(absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rootId = $"custom_{Guid.NewGuid():N}";

        roots.Add(new ConfiguredRoot
        {
            RootId = rootId,
            DisplayName = displayName,
            AbsolutePath = absolutePath,
            IsWritable = true,
            CanRename = true,
            CanMove = true,
            CanDelete = true,
            CanRemoveRoot = true,
        });

        SaveConfiguredRoots(roots);
        return Task.FromResult<IReadOnlyList<FileSharedRoot>>(MapToSharedRoots(roots));
    }

    public Task<IReadOnlyList<FileSharedRoot>> RemoveRootAsync(string rootId, CancellationToken ct)
    {
        var roots = LoadConfiguredRoots().ToList();
        var target = roots.FirstOrDefault(r => r.RootId == rootId)
            ?? throw new InvalidOperationException($"Shared root '{rootId}' not found.");

        if (!target.CanRemoveRoot)
            throw new UnauthorizedAccessException($"The root '{target.DisplayName}' cannot be removed.");

        roots.RemoveAll(r => r.RootId == rootId);
        SaveConfiguredRoots(roots);
        return Task.FromResult<IReadOnlyList<FileSharedRoot>>(MapToSharedRoots(roots));
    }

    private static IReadOnlyList<FileSharedRoot> MapToSharedRoots(IReadOnlyList<ConfiguredRoot> roots)
        => roots.Select(r => new FileSharedRoot
        {
            RootId = r.RootId,
            DisplayName = r.DisplayName,
            IsWritable = r.IsWritable,
            CanRename = r.CanRename,
            CanMove = r.CanMove,
            CanDelete = r.CanDelete,
            CanRemoveRoot = r.CanRemoveRoot,
        }).ToList();

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
            if (roots is not null)
                return roots
                    .Where(root => !string.IsNullOrWhiteSpace(root.AbsolutePath))
                    .Where(root => Directory.Exists(root.AbsolutePath))
                    .Select(root => root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) })
                    .ToList();
        }
        catch (Exception ex)
        {
            // Do not rethrow — empty/default roots is a recoverable state.
            // Log so operators can diagnose configuration corruption or permission issues.
            _logger.LogError(ex,
                "Failed to load configured file-transfer roots from {Path}. " +
                "Falling back to defaults.", _configPath);
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
                CanRemoveRoot = false,
            },
            new()
            {
                RootId = "downloads",
                DisplayName = "Downloads",
                AbsolutePath = Path.Combine(home, "Downloads"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
            new()
            {
                RootId = "desktop",
                DisplayName = "Desktop",
                AbsolutePath = Path.Combine(home, "Desktop"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
            new()
            {
                RootId = "documents",
                DisplayName = "Documents",
                AbsolutePath = Path.Combine(home, "Documents"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
            new()
            {
                RootId = "pictures",
                DisplayName = "Pictures",
                AbsolutePath = Path.Combine(home, "Pictures"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
        };

        return candidates
            .Where(root => Directory.Exists(root.AbsolutePath))
            .Select(root => root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) })
            .ToList();
    }
}
