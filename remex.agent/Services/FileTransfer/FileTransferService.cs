using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Remex.Core.Validation;

namespace Remex.Agent.Services.FileTransfer;

public sealed class FileTransferService : IFileTransferService
{
    private const long MaxUploadBytes = 5_000_000_000L;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<FileTransferService> _logger;
    private readonly string _configPath;
    private readonly ThumbnailService _thumbnailService;

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
        _thumbnailService = new ThumbnailService(logger);
    }

    /// <summary>
    /// Test-only seam (visible to <c>Remex.Agent.Tests</c> via <c>InternalsVisibleTo</c>). Points the
    /// roots config at an explicit path so unit tests can drive copy/move/mkdir/search/metadata against a
    /// temp directory without touching the machine-wide ProgramData store. Not used by DI — the default
    /// container only binds public constructors, so this overload is invisible to host bootstrapping.
    /// </summary>
    internal FileTransferService(ILogger<FileTransferService> logger, string configFilePath)
    {
        _logger = logger;
        _configPath = configFilePath;
        _thumbnailService = new ThumbnailService(logger);
    }

    /// <summary>
    /// Test-only seam: writes the given roots directly to the config file, bypassing default-root creation
    /// (which would otherwise create folders in the real user profile). Each tuple maps to a configured
    /// shared root with the supplied permission flags.
    /// </summary>
    internal void SeedRootsForTests(
        params (string rootId, string displayName, string absolutePath,
                bool isWritable, bool canRename, bool canMove, bool canDelete, bool canRemoveRoot)[] roots)
    {
        var configured = roots
            .Select(r => new ConfiguredRoot
            {
                RootId = r.rootId,
                DisplayName = r.displayName,
                AbsolutePath = Path.GetFullPath(r.absolutePath),
                IsWritable = r.isWritable,
                CanRename = r.canRename,
                CanMove = r.canMove,
                CanDelete = r.canDelete,
                CanRemoveRoot = r.canRemoveRoot,
            })
            .ToList();
        SaveConfiguredRoots(configured);
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

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP3: file-manager ops ──
    // Copy / move / mkdir / search / metadata / thumbnail. All paths are resolved through the centralized
    // FilePathValidation helper (plan §2) so the root-escape + Linux system denylist rules are identical to
    // browse/transfer. Permission flags: copy/mkdir require IsWritable; move additionally requires the
    // (previously unwired) CanMove flag.

    public Task CopyAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.IsWritable)
            throw new UnauthorizedAccessException($"Copies are not permitted in '{root.DisplayName}' (read-only).");

        var source = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
        var destination = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, destinationRelativePath, root.DisplayName);

        if (PathsEqual(source, destination))
            throw new IOException("Source and destination are the same.");

        EnsureParentDirectory(destination);

        if (File.Exists(source))
        {
            if (Directory.Exists(destination))
                throw new IOException($"A folder already exists at the destination '{Path.GetFileName(destination)}'.");
            if (File.Exists(destination) && !overwrite)
                throw new IOException($"A file named '{Path.GetFileName(destination)}' already exists.");
            File.Copy(source, destination, overwrite);
        }
        else if (Directory.Exists(source))
        {
            if (IsDestinationInsideSource(source, destination))
                throw new IOException("Cannot copy a folder into itself.");
            if (Directory.Exists(destination) && !overwrite)
                throw new IOException($"A folder named '{Path.GetFileName(destination)}' already exists.");
            CopyDirectoryRecursive(source, destination, overwrite, ct);
        }
        else
        {
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.CanMove)
            throw new UnauthorizedAccessException($"Moves are not permitted in '{root.DisplayName}'.");

        var source = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
        var destination = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, destinationRelativePath, root.DisplayName);

        if (PathsEqual(source, destination))
            throw new IOException("Source and destination are the same.");

        EnsureParentDirectory(destination);

        if (File.Exists(source))
        {
            if (Directory.Exists(destination))
                throw new IOException($"A folder already exists at the destination '{Path.GetFileName(destination)}'.");
            if (File.Exists(destination))
            {
                if (!overwrite)
                    throw new IOException($"A file named '{Path.GetFileName(destination)}' already exists.");
                File.Delete(destination);
            }

            if (AreSameVolume(source, destination))
                File.Move(source, destination);
            else
            {
                // Cross-volume move: File.Move can throw across devices, so realize it as copy+delete.
                File.Copy(source, destination, overwrite: true);
                File.Delete(source);
            }
        }
        else if (Directory.Exists(source))
        {
            if (IsDestinationInsideSource(source, destination))
                throw new IOException("Cannot move a folder into itself.");
            if (Directory.Exists(destination))
            {
                if (!overwrite)
                    throw new IOException($"A folder named '{Path.GetFileName(destination)}' already exists.");
                Directory.Delete(destination, recursive: true);
            }
            else if (File.Exists(destination))
            {
                if (!overwrite)
                    throw new IOException($"A file named '{Path.GetFileName(destination)}' already exists.");
                File.Delete(destination);
            }

            if (AreSameVolume(source, destination))
                Directory.Move(source, destination);
            else
            {
                // Cross-volume directory move: Directory.Move fails across devices, so copy then delete.
                CopyDirectoryRecursive(source, destination, overwrite: true, ct);
                Directory.Delete(source, recursive: true);
            }
        }
        else
        {
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");
        }

        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.IsWritable)
            throw new UnauthorizedAccessException($"New folders are not permitted in '{root.DisplayName}' (read-only).");

        var resolved = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);

        if (Directory.Exists(resolved))
            throw new IOException($"A folder named '{Path.GetFileName(resolved)}' already exists.");
        if (File.Exists(resolved))
            throw new IOException($"A file named '{Path.GetFileName(resolved)}' already exists.");

        Directory.CreateDirectory(resolved);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileSearchEntry>> SearchAsync(
        string rootId, string relativePath, string query, int maxResults, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        var rootPath = Path.GetFullPath(root.AbsolutePath);
        var baseDir = FilePathValidation.ResolveWithinRoot(rootPath, relativePath, root.DisplayName);

        var cap = maxResults <= 0
            ? FileTransferLimits.SearchMaxResults
            : Math.Min(maxResults, FileTransferLimits.SearchMaxResults);

        var results = new List<FileSearchEntry>(Math.Min(cap, 64));
        if (!string.IsNullOrWhiteSpace(query) && Directory.Exists(baseDir))
            SearchRecursive(rootPath, baseDir, query, cap, results, ct);

        return Task.FromResult<IReadOnlyList<FileSearchEntry>>(results);
    }

    public Task<FileMetadata> GetMetadataAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        var resolved = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);

        if (Directory.Exists(resolved))
        {
            var dir = new DirectoryInfo(resolved);
            int itemCount;
            try
            {
                itemCount = dir.EnumerateFileSystemInfos().Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                itemCount = 0;
            }

            return Task.FromResult(new FileMetadata
            {
                Size = 0,
                CreatedUtc = ToUnixMs(dir.CreationTimeUtc),
                ModifiedUtc = ToUnixMs(dir.LastWriteTimeUtc),
                IsDirectory = true,
                ItemCount = itemCount,
                MimeType = null,
                ReadOnly = (dir.Attributes & FileAttributes.ReadOnly) != 0,
            });
        }

        if (File.Exists(resolved))
        {
            var file = new FileInfo(resolved);
            return Task.FromResult(new FileMetadata
            {
                Size = file.Length,
                CreatedUtc = ToUnixMs(file.CreationTimeUtc),
                ModifiedUtc = ToUnixMs(file.LastWriteTimeUtc),
                IsDirectory = false,
                ItemCount = null,
                MimeType = InferMimeType(resolved),
                ReadOnly = file.IsReadOnly,
            });
        }

        throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");
    }

    public async Task<string?> GetThumbnailBase64Async(string rootId, string relativePath, int maxDim, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        var resolved = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found in shared root '{root.DisplayName}': {relativePath}");

        var effectiveMaxDim = maxDim <= 0 ? FileTransferLimits.ThumbnailDefaultMaxDim : maxDim;
        return await _thumbnailService.TryCreateThumbnailBase64Async(
            resolved, effectiveMaxDim, FileTransferLimits.ThumbnailMaxBytes, ct);
    }

    /// <summary>
    /// Recursively collects search hits (name contains <paramref name="query"/>, case-insensitive) under
    /// <paramref name="currentDir"/>, stopping once <paramref name="cap"/> results are gathered. Restricted
    /// system paths are skipped and unreadable subtrees are ignored rather than aborting the whole search.
    /// </summary>
    private static void SearchRecursive(
        string rootPath, string currentDir, string query, int cap, List<FileSearchEntry> results, CancellationToken ct)
    {
        if (results.Count >= cap)
            return;

        ct.ThrowIfCancellationRequested();

        IEnumerable<FileSystemInfo> children;
        try
        {
            children = new DirectoryInfo(currentDir).EnumerateFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (results.Count >= cap)
                return;
            ct.ThrowIfCancellationRequested();

            if (FilePathValidation.IsRestrictedSystemPath(child.FullName))
                continue;

            var isDirectory = child is DirectoryInfo;
            if (child.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FileSearchEntry
                {
                    Name = child.Name,
                    RelativePath = ToRootRelative(rootPath, child.FullName),
                    IsDirectory = isDirectory,
                    SizeBytes = child is FileInfo fi ? fi.Length : 0,
                    ModifiedUnixMs = ToUnixMs(child.LastWriteTimeUtc),
                });
            }

            if (isDirectory)
                SearchRecursive(rootPath, child.FullName, query, cap, results, ct);
        }
    }

    /// <summary>Returns a forward-slash path relative to the root so both PC and Android can re-navigate it.</summary>
    private static string ToRootRelative(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        return relative.Replace('\\', '/');
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }

    private static bool AreSameVolume(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.GetPathRoot(Path.GetFullPath(a)),
            Path.GetPathRoot(Path.GetFullPath(b)),
            comparison);
    }

    private static bool IsDestinationInsideSource(string sourceDir, string destination)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedSource = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDest = Path.GetFullPath(destination);
        return normalizedDest.StartsWith(normalizedSource + Path.DirectorySeparatorChar, comparison)
            || normalizedDest.StartsWith(normalizedSource + Path.AltDirectorySeparatorChar, comparison);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir, bool overwrite, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(target) && !overwrite)
                throw new IOException($"A file named '{Path.GetFileName(target)}' already exists.");
            File.Copy(file, target, overwrite);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectoryRecursive(sub, Path.Combine(destDir, Path.GetFileName(sub)), overwrite, ct);
        }
    }

    /// <summary>
    /// Best-effort MIME type from the file extension for <see cref="FileMetadata.MimeType"/>. Covers common
    /// document/image/media/archive types; returns null for unknown extensions (the client shows a generic
    /// icon). Intentionally a small static table — no <c>System.Web</c>/registry lookups needed on the host.
    /// </summary>
    private static string? InferMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".ini" or ".cfg" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        ".rar" => "application/vnd.rar",
        ".gz" or ".tgz" => "application/gzip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".heic" => "image/heic",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".webm" => "video/webm",
        ".exe" => "application/vnd.microsoft.portable-executable",
        _ => null,
    };

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

    // Browse/transfer/delete/rename/hash resolve through the SAME shared validator as copy/move/search/
    // metadata/thumbnail, so the root-escape guard and the full restricted-system-path denylist
    // (/proc, /sys, /dev, /run, /boot/efi) are enforced uniformly rather than via a narrower local copy.
    private string ResolvePath(string rootId, string relativePath)
    {
        var root = GetConfiguredRoot(rootId);
        return FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
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
