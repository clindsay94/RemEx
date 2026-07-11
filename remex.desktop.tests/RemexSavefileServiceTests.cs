using System.Text;
using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Models.Backup;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.Tests;

/// <summary>
/// Tests for <see cref="RemexSavefileService"/>: envelope validation (format version
/// compatibility), section-by-section import behavior (skip-if-missing, warn-and-continue on a
/// single section's failure), and rolling auto-snapshot retention.
///
/// <see cref="DashboardLayoutService"/> and <see cref="FileTransferRootSettingsService"/> always
/// resolve fixed, machine-wide storage paths (see <c>RemexDataPaths</c>) and have no
/// test-friendly path override, unlike <see cref="LauncherStorageService"/> (which accepts a
/// storage folder in its constructor) and <see cref="IDashboardProfileStorageService"/> (an
/// interface, faked below). To avoid a unit test ever touching a developer's real
/// <c>dashboard_layout.json</c> / <c>file_transfer_roots.json</c> / <c>RemEx Transfers</c> folder,
/// every test below either (a) leaves the DashboardLayout/FileTransferRoots sections absent from
/// the savefile under test, so <see cref="RemexSavefileService.ImportAsync"/> never calls into
/// those two live services, or (b) validates the envelope/serialization shape for those sections
/// directly against <see cref="RemexSavefileService.JsonOptions"/>, without going through the
/// live services at all.
/// </summary>
public sealed class RemexSavefileServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remex-savefile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Builds a real <see cref="RemexSavefileService"/> whose <see cref="ILauncherStorageService"/>
    /// is redirected to a temp folder and whose <see cref="IDashboardProfileStorageService"/> is a
    /// fake — the only two dependencies that support test isolation. The remaining two
    /// dependencies (<see cref="DashboardLayoutService"/>, <see cref="FileTransferRootSettingsService"/>)
    /// are real instances bound to the real machine-wide paths; safe to construct (no I/O happens
    /// in their constructors) as long as tests never populate the corresponding savefile sections.
    /// </summary>
    private static RemexSavefileService CreateService(string launcherDir, IDashboardProfileStorageService? hostStorage = null)
    {
        return new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            new LauncherStorageService(launcherDir),
            new FileTransferRootSettingsService(),
            hostStorage ?? new FakeDashboardProfileStorageService());
    }

    [Fact]
    public async Task ImportAsync_AppliesLaunchers_PreservingAppEntryIdsAndFields()
    {
        var launcherDir = CreateTempDir();
        var entries = new List<AppEntry>
        {
            new(Guid.NewGuid(), "Notepad", @"C:\Windows\System32\notepad.exe", "#FF00FF", null, 0),
            new(Guid.NewGuid(), "Calculator", @"C:\Windows\System32\calc.exe", "#00FF00", "aWNvbg==", 1),
        };

        var savefile = new RemexSavefile
        {
            FormatVersion = RemexSavefile.CurrentFormatVersion,
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = "2.1.0",
            Os = "windows",
            Kind = "manual",
            Sections = new RemexSavefileSections { Launchers = entries },
        };

        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, savefile, RemexSavefileService.JsonOptions);
        stream.Position = 0;

        var service = CreateService(launcherDir);
        var result = await service.ImportAsync(stream);

        Assert.Contains(nameof(RemexSavefileSections.Launchers), result.AppliedSections);
        Assert.Contains(nameof(RemexSavefileSections.DashboardLayout), result.SkippedSections);
        Assert.Contains(nameof(RemexSavefileSections.FileTransferRoots), result.SkippedSections);
        Assert.Contains(nameof(RemexSavefileSections.HostDashboardLayout), result.SkippedSections);
        Assert.Empty(result.Warnings);

        var reloaded = await new LauncherStorageService(launcherDir).LoadEntriesAsync();
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(entries[0].Id, reloaded[0].Id);
        Assert.Equal(entries[1].Id, reloaded[1].Id);
        Assert.Equal(entries[0].DisplayName, reloaded[0].DisplayName);
        Assert.Equal(entries[0].TargetPath, reloaded[0].TargetPath);
        Assert.Equal(entries[0].HexColor, reloaded[0].HexColor);
        Assert.Equal(entries[1].IconBase64, reloaded[1].IconBase64);
        Assert.Equal(entries[1].Order, reloaded[1].Order);
    }

    [Fact]
    public void SavefileEnvelope_RoundTrips_DashboardProfileAndFileTransferRootFields_AtSerializerLevel()
    {
        // DashboardLayoutService and FileTransferRootSettingsService always resolve fixed,
        // machine-wide paths with no test override (see class remarks) — so the round trip for
        // these two sections is validated directly against the envelope's JSON shape rather than
        // through the live services.
        var profile = new DashboardProfile
        {
            ProfileName = "Test Profile",
            Language = "es",
            HostAddress = "wss://192.168.1.5:5005/ws",
            StreamQuality = 77,
            StreamFps = 45,
            IsSnapToGridEnabled = true,
            GridSize = 25,
            PinnedSensorIds = new List<string> { "CPU Temp", "GPU Load" },
        };

        var roots = new List<FileTransferRootConfiguration>
        {
            new()
            {
                RootId = "transfers",
                DisplayName = "RemEx Transfers",
                AbsolutePath = @"C:\Users\test\RemEx Transfers",
                IsWritable = true,
            },
        };

        var savefile = new RemexSavefile
        {
            FormatVersion = RemexSavefile.CurrentFormatVersion,
            CreatedAtUtc = new DateTime(2026, 7, 11, 18, 30, 0, DateTimeKind.Utc),
            AppVersion = "2.1.0",
            Os = "windows",
            Kind = "manual",
            Sections = new RemexSavefileSections
            {
                DashboardLayout = profile,
                FileTransferRoots = roots,
            },
        };

        var json = JsonSerializer.Serialize(savefile, RemexSavefileService.JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<RemexSavefile>(json, RemexSavefileService.JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.FormatVersion);
        Assert.NotNull(roundTripped.Sections.DashboardLayout);
        Assert.Equal("Test Profile", roundTripped.Sections.DashboardLayout!.ProfileName);
        Assert.Equal("es", roundTripped.Sections.DashboardLayout!.Language);
        Assert.Equal(77, roundTripped.Sections.DashboardLayout!.StreamQuality);
        Assert.Equal(45, roundTripped.Sections.DashboardLayout!.StreamFps);
        Assert.True(roundTripped.Sections.DashboardLayout!.IsSnapToGridEnabled);
        Assert.Equal(new[] { "CPU Temp", "GPU Load" }, roundTripped.Sections.DashboardLayout!.PinnedSensorIds);
        Assert.NotNull(roundTripped.Sections.FileTransferRoots);
        var root = Assert.Single(roundTripped.Sections.FileTransferRoots!);
        Assert.Equal("RemEx Transfers", root.DisplayName);
        Assert.True(root.IsWritable);

        // Zero secret material anywhere in the serialized JSON.
        Assert.DoesNotContain("cert.pfx", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paired_clients", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinned_hosts", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep-session-unlocked", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_ThrowsSavefileNewerVersionException_WhenFormatVersionIsNewerThanCurrent()
    {
        var launcherDir = CreateTempDir();
        var json = /*lang=json,strict*/
            """{"formatVersion":2,"createdAtUtc":"2026-07-11T00:00:00Z","appVersion":"9.9.9","os":"windows","kind":"manual","sections":{}}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var service = CreateService(launcherDir);

        var ex = await Assert.ThrowsAsync<SavefileNewerVersionException>(() => service.ImportAsync(stream));
        Assert.Equal(2, ex.FormatVersion);
    }

    [Fact]
    public async Task ImportAsync_ThrowsSavefileFormatException_WhenFormatVersionIsMissing()
    {
        var launcherDir = CreateTempDir();

        // No "formatVersion" property at all — RemexSavefile.FormatVersion has no default value
        // specifically so this deserializes to 0, not to CurrentFormatVersion.
        var json = /*lang=json,strict*/
            """{"createdAtUtc":"2026-07-11T00:00:00Z","appVersion":"1.0.0","os":"windows","kind":"manual","sections":{}}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var service = CreateService(launcherDir);

        await Assert.ThrowsAsync<SavefileFormatException>(() => service.ImportAsync(stream));
    }

    [Fact]
    public async Task ImportAsync_IgnoresUnknownProperties_AndSkipsMissingLaunchers_WhileApplyingPresentSections()
    {
        var launcherDir = CreateTempDir();
        var hostStorage = new FakeDashboardProfileStorageService();

        // "launchers" is entirely absent from "sections" (missing, not just null), and the
        // top-level document carries an unrecognized property that must be silently ignored.
        var json = /*lang=json,strict*/
            """
            {
              "formatVersion": 1,
              "createdAtUtc": "2026-07-11T18:30:00Z",
              "appVersion": "2.1.0",
              "os": "windows",
              "kind": "manual",
              "somethingFromAFutureVersion": { "nested": true, "value": 42 },
              "sections": {
                "hostDashboardLayout": { "profileName": "Host Profile", "language": "fr" }
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var service = CreateService(launcherDir, hostStorage);

        var result = await service.ImportAsync(stream);

        Assert.Contains(nameof(RemexSavefileSections.HostDashboardLayout), result.AppliedSections);
        Assert.Contains(nameof(RemexSavefileSections.Launchers), result.SkippedSections);
        Assert.Contains(nameof(RemexSavefileSections.FileTransferRoots), result.SkippedSections);
        Assert.Contains(nameof(RemexSavefileSections.DashboardLayout), result.SkippedSections);
        Assert.Empty(result.Warnings);

        Assert.NotNull(hostStorage.SavedProfile);
        Assert.Equal("Host Profile", hostStorage.SavedProfile!.ProfileName);
        Assert.Equal("fr", hostStorage.SavedProfile!.Language);
    }

    [Fact]
    public void PruneSnapshots_KeepsOnlyTheNewest5_WhenGiven7()
    {
        var dir = CreateTempDir();
        var timestamps = new[]
        {
            "20260101-000000", "20260102-000000", "20260103-000000",
            "20260104-000000", "20260105-000000", "20260106-000000", "20260107-000000",
        };

        foreach (var ts in timestamps)
        {
            File.WriteAllText(Path.Combine(dir, $"autosave-{ts}.remexsave"), "{}");
        }

        RemexSavefileService.PruneSnapshots(dir, keep: 5);

        var remaining = Directory.GetFiles(dir, "autosave-*.remexsave")
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                "autosave-20260103-000000.remexsave",
                "autosave-20260104-000000.remexsave",
                "autosave-20260105-000000.remexsave",
                "autosave-20260106-000000.remexsave",
                "autosave-20260107-000000.remexsave",
            },
            remaining);
    }

    [Fact]
    public void PruneSnapshots_NoOp_WhenDirectoryHas5OrFewer()
    {
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "autosave-20260101-000000.remexsave"), "{}");
        File.WriteAllText(Path.Combine(dir, "autosave-20260102-000000.remexsave"), "{}");

        RemexSavefileService.PruneSnapshots(dir, keep: 5);

        Assert.Equal(2, Directory.GetFiles(dir, "autosave-*.remexsave").Length);
    }

    private sealed class FakeDashboardProfileStorageService : IDashboardProfileStorageService
    {
        private readonly DashboardProfile _profile;

        public FakeDashboardProfileStorageService(DashboardProfile? profile = null)
        {
            _profile = profile ?? new DashboardProfile();
        }

        public DashboardProfile? SavedProfile { get; private set; }

        public Task<DashboardProfile> LoadProfileAsync() => Task.FromResult(_profile);

        public Task SaveProfileAsync(DashboardProfile profile)
        {
            SavedProfile = profile;
            return Task.CompletedTask;
        }
    }
}
