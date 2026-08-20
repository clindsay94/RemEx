using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Models;
using Remex.Agent.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// Paging and correctness tests for the folder-transfer manifest (RemEx-q3twg).
///
/// The property that matters is that paging is TRANSPARENT: walking a subtree one page at a time must
/// produce exactly the sequence a single unbounded walk would, with nothing repeated and nothing
/// skipped across a boundary. That is entirely carried by the cursor, and a cursor bug looks like a
/// successful folder transfer that is quietly missing files — so it is asserted here rather than left
/// to be noticed downstream.
/// </summary>
public sealed class FileTransferManifestTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private (FileTransferService Service, string RootDir) CreateService()
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-manifest-" + Guid.NewGuid().ToString("N"));
        var rootDir = Path.Combine(baseTemp, "root");
        Directory.CreateDirectory(rootDir);
        _tempDirs.Add(baseTemp);

        var configPath = Path.Combine(baseTemp, "roots.json");
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, configPath);
        service.SeedRootsForTests(("root-1", "Test Root", rootDir, true, true, true, true, false));

        return (service, rootDir);
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    /// <summary>Pages the whole subtree with the given page size and returns the concatenated entries.</summary>
    private static async Task<List<FileManifestEntry>> PageAllAsync(
        FileTransferService service, string relativePath, int pageSize)
    {
        var all = new List<FileManifestEntry>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await service.EnumerateSubtreeAsync("root-1", relativePath, cursor, pageSize, CancellationToken.None);
            all.AddRange(page.Entries);
            cursor = page.NextCursor;

            // A cursor that never clears would loop forever; fail loudly instead of hanging the suite.
            Assert.True(++pages < 200, "The manifest did not terminate within 200 pages.");
        }
        while (!string.IsNullOrEmpty(cursor));

        return all;
    }

    private void SeedSampleTree(string rootDir)
    {
        WriteFile(Path.Combine(rootDir, "a.txt"), 10);
        WriteFile(Path.Combine(rootDir, "b", "b1.txt"), 20);
        WriteFile(Path.Combine(rootDir, "b", "b2.txt"), 30);
        WriteFile(Path.Combine(rootDir, "b", "deep", "d1.txt"), 40);
        WriteFile(Path.Combine(rootDir, "c.txt"), 50);
        Directory.CreateDirectory(Path.Combine(rootDir, "empty"));
    }

    [Fact]
    public async Task Enumerate_SinglePage_ReturnsWholeSubtreePreOrderWithDirectories()
    {
        var (service, rootDir) = CreateService();
        SeedSampleTree(rootDir);

        var page = await service.EnumerateSubtreeAsync("root-1", string.Empty, null, 100, CancellationToken.None);

        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
        Assert.Equal(
            new[] { "a.txt", "b", "b/b1.txt", "b/b2.txt", "b/deep", "b/deep/d1.txt", "c.txt", "empty" },
            page.Entries.Select(entry => entry.RelativePath));

        // An empty directory has no file to imply it, so it must arrive in its own right or it is lost.
        Assert.Contains(page.Entries, entry => entry.RelativePath == "empty" && entry.IsDirectory);
        Assert.Equal(20, page.Entries.Single(entry => entry.RelativePath == "b/b1.txt").SizeBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Enumerate_Paged_MatchesTheUnpagedWalkExactly(int pageSize)
    {
        var (service, rootDir) = CreateService();
        SeedSampleTree(rootDir);

        var whole = await service.EnumerateSubtreeAsync("root-1", string.Empty, null, 100, CancellationToken.None);
        var paged = await PageAllAsync(service, string.Empty, pageSize);

        Assert.Equal(
            whole.Entries.Select(entry => entry.RelativePath),
            paged.Select(entry => entry.RelativePath));
        Assert.Equal(paged.Select(entry => entry.RelativePath).Distinct().Count(), paged.Count);
    }

    [Fact]
    public async Task Enumerate_PageBoundaryOnADirectory_DoesNotLoseItsChildren()
    {
        // The cursor landing ON a directory is the case that breaks naively: the directory was emitted,
        // but nothing under it has been, so resuming must descend into it rather than step past it.
        var (service, rootDir) = CreateService();
        WriteFile(Path.Combine(rootDir, "dir", "inner.txt"), 5);
        WriteFile(Path.Combine(rootDir, "z.txt"), 5);

        var first = await service.EnumerateSubtreeAsync("root-1", string.Empty, null, 1, CancellationToken.None);
        Assert.Equal("dir", Assert.Single(first.Entries).RelativePath);
        Assert.NotNull(first.NextCursor);

        var rest = new List<string>();
        var cursor = first.NextCursor;
        while (!string.IsNullOrEmpty(cursor))
        {
            var page = await service.EnumerateSubtreeAsync("root-1", string.Empty, cursor, 1, CancellationToken.None);
            rest.AddRange(page.Entries.Select(entry => entry.RelativePath));
            cursor = page.NextCursor;
        }

        Assert.Equal(new[] { "dir/inner.txt", "z.txt" }, rest);
    }

    [Fact]
    public async Task Enumerate_Subtree_PathsAreRootRelativeNotSubtreeRelative()
    {
        // Entries feed straight into a download request, which addresses by rootId + ROOT-relative path.
        var (service, rootDir) = CreateService();
        WriteFile(Path.Combine(rootDir, "b", "deep", "d1.txt"), 1);

        var page = await service.EnumerateSubtreeAsync("root-1", "b", null, 100, CancellationToken.None);

        Assert.Equal(new[] { "b/deep", "b/deep/d1.txt" }, page.Entries.Select(entry => entry.RelativePath));
    }

    [Fact]
    public async Task Enumerate_FirstPage_CarriesWholeSubtreeTotals_ContinuationsDoNot()
    {
        var (service, rootDir) = CreateService();
        SeedSampleTree(rootDir);

        var first = await service.EnumerateSubtreeAsync("root-1", string.Empty, null, 2, CancellationToken.None);

        Assert.True(first.TotalsComplete);
        Assert.Equal(5, first.TotalFiles);
        Assert.Equal(3, first.TotalDirectories);
        Assert.Equal(10 + 20 + 30 + 40 + 50, first.TotalBytes);

        var second = await service.EnumerateSubtreeAsync("root-1", string.Empty, first.NextCursor, 2, CancellationToken.None);

        // Null, not zero: a client has to be able to tell "not counted here" from "counted nothing".
        Assert.Null(second.TotalFiles);
        Assert.Null(second.TotalDirectories);
        Assert.Null(second.TotalBytes);
    }

    [Fact]
    public async Task Enumerate_MalformedCursor_RestartsFromTheBeginning()
    {
        // The cursor is client-supplied text. Positioning with it grants nothing the request did not
        // already have, so garbage restarts the walk rather than failing it.
        var (service, rootDir) = CreateService();
        SeedSampleTree(rootDir);

        var page = await service.EnumerateSubtreeAsync("root-1", string.Empty, "not-a-cursor", 100, CancellationToken.None);

        Assert.Equal("a.txt", page.Entries[0].RelativePath);
        Assert.Equal(8, page.Entries.Count);
    }

    [Fact]
    public async Task Enumerate_CursorPointingOutsideTheRoot_CannotEscapeIt()
    {
        var (service, rootDir) = CreateService();
        SeedSampleTree(rootDir);

        var page = await service.EnumerateSubtreeAsync("root-1", string.Empty, "0|../../etc", 100, CancellationToken.None);

        // Every path still sits inside the root: the cursor only positions a walk, it never opens one.
        Assert.All(page.Entries, entry => Assert.DoesNotContain("..", entry.RelativePath));
    }

    [Fact]
    public async Task Enumerate_NonDirectory_Throws()
    {
        var (service, rootDir) = CreateService();
        WriteFile(Path.Combine(rootDir, "a.txt"), 1);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => service.EnumerateSubtreeAsync("root-1", "a.txt", null, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Enumerate_UnknownRoot_Throws()
    {
        var (service, _) = CreateService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.EnumerateSubtreeAsync("nope", string.Empty, null, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Enumerate_RequestedPageSize_IsClampedToTheProtocolCeiling()
    {
        var (service, rootDir) = CreateService();
        for (var i = 0; i < 20; i++)
            WriteFile(Path.Combine(rootDir, $"f{i:D3}.txt"), 1);

        var huge = await service.EnumerateSubtreeAsync(
            "root-1", string.Empty, null, FileTransferLimits.ManifestMaxEntriesPerPage * 10, CancellationToken.None);
        Assert.Equal(20, huge.Entries.Count);

        // A non-positive request means "use the default", not "send nothing".
        var defaulted = await service.EnumerateSubtreeAsync("root-1", string.Empty, null, 0, CancellationToken.None);
        Assert.Equal(20, defaulted.Entries.Count);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — a leftover temp dir must not fail the run.
            }
        }
    }
}
