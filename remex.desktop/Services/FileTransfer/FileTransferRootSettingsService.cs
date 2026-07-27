using System.Text.Json;
using Remex.Core.Services;

namespace Remex.Desktop.Services.FileTransfer;

public sealed class FileTransferRootSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;

    public FileTransferRootSettingsService()
    {
        // Mirror FileTransferService (the live host) EXACTLY: resolve the shared-roots store through
        // RemexDataPaths so Settings writes the SAME file the host reads. On Windows that store is
        // machine-wide at C:\ProgramData\RemEx (originally because the host ran as the LocalSystem
        // service, and still so it survives a change of signed-in user); writing to per-user
        // LocalApplicationData here meant adding/removing a shared root in Settings never reached the
        // host after the one-time migration (RemEx-y8xy). Off Windows the legacy per-user folder is
        // returned verbatim, so behaviour there is unchanged.
        var legacyFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
        RemexDataPaths.TryMigrateWindowsFile("file_transfer_roots.json");
        _configPath = Path.Combine(baseFolder, "file_transfer_roots.json");
    }

    public async Task<IReadOnlyList<FileTransferRootConfiguration>> LoadAsync()
    {
        if (!File.Exists(_configPath))
        {
            var defaults = CreateDefaultRoots();
            await SaveAsync(defaults);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            var roots = JsonSerializer.Deserialize<List<FileTransferRootConfiguration>>(json, JsonOptions);
            if (roots is not null)
                return NormalizeRoots(roots);
        }
        catch
        {
            // Fall back to defaults when the config is unreadable.
        }

        var fallbackRoots = CreateDefaultRoots();
        await SaveAsync(fallbackRoots);
        return fallbackRoots;
    }

    public async Task SaveAsync(IReadOnlyList<FileTransferRootConfiguration> roots)
    {
        var normalizedRoots = NormalizeRoots(roots);
        var json = JsonSerializer.Serialize(normalizedRoots, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }

    public async Task<IReadOnlyList<FileTransferRootConfiguration>> ResetToDefaultsAsync()
    {
        var defaults = CreateDefaultRoots();
        await SaveAsync(defaults);
        return defaults;
    }

    private static List<FileTransferRootConfiguration> NormalizeRoots(IEnumerable<FileTransferRootConfiguration> roots)
    {
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root.AbsolutePath))
            .Select(root =>
            {
                var fullPath = Path.GetFullPath(root.AbsolutePath);
                var displayName = string.IsNullOrWhiteSpace(root.DisplayName)
                    ? GetDisplayName(fullPath)
                    : root.DisplayName.Trim();

                return root with
                {
                    DisplayName = displayName,
                    AbsolutePath = fullPath,
                    CanRename = root.IsWritable,
                    CanMove = root.IsWritable,
                    CanDelete = root.IsWritable,
                };
            })
            .Where(root => Directory.Exists(root.AbsolutePath))
            .ToList();
    }

    private static List<FileTransferRootConfiguration> CreateDefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var transfers = Path.Combine(home, "RemEx Transfers");
        Directory.CreateDirectory(transfers);

        return NormalizeRoots([
            CreateRoot("transfers", "RemEx Transfers", transfers, isWritable: true),
            CreateRoot("downloads", "Downloads", Path.Combine(home, "Downloads"), isWritable: false),
            CreateRoot("desktop", "Desktop", Path.Combine(home, "Desktop"), isWritable: false),
            CreateRoot("documents", "Documents", Path.Combine(home, "Documents"), isWritable: false),
            CreateRoot("pictures", "Pictures", Path.Combine(home, "Pictures"), isWritable: false),
        ]);
    }

    private static FileTransferRootConfiguration CreateRoot(string rootId, string displayName, string absolutePath, bool isWritable)
    {
        return new FileTransferRootConfiguration
        {
            RootId = rootId,
            DisplayName = displayName,
            AbsolutePath = absolutePath,
            IsWritable = isWritable,
            CanRename = isWritable,
            CanMove = isWritable,
            CanDelete = isWritable,
        };
    }

    private static string GetDisplayName(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }
}

public sealed record FileTransferRootConfiguration
{
    public required string RootId { get; init; }
    public required string DisplayName { get; init; }
    public required string AbsolutePath { get; init; }
    public bool IsWritable { get; init; }
    public bool CanRename { get; init; }
    public bool CanMove { get; init; }
    public bool CanDelete { get; init; }
}
