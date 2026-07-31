using System.Reflection;
using System.Text.Json;
using Remex.Core.Guards;
using Remex.Core.Services;
using Remex.Desktop.Models.Backup;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.Services.Backup;

/// <summary>
/// Thrown when a savefile's <c>formatVersion</c> is missing, zero, or negative — i.e. the file
/// is not a recognizable RemEx savefile at all.
/// </summary>
public sealed class SavefileFormatException : Exception
{
    public SavefileFormatException(string message) : base(message) { }
}

/// <summary>
/// Thrown when a savefile's <c>formatVersion</c> is higher than
/// <see cref="RemexSavefile.CurrentFormatVersion"/> — the file was produced by a newer build of
/// RemEx than the one trying to read it.
/// </summary>
public sealed class SavefileNewerVersionException : Exception
{
    public int FormatVersion { get; }

    public SavefileNewerVersionException(int formatVersion)
        : base($"Savefile format version {formatVersion} is newer than the supported version {RemexSavefile.CurrentFormatVersion}.")
    {
        FormatVersion = formatVersion;
    }
}

/// <summary>
/// Builds, exports, and imports RemEx savefiles (<c>.remexsave</c>), and maintains a silent
/// rolling auto-snapshot in <see cref="BackupsDirectory"/> so a corrupted or missing
/// <c>dashboard_layout.json</c> can offer a first-run restore prompt. Never touches secret
/// material (cert.pfx, paired_clients.json, pinned_hosts.json, keep-session-unlocked.flag) — those
/// files have no corresponding field anywhere in <see cref="RemexSavefileSections"/>.
/// </summary>
public sealed class RemexSavefileService : IDisposable
{
    /// <summary>Matches <see cref="DashboardLayoutService"/>'s own serializer configuration exactly.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int SnapshotDebounceMs = 30_000;
    private const int MaxSnapshotsToKeep = 5;
    private const string SnapshotFilePrefix = "autosave-";
    private const string SnapshotFileExtension = ".remexsave";
    private const string SnapshotTimestampFormat = "yyyyMMdd-HHmmss";

    private readonly DashboardLayoutService _layoutService;
    private readonly ILauncherStorageService _launcherStorage;
    private readonly FileTransferRootSettingsService _fileTransferRootSettings;
    private readonly IDashboardProfileStorageService _hostProfileStorage;
    private readonly object _timerGate = new();
    private Timer? _snapshotTimer;
    private bool _disposed;

    /// <summary><c>%LocalAppData%\Remex\backups</c> (or the Linux/macOS equivalent). Created on demand.</summary>
    public string BackupsDirectory { get; }

    public RemexSavefileService(
        DashboardLayoutService layoutService,
        ILauncherStorageService launcherStorage,
        FileTransferRootSettingsService fileTransferRootSettings,
        IDashboardProfileStorageService hostProfileStorage)
    {
        _layoutService = Guard.NotNull(layoutService);
        _launcherStorage = Guard.NotNull(launcherStorage);
        _fileTransferRootSettings = Guard.NotNull(fileTransferRootSettings);
        _hostProfileStorage = Guard.NotNull(hostProfileStorage);

        BackupsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex",
            "backups");
    }

    /// <summary>Reads all four sections from their live storage services and assembles a savefile envelope.</summary>
    public async Task<RemexSavefile> BuildSavefileAsync(string kind)
    {
        var dashboardLayout = await _layoutService.LoadAsync();
        var launchers = await _launcherStorage.LoadEntriesAsync();
        var fileTransferRoots = await _fileTransferRootSettings.LoadAsync();
        var hostDashboardLayout = await _hostProfileStorage.LoadProfileAsync();

        return new RemexSavefile
        {
            FormatVersion = RemexSavefile.CurrentFormatVersion,
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Os = GetOsIdentifier(),
            Kind = kind,
            Sections = new RemexSavefileSections
            {
                DashboardLayout = dashboardLayout,
                Launchers = launchers,
                FileTransferRoots = fileTransferRoots.ToList(),
                HostDashboardLayout = hostDashboardLayout,
            },
        };
    }

    /// <summary>Serializes a freshly-built savefile to <paramref name="destination"/>.</summary>
    public async Task ExportAsync(Stream destination, string kind = "manual")
    {
        Guard.NotNull(destination);

        var savefile = await BuildSavefileAsync(kind);
        await JsonSerializer.SerializeAsync(destination, savefile, JsonOptions);
    }

    /// <summary>
    /// Parses <paramref name="source"/> fully before applying anything — a corrupt or
    /// unrecognizable file throws and leaves all existing state untouched. Once parsed, each
    /// present section is applied independently; a failure in one section is recorded as a
    /// warning and does not prevent the others from applying. The dashboard layout section (if
    /// present) is always applied last, since loading it re-applies the theme and language live.
    /// </summary>
    public async Task<SavefileImportResult> ImportAsync(Stream source)
    {
        Guard.NotNull(source);

        var savefile = await JsonSerializer.DeserializeAsync<RemexSavefile>(source, JsonOptions)
            ?? throw new SavefileFormatException("Savefile could not be parsed.");

        ValidateFormatVersion(savefile.FormatVersion);

        var applied = new List<string>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var sections = savefile.Sections ?? new RemexSavefileSections();

        await ImportLaunchersAsync(sections.Launchers, applied, skipped, warnings);
        await ImportFileTransferRootsAsync(sections.FileTransferRoots, applied, skipped, warnings);
        await ImportHostDashboardLayoutAsync(sections.HostDashboardLayout, applied, skipped, warnings);

        // Dashboard layout is applied LAST: loading it re-applies the live theme and language.
        await ImportDashboardLayoutAsync(sections.DashboardLayout, applied, skipped, warnings);

        return new SavefileImportResult(applied, skipped, warnings);
    }

    private async Task ImportLaunchersAsync(List<Core.Models.AppEntry>? launchers, List<string> applied, List<string> skipped, List<string> warnings)
    {
        if (launchers is null)
        {
            skipped.Add(nameof(RemexSavefileSections.Launchers));
            return;
        }

        try
        {
            foreach (var entry in launchers)
            {
                if (!string.IsNullOrWhiteSpace(entry.TargetPath) && !File.Exists(entry.TargetPath) && !Directory.Exists(entry.TargetPath))
                {
                    warnings.Add($"Launcher entry '{entry.DisplayName}' points to a path that no longer exists on this machine: {entry.TargetPath}");
                }
            }

            await _launcherStorage.SaveEntriesAsync(launchers);
            applied.Add(nameof(RemexSavefileSections.Launchers));
        }
        catch (Exception ex)
        {
            warnings.Add($"Launcher apps could not be imported: {ex.Message}");
        }
    }

    private async Task ImportFileTransferRootsAsync(List<FileTransferRootConfiguration>? roots, List<string> applied, List<string> skipped, List<string> warnings)
    {
        if (roots is null)
        {
            skipped.Add(nameof(RemexSavefileSections.FileTransferRoots));
            return;
        }

        try
        {
            var validRoots = new List<FileTransferRootConfiguration>();
            foreach (var root in roots)
            {
                if (Directory.Exists(root.AbsolutePath))
                {
                    validRoots.Add(root);
                }
                else
                {
                    warnings.Add($"Shared folder '{root.DisplayName}' no longer exists on this machine and was skipped: {root.AbsolutePath}");
                }
            }

            await _fileTransferRootSettings.SaveAsync(validRoots);
            applied.Add(nameof(RemexSavefileSections.FileTransferRoots));
        }
        catch (Exception ex)
        {
            warnings.Add($"Shared folders could not be imported: {ex.Message}");
        }
    }

    private async Task ImportHostDashboardLayoutAsync(Core.Models.DashboardProfile? hostProfile, List<string> applied, List<string> skipped, List<string> warnings)
    {
        if (hostProfile is null)
        {
            skipped.Add(nameof(RemexSavefileSections.HostDashboardLayout));
            return;
        }

        try
        {
            await _hostProfileStorage.SaveProfileAsync(hostProfile);
            applied.Add(nameof(RemexSavefileSections.HostDashboardLayout));
        }
        catch (Exception ex)
        {
            warnings.Add($"Host dashboard layout could not be imported: {ex.Message}");
        }
    }

    private async Task ImportDashboardLayoutAsync(Core.Models.DashboardProfile? profile, List<string> applied, List<string> skipped, List<string> warnings)
    {
        if (profile is null)
        {
            skipped.Add(nameof(RemexSavefileSections.DashboardLayout));
            return;
        }

        try
        {
            await _layoutService.SaveAsync(profile);
            // Re-applies the theme (ThemeService.ApplyCustomization) as a side effect of loading.
            await _layoutService.LoadAsync();

            if (!string.IsNullOrWhiteSpace(profile.Language))
            {
                LocalizationService.Instance.SetCulture(profile.Language);
            }

            applied.Add(nameof(RemexSavefileSections.DashboardLayout));
        }
        catch (Exception ex)
        {
            warnings.Add($"Dashboard layout could not be imported: {ex.Message}");
        }
    }

    /// <summary>Throws <see cref="SavefileFormatException"/> or <see cref="SavefileNewerVersionException"/> for an unreadable format version; otherwise returns.</summary>
    internal static void ValidateFormatVersion(int formatVersion)
    {
        if (formatVersion < 1)
        {
            throw new SavefileFormatException($"Savefile is missing a valid format version (got {formatVersion}).");
        }

        if (formatVersion > RemexSavefile.CurrentFormatVersion)
        {
            throw new SavefileNewerVersionException(formatVersion);
        }
    }

    // ═══════════════ Silent rolling auto-snapshot ═══════════════

    /// <summary>
    /// Call whenever any tracked state changes. Restarts a 30-second debounce timer; when it
    /// elapses, a fresh snapshot is written to <see cref="BackupsDirectory"/> and older snapshots
    /// beyond the newest 5 are pruned. Best-effort — all failures are swallowed.
    /// </summary>
    public void NotifyStateChanged()
    {
        lock (_timerGate)
        {
            if (_disposed) return;

            _snapshotTimer?.Dispose();
            _snapshotTimer = new Timer(_ => _ = WriteSnapshotSafeAsync(), null, SnapshotDebounceMs, Timeout.Infinite);
        }
    }

    private async Task WriteSnapshotSafeAsync()
    {
        try
        {
            await WriteSnapshotAsync();
        }
        catch
        {
            // Best-effort — a failed background snapshot must never surface to the user.
        }
    }

    /// <summary>Writes an autosnapshot savefile immediately and prunes to the newest 5.</summary>
    public async Task WriteSnapshotAsync()
    {
        Directory.CreateDirectory(BackupsDirectory);

        var savefile = await BuildSavefileAsync("autosnapshot");
        var fileName = $"{SnapshotFilePrefix}{DateTime.UtcNow.ToString(SnapshotTimestampFormat)}{SnapshotFileExtension}";
        var path = Path.Combine(BackupsDirectory, fileName);

        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, savefile, JsonOptions);
        }

        PruneSnapshots(BackupsDirectory, MaxSnapshotsToKeep);
    }

    /// <summary>Deletes the oldest snapshot files in <paramref name="directory"/> beyond the newest <paramref name="keep"/>. Best-effort.</summary>
    internal static void PruneSnapshots(string directory, int keep)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            // Filenames are "autosave-yyyyMMdd-HHmmss.remexsave" — lexicographic order is
            // chronological order, so a plain ordinal sort is sufficient and avoids extra parsing.
            var files = Directory.GetFiles(directory, $"{SnapshotFilePrefix}*{SnapshotFileExtension}")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();

            foreach (var stale in files.Skip(keep))
            {
                try { File.Delete(stale); } catch { /* best-effort */ }
            }
        }
        catch
        {
            // Best-effort — pruning failures must never surface to the user.
        }
    }

    /// <summary>The most recent autosnapshot's full path, or null if none exists.</summary>
    public string? TryGetLatestSnapshotPath()
    {
        try
        {
            if (!Directory.Exists(BackupsDirectory)) return null;

            return Directory.GetFiles(BackupsDirectory, $"{SnapshotFilePrefix}*{SnapshotFileExtension}")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string GetOsIdentifier()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        return "other";
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            _disposed = true;
            _snapshotTimer?.Dispose();
            _snapshotTimer = null;
        }
    }
}
