using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// Path-escape, permission, size-cap and data-safety contracts of <see cref="FileTransferService"/>
/// that the conflict and promotion suites do not exercise (RemEx-uk38f.4).
/// </summary>
public sealed class FileTransferServicePathSafetyTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "remex-pathsafety-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly string _lookalike;

    public FileTransferServicePathSafetyTests()
    {
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        // Shares the root's name as a prefix: a naive StartsWith containment check lets it through.
        _lookalike = Path.Combine(_base, "root-evil");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(_lookalike);
    }

    private FileTransferService Create(
        bool writable = true, bool rename = true, bool move = true, bool delete = true, bool removable = false)
    {
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, Path.Combine(_base, "roots.json"));
        service.SeedRootsForTests(("r", "Root", _root, writable, rename, move, delete, removable));
        return service;
    }

    private string Put(string dir, string name, string content = "data")
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public static TheoryData<string> EscapingPaths => new()
    {
        "../outside/x.txt",
        "..\\outside\\x.txt",
        "sub/../../outside/x.txt",
        "../root-evil/x.txt",
        "a/b/../../../outside/x.txt",
    };

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task OpenForWrite_PathEscapingRoot_IsRejectedAndNothingIsCreatedOutside(string relative)
    {
        var service = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.OpenForWriteAsync("r", relative, 1, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_outside));
        Assert.Empty(Directory.GetFiles(_lookalike));
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task ReadSideOperations_PathEscapingRoot_AreRejected(string relative)
    {
        var service = Create();
        Put(_outside, "x.txt", "secret");
        Put(_lookalike, "x.txt", "secret");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenForReadAsync("r", relative, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ComputeSha256Async("r", relative, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadRangeAsync("r", relative, 0, 10, false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BrowseAsync("r", relative, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task DeleteAndRename_PathEscapingRoot_LeaveOutsideFilesUntouched(string relative)
    {
        var service = Create();
        var victim = Put(_outside, "x.txt", "keep");
        var lookalikeVictim = Put(_lookalike, "x.txt", "keep");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync("r", relative, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameAsync("r", relative, "renamed.txt", CancellationToken.None));

        Assert.Equal("keep", File.ReadAllText(victim));
        Assert.Equal("keep", File.ReadAllText(lookalikeVictim));
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task CopyAndMove_DestinationEscapingRoot_IsRejectedAndSourceSurvives(string escape)
    {
        var service = Create();
        var source = Put(_root, "in.txt", "payload");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CopyAsync("r", "in.txt", escape, false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveAsync("r", "in.txt", escape, false, CancellationToken.None));

        Assert.Equal("payload", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(_outside));
        Assert.Empty(Directory.GetFiles(_lookalike));
    }

    [Fact]
    public async Task CopyAndMove_SourceEscapingRoot_IsRejected()
    {
        var service = Create();
        var victim = Put(_outside, "x.txt", "secret");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CopyAsync("r", "../outside/x.txt", "stolen.txt", false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveAsync("r", "../outside/x.txt", "stolen.txt", false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "stolen.txt")));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task CreateDirectory_PathEscapingRoot_IsRejected()
    {
        var service = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateDirectoryAsync("r", "../outside/newdir", CancellationToken.None));

        Assert.Empty(Directory.GetDirectories(_outside));
    }

    [Fact]
    public async Task OpenForWrite_AbsoluteDestination_NeverWritesAtTheAbsoluteLocation()
    {
        var service = Create();
        var absolute = Path.Combine(_outside, "abs.txt");

        // Windows rejects a rooted path; Linux strips the leading slash and treats it as relative to the root.
        try
        {
            await using var s = await service.OpenForWriteAsync("r", absolute, 1, CancellationToken.None);
        }
        catch (UnauthorizedAccessException)
        {
        }

        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task Operations_UnknownRoot_AreRejected()
    {
        var service = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenForReadAsync("nope", "a.txt", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenForWriteAsync("nope", "a.txt", 1, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync("nope", "a.txt", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateDirectoryAsync("nope", "d", CancellationToken.None));
    }

    [Fact]
    public async Task OpenForWrite_ReadOnlyRoot_IsRejectedWithoutCreatingParentDirectories()
    {
        var service = Create(writable: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.OpenForWriteAsync("r", "deep/er/file.bin", 1, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_root, "deep")));
    }

    [Fact]
    public async Task OpenForWrite_OverUploadCap_IsRejectedBeforeAnythingIsCreated()
    {
        var service = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.OpenForWriteAsync("r", "huge/file.bin", 5_000_000_001L, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_root, "huge")));
    }

    [Fact]
    public async Task OpenForWrite_ExactlyAtUploadCap_IsAccepted()
    {
        var service = Create();

        await using var stream = await service.OpenForWriteAsync("r", "cap.bin", 5_000_000_000L, CancellationToken.None);

        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task OpenForWrite_MissingParents_AreCreatedInsideTheRoot()
    {
        var service = Create();

        await using (var stream = await service.OpenForWriteAsync("r", "a/b/c.txt", 3, CancellationToken.None))
            await stream.WriteAsync(new byte[] { 1, 2, 3 });

        Assert.Equal(3, new FileInfo(Path.Combine(_root, "a", "b", "c.txt")).Length);
    }

    [Fact]
    public async Task PromoteStagedFile_ReadOnlyRoot_IsRejectedAndStagedFileIsKept()
    {
        var service = Create(writable: false);
        var staged = Put(_base, "staged.tmp", "upload");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.PromoteStagedFileAsync("r", "up.txt", 6, staged, CancellationToken.None));

        Assert.True(File.Exists(staged));
        Assert.False(File.Exists(Path.Combine(_root, "up.txt")));
    }

    [Fact]
    public async Task PromoteStagedFile_EscapingPath_IsRejectedAndStagedFileIsKept()
    {
        var service = Create();
        var staged = Put(_base, "staged.tmp", "upload");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.PromoteStagedFileAsync("r", "../outside/up.txt", 6, staged, CancellationToken.None));

        Assert.True(File.Exists(staged));
        Assert.Empty(Directory.GetFiles(_outside));
    }

    [Fact]
    public async Task PromoteStagedFile_OverUploadCap_IsRejectedAndExistingDestinationIsUntouched()
    {
        var service = Create();
        var staged = Put(_base, "staged.tmp", "new");
        var existing = Put(_root, "up.txt", "old");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.PromoteStagedFileAsync("r", "up.txt", 5_000_000_001L, staged, CancellationToken.None));

        Assert.Equal("old", File.ReadAllText(existing));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public async Task Delete_RootWithoutDeletePermission_KeepsTheFile()
    {
        var service = Create(delete: false);
        var file = Put(_root, "keep.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync("r", "keep.txt", CancellationToken.None));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Delete_MissingTarget_ThrowsFileNotFound()
    {
        var service = Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.DeleteAsync("r", "ghost.txt", CancellationToken.None));
    }

    [Theory(Skip = "RemEx-uk38f.4: DeleteAsync resolves ''/'/'/'.' to the root and recursively deletes the whole shared root")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(".")]
    public async Task Delete_TheRootItself_IsRefusedAndTheSharedFolderSurvives(string relative)
    {
        var service = Create();
        var inside = Put(_root, "keep.txt");

        try
        {
            await service.DeleteAsync("r", relative, CancellationToken.None);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or InvalidOperationException)
        {
        }

        Assert.True(File.Exists(inside), "Deleting '" + relative + "' wiped the entire shared root.");
    }

    [Theory]
    [InlineData("sub/name.txt")]
    [InlineData("..\\name.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_InvalidNewName_IsRejectedAndSourceIsUnchanged(string newName)
    {
        var service = Create();
        var file = Put(_root, "a.txt", "x");

        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync("r", "a.txt", newName, CancellationToken.None));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Rename_RootWithoutRenamePermission_KeepsTheName()
    {
        var service = Create(rename: false);
        var file = Put(_root, "a.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameAsync("r", "a.txt", "b.txt", CancellationToken.None));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Rename_ToExistingName_DoesNotOverwriteEitherFile()
    {
        var service = Create();
        var a = Put(_root, "a.txt", "A");
        var b = Put(_root, "b.txt", "B");

        await Assert.ThrowsAnyAsync<IOException>(() => service.RenameAsync("r", "a.txt", "b.txt", CancellationToken.None));

        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact(Skip = "RemEx-uk38f.4: RenameAsync on the root path moves the shared root folder to a sibling name (parent escape)")]
    public async Task Rename_TheRootItself_IsRefusedAndTheSharedFolderStaysPut()
    {
        var service = Create();
        Put(_root, "keep.txt");

        try
        {
            await service.RenameAsync("r", "", "moved", CancellationToken.None);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or InvalidOperationException)
        {
        }

        Assert.True(File.Exists(Path.Combine(_root, "keep.txt")), "Renaming '' moved the shared root away.");
    }

    [Fact]
    public async Task Copy_ReadOnlyRoot_IsRejectedAndNothingIsWritten()
    {
        var service = Create(writable: false);
        Put(_root, "a.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CopyAsync("r", "a.txt", "b.txt", false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task Move_RootWithoutMovePermission_KeepsTheSource()
    {
        var service = Create(move: false);
        var a = Put(_root, "a.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveAsync("r", "a.txt", "b.txt", false, CancellationToken.None));

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task CopyAndMove_ToTheSamePath_AreRefusedWithoutTouchingTheFile()
    {
        var service = Create();
        var a = Put(_root, "a.txt", "keep");

        await Assert.ThrowsAsync<IOException>(() => service.CopyAsync("r", "a.txt", "./a.txt", true, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => service.MoveAsync("r", "a.txt", "sub/../a.txt", true, CancellationToken.None));

        Assert.Equal("keep", File.ReadAllText(a));
    }

    [Fact]
    public async Task CopyAndMove_FolderIntoItself_IsRefused()
    {
        var service = Create();
        Put(_root, "dir/f.txt");

        await Assert.ThrowsAsync<IOException>(() => service.CopyAsync("r", "dir", "dir/inner", false, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => service.MoveAsync("r", "dir", "dir/inner", false, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_root, "dir", "f.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "dir", "inner")));
    }

    [Fact]
    public async Task Copy_MissingSource_ThrowsFileNotFoundAndCreatesNothing()
    {
        var service = Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CopyAsync("r", "ghost.txt", "b.txt", false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task Move_ExistingDestinationWithoutOverwrite_KeepsBothFiles()
    {
        var service = Create();
        var a = Put(_root, "a.txt", "A");
        var b = Put(_root, "b.txt", "B");

        await Assert.ThrowsAsync<FileConflictException>(() => service.MoveAsync("r", "a.txt", "b.txt", false, CancellationToken.None));

        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public async Task Move_FolderOverFile_IsRefusedEvenWithOverwrite_AndTheFileSurvives()
    {
        var service = Create();
        Put(_root, "dir/f.txt");
        var target = Put(_root, "target", "precious");

        await Assert.ThrowsAsync<FileConflictException>(() => service.MoveAsync("r", "dir", "target", true, CancellationToken.None));

        Assert.Equal("precious", File.ReadAllText(target));
        Assert.True(File.Exists(Path.Combine(_root, "dir", "f.txt")));
    }

    [Fact]
    public void CopyDirectoryRecursive_PreCancelled_ThrowsAfterCreatingOnlyTheEmptyDestination()
    {
        var src = Path.Combine(_root, "src");
        Put(src, "1.txt");
        Put(src, "2.txt");
        var dest = Path.Combine(_root, "dest");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FileTransferService.CopyDirectoryRecursive(src, dest, false, cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(dest));
        Assert.Equal(2, Directory.GetFiles(src).Length);
    }

    [Fact]
    public async Task CreateDirectory_ReadOnlyRoot_IsRejected()
    {
        var service = Create(writable: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateDirectoryAsync("r", "d", CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_root, "d")));
    }

    [Theory]
    [InlineData(-1L, 10)]
    [InlineData(0L, 0)]
    [InlineData(0L, -5)]
    [InlineData(0L, 1024 * 1024 + 1)]
    public async Task ReadRange_OutOfBoundsArguments_AreRejected(long offset, int length)
    {
        var service = Create();
        Put(_root, "f.txt", "hello");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ReadRangeAsync("r", "f.txt", offset, length, false, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRange_MissingFile_ThrowsFileNotFound()
    {
        var service = Create();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ReadRangeAsync("r", "ghost.log", 0, 10, false, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRange_OffsetPastEnd_ReturnsEmptyWithTheRealFileSize()
    {
        var service = Create();
        Put(_root, "f.txt", "hello");

        var result = await service.ReadRangeAsync("r", "f.txt", 500, 10, false, CancellationToken.None);

        Assert.Empty(result.Data);
        Assert.Equal(5, result.FileSize);
    }

    [Fact]
    public async Task ReadRange_FromEndLongerThanFile_ReturnsWholeFileFromZero()
    {
        var service = Create();
        Put(_root, "f.txt", "hello");

        var result = await service.ReadRangeAsync("r", "f.txt", 0, 100, true, CancellationToken.None);

        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(result.Data));
        Assert.Equal(0, result.Offset);
    }

    [Fact]
    public async Task ReadRange_FromEnd_ReturnsTheTail()
    {
        var service = Create();
        Put(_root, "f.txt", "0123456789");

        var result = await service.ReadRangeAsync("r", "f.txt", 0, 4, true, CancellationToken.None);

        Assert.Equal("6789", System.Text.Encoding.UTF8.GetString(result.Data));
        Assert.Equal(6, result.Offset);
    }

    [Fact]
    public async Task OpenForRead_MissingFile_ThrowsFileNotFound()
    {
        var service = Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.OpenForReadAsync("r", "ghost.txt", CancellationToken.None));
    }

    [Fact]
    public async Task OpenForRead_DirectoryPath_IsNotTreatedAsAFile()
    {
        var service = Create();
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.OpenForReadAsync("r", "dir", CancellationToken.None));
    }

    [Fact]
    public async Task Browse_MissingDirectory_ThrowsDirectoryNotFound()
    {
        var service = Create();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.BrowseAsync("r", "nope", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveRoot_NonRemovableRoot_IsRefusedAndStillListed()
    {
        var service = Create(removable: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveRootAsync("r", CancellationToken.None));

        Assert.Contains(await service.ListRootsAsync(CancellationToken.None), r => r.RootId == "r");
    }

    [Fact]
    public async Task RemoveRoot_UnknownRoot_ThrowsInvalidOperation()
    {
        var service = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveRootAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveRoot_RemovableRoot_UnsharesItWithoutDeletingFiles()
    {
        var service = Create(removable: true);
        var file = Put(_root, "keep.txt");

        var remaining = await service.RemoveRootAsync("r", CancellationToken.None);

        Assert.DoesNotContain(remaining, r => r.RootId == "r");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AddRootFromPath_AlreadySharedFolder_IsRejected()
    {
        var service = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddRootFromPathAsync("r", "", CancellationToken.None));
    }

    [Fact]
    public async Task AddRootFromPath_MissingFolder_ThrowsDirectoryNotFound()
    {
        var service = Create();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.AddRootFromPathAsync("r", "nope", CancellationToken.None));
    }

    [Fact]
    public async Task AddRootFromPath_EscapingFolder_IsRejected()
    {
        var service = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddRootFromPathAsync("r", "../outside", CancellationToken.None));
    }

    [Fact]
    public async Task ListRoots_CorruptConfigFile_RecoversToUsableDefaultsInsteadOfThrowing()
    {
        var config = Path.Combine(_base, "roots.json");
        File.WriteAllText(config, "{ this is not json");
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, config);

        var roots = await service.ListRootsAsync(CancellationToken.None);

        Assert.NotEmpty(roots);
        Assert.DoesNotContain(roots, r => r.RootId == "r");
    }

    [Fact]
    public async Task ListRoots_RootWhoseFolderWasDeleted_IsDroppedFromTheShare()
    {
        var service = Create();
        Directory.Delete(_root, true);

        var roots = await service.ListRootsAsync(CancellationToken.None);

        Assert.DoesNotContain(roots, r => r.RootId == "r");
    }

    [Fact(Skip = "RemEx-uk38f.4: service accepts 'file.txt:stream' names on NTFS (no ADS rejection in ResolveWithinRoot or OpenForWriteAsync)")]
    public async Task OpenForWrite_AlternateDataStreamName_DoesNotLeakOutsideTheNamedFile()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var service = Create();

        // The request is either refused or lands inside the root; it must never create a hidden stream on a file the client could not otherwise name.
        try
        {
            await using var s = await service.OpenForWriteAsync("r", "visible.txt:hidden", 1, CancellationToken.None);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or NotSupportedException)
        {
            return;
        }

        Assert.Fail("Alternate data stream write was accepted.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }
}
