using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Models;
using Remex.Agent.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// Security regression tests for <see cref="FileTransferService"/>.
///
/// VULN-4 (RemEx-s032.4): a root derived from an existing shared root via
/// <see cref="FileTransferService.AddRootFromPathAsync"/> must INHERIT the parent's permission flags,
/// not be granted full read/write/delete unconditionally. Otherwise a paired client could browse a
/// read-only default root (Documents/Desktop/Pictures/Downloads), pick a subfolder, and re-pin it as a
/// writable/deletable root — silently defeating the read-only designation.
/// </summary>
public sealed class FileTransferServiceSecurityTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private (FileTransferService service, string rootDir, string subDir) CreateServiceWithSeededRoot(
        bool isWritable, bool canRename, bool canMove, bool canDelete)
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-vuln4-" + Guid.NewGuid().ToString("N"));
        var rootDir = Path.Combine(baseTemp, "root");
        var subDir = Path.Combine(rootDir, "sub");
        Directory.CreateDirectory(subDir);
        _tempDirs.Add(baseTemp);

        var configPath = Path.Combine(baseTemp, "roots.json");
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, configPath);
        service.SeedRootsForTests(("root-1", "Test Root", rootDir, isWritable, canRename, canMove, canDelete, false));

        return (service, rootDir, subDir);
    }

    private static FileSharedRoot DerivedRoot(IReadOnlyList<FileSharedRoot> roots)
        => roots.Single(r => r.RootId.StartsWith("custom_", StringComparison.Ordinal));

    [Fact]
    public async Task AddRootFromPath_ReadOnlyParent_ProducesReadOnlyDerivedRoot()
    {
        // Parent is a read-only root (mirrors the Documents/Desktop/Pictures/Downloads defaults).
        var (service, _, _) = CreateServiceWithSeededRoot(
            isWritable: false, canRename: false, canMove: false, canDelete: false);

        var roots = await service.AddRootFromPathAsync("root-1", "sub", CancellationToken.None);
        var derived = DerivedRoot(roots);

        // The derived root must NOT have escalated to writable/deletable — the whole point of VULN-4.
        Assert.False(derived.IsWritable, "A root derived from a read-only parent must stay read-only.");
        Assert.False(derived.CanRename);
        Assert.False(derived.CanMove);
        Assert.False(derived.CanDelete);

        // It remains individually removable — that's a UI-management flag, not a filesystem-write grant.
        Assert.True(derived.CanRemoveRoot);
    }

    [Fact]
    public async Task AddRootFromPath_WritableParent_ProducesWritableDerivedRoot()
    {
        // Parent is a writable root (mirrors the Transfers default) — the derived root legitimately inherits write.
        var (service, _, _) = CreateServiceWithSeededRoot(
            isWritable: true, canRename: true, canMove: true, canDelete: true);

        var roots = await service.AddRootFromPathAsync("root-1", "sub", CancellationToken.None);
        var derived = DerivedRoot(roots);

        Assert.True(derived.IsWritable);
        Assert.True(derived.CanRename);
        Assert.True(derived.CanMove);
        Assert.True(derived.CanDelete);
        Assert.True(derived.CanRemoveRoot);
    }

    // ── RemEx-hb1t.3: volume-path → pinned-root re-mapping (write-op parity) ──
    // The mapping must be resolve-first (no raw prefix compares), bounded exactly by the pinned root,
    // and must return null — never a widened grant — for paths outside every configured root.

    private (FileTransferService service, string baseTemp) CreateServiceForRemap()
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-hb1t3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseTemp);
        _tempDirs.Add(baseTemp);
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, Path.Combine(baseTemp, "roots.json"));
        return (service, baseTemp);
    }

    [Fact]
    public async Task TryMapVolumePath_InsidePinnedRoot_MapsToRootWithRebasedRelativePath()
    {
        var (service, baseTemp) = CreateServiceForRemap();
        var pinned = Path.Combine(baseTemp, "volume", "pinned");
        Directory.CreateDirectory(pinned);
        service.SeedRootsForTests(("root-1", "Pinned", pinned, true, true, true, true, false));

        var mapped = await service.TryMapVolumePathToConfiguredRootAsync(
            Path.Combine(baseTemp, "volume"), "pinned/sub/file.txt", CancellationToken.None);

        Assert.NotNull(mapped);
        Assert.Equal("root-1", mapped.Value.RootId);
        Assert.Equal("sub/file.txt", mapped.Value.RelativePath);
    }

    [Fact]
    public async Task TryMapVolumePath_ExactlyThePinnedRoot_MapsWithEmptyRelativePath()
    {
        var (service, baseTemp) = CreateServiceForRemap();
        var pinned = Path.Combine(baseTemp, "volume", "pinned");
        Directory.CreateDirectory(pinned);
        service.SeedRootsForTests(("root-1", "Pinned", pinned, true, true, true, true, false));

        var mapped = await service.TryMapVolumePathToConfiguredRootAsync(
            Path.Combine(baseTemp, "volume"), "pinned", CancellationToken.None);

        Assert.NotNull(mapped);
        Assert.Equal("root-1", mapped.Value.RootId);
        Assert.Equal(string.Empty, mapped.Value.RelativePath);
    }

    [Fact]
    public async Task TryMapVolumePath_OutsideEveryRoot_ReturnsNull()
    {
        var (service, baseTemp) = CreateServiceForRemap();
        var pinned = Path.Combine(baseTemp, "volume", "pinned");
        Directory.CreateDirectory(Path.Combine(baseTemp, "volume", "elsewhere"));
        Directory.CreateDirectory(pinned);
        service.SeedRootsForTests(("root-1", "Pinned", pinned, true, true, true, true, false));

        var mapped = await service.TryMapVolumePathToConfiguredRootAsync(
            Path.Combine(baseTemp, "volume"), "elsewhere/file.txt", CancellationToken.None);

        Assert.Null(mapped);
    }

    [Fact]
    public async Task TryMapVolumePath_SiblingWithRootNameAsPrefix_DoesNotFalseMatch()
    {
        // "pinned-extra" starts with "pinned" as a raw string — a prefix compare without the separator
        // guard would wrongly map it into root-1. The resolve-then-compare must not.
        var (service, baseTemp) = CreateServiceForRemap();
        var pinned = Path.Combine(baseTemp, "volume", "pinned");
        Directory.CreateDirectory(pinned);
        Directory.CreateDirectory(Path.Combine(baseTemp, "volume", "pinned-extra"));
        service.SeedRootsForTests(("root-1", "Pinned", pinned, true, true, true, true, false));

        var mapped = await service.TryMapVolumePathToConfiguredRootAsync(
            Path.Combine(baseTemp, "volume"), "pinned-extra/file.txt", CancellationToken.None);

        Assert.Null(mapped);
    }

    [Fact]
    public async Task TryMapVolumePath_DotDotIntoPinnedRoot_ResolvesBeforeComparing()
    {
        // The relative path detours through a sibling and back via '..'; resolve-first means the
        // RESOLVED location (inside the pinned root) is what gets compared, not the raw string.
        var (service, baseTemp) = CreateServiceForRemap();
        var pinned = Path.Combine(baseTemp, "volume", "pinned");
        Directory.CreateDirectory(pinned);
        Directory.CreateDirectory(Path.Combine(baseTemp, "volume", "other"));
        service.SeedRootsForTests(("root-1", "Pinned", pinned, true, true, true, true, false));

        var mapped = await service.TryMapVolumePathToConfiguredRootAsync(
            Path.Combine(baseTemp, "volume"), "other/../pinned/doc.txt", CancellationToken.None);

        Assert.NotNull(mapped);
        Assert.Equal("root-1", mapped.Value.RootId);
        Assert.Equal("doc.txt", mapped.Value.RelativePath);
    }

    [Fact]
    public async Task TryMapVolumePath_NestedRoots_DeepestRootWins()
    {
        // outer/ and outer/inner/ are both pinned; a path under inner must map to inner so the most
        // specific permission set applies.
        var (service, baseTemp) = CreateServiceForRemap();
        var outer = Path.Combine(baseTemp, "volume", "outer");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        service.SeedRootsForTests(
            ("root-outer", "Outer", outer, true, true, true, true, false),
            ("root-inner", "Inner", inner, false, false, false, false, false));

        var mapped = await service.TryMapVolumePathToConfiguredRootAsync(
            Path.Combine(baseTemp, "volume"), "outer/inner/file.txt", CancellationToken.None);

        Assert.NotNull(mapped);
        Assert.Equal("root-inner", mapped.Value.RootId);
        Assert.Equal("file.txt", mapped.Value.RelativePath);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
