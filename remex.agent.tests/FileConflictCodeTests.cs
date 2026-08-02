using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that a real collision produces a machine-readable code, not just prose (RemEx-6vd8).
/// </summary>
/// <remarks>
/// **THE BEAD'S CENTRAL WARNING WAS ABOUT SEQUENCING.** An <c>errorCode</c> field was written during
/// RemEx-12cj and reverted before commit, because it would have shipped unset — the same shape as
/// RemEx-mneb, where a message type is declared and round-trip tested while no code path ever sets
/// it. So these tests run against the REAL <c>FileTransferService</c> on a REAL temp directory: they
/// fail if the field exists but nothing populates it, which a serialization round-trip cannot.
/// </remarks>
public sealed class FileConflictCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remex-conflict-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly FileTransferService _service;

    public FileConflictCodeTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "roots.json");
        _service = new FileTransferService(NullLogger<FileTransferService>.Instance, _configPath);
        _service.SeedRootsForTests(("r1", "Shared", _root, true, true, true, true, false));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relativeName, string content = "x")
    {
        var path = Path.Combine(_root, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task CopyingOntoAnExistingFileCarriesTheCollisionCode()
    {
        Write("a.txt", "source");
        Write("b.txt", "victim");

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCodes.DestinationExists, ex.ErrorCode);
        Assert.Equal("b.txt", ex.ConflictingName);

        // The prose is KEPT, not replaced - it is still what a person reads when the client is older
        // than this change, or when the UI has nothing better to show.
        Assert.Contains("b.txt", ex.Message);
    }

    [Fact]
    public async Task ItIsStillAnIOException_SoExistingCatchBlocksAreUnaffected()
    {
        Write("a.txt");
        Write("b.txt");

        // The code is additional information for the one handler that looks for it, never a new
        // failure mode for the many that do not.
        await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        var caught = await Record.ExceptionAsync(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        Assert.IsAssignableFrom<IOException>(caught);
    }

    [Fact]
    public async Task AFolderStandingWhereAFileShouldGoIsADIFFERENTCode()
    {
        // "Replace" here would mean deleting a whole directory tree to make room for one file.
        // Nobody intends that from a copy and nothing undoes it, so a client must be able to tell
        // this apart and withhold the button.
        Write("a.txt");
        Directory.CreateDirectory(Path.Combine(_root, "b.txt"));

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCodes.DestinationIsDifferentKind, ex.ErrorCode);
    }

    [Fact]
    public async Task KeepBothWritesASiblingAndReportsTheNameItUsed()
    {
        Write("a.txt", "source");
        Write("b.txt", "keep me");

        var resolved = await _service.CopyAsync(
            "r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.KeepBoth);

        Assert.Equal("b (2).txt", resolved);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b (2).txt")));
    }

    [Fact]
    public async Task ReplaceOverwritesWithoutNeedingTheLegacyFlag()
    {
        Write("a.txt", "source");
        Write("b.txt", "victim");

        var resolved = await _service.CopyAsync(
            "r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.Replace);

        Assert.Null(resolved);
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task MovingHonoursKeepBothToo_AndLeavesTheExistingFileAlone()
    {
        // Move is the destructive one: a wrong answer here loses the source AND the destination.
        Write("a.txt", "source");
        Write("b.txt", "keep me");

        var resolved = await _service.MoveAsync(
            "r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.KeepBoth);

        Assert.Equal("b (2).txt", resolved);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b (2).txt")));
    }

    [Fact]
    public async Task ACopyThatDoesNotCollideReportsNoResolvedName()
    {
        // Null means "what you asked for is what you got", and the UI shows nothing extra.
        Write("a.txt", "source");

        var resolved = await _service.CopyAsync(
            "r1", "a.txt", "c.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.KeepBoth);

        Assert.Null(resolved);
        Assert.True(File.Exists(Path.Combine(_root, "c.txt")));
    }

    [Fact]
    public async Task ARequestWithNoResolutionStillFails_WhichIsTheCompatibilityGuarantee()
    {
        // Every client that predates this field must behave exactly as before. If passing null
        // started overwriting or renaming, this change would silently alter what existing phones do.
        Write("a.txt", "source");
        Write("b.txt", "victim");

        await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None, null));

        Assert.Equal("victim", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }
}
