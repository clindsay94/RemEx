using Remex.Core.Services;

namespace Remex.Core.Tests;

/// <summary>
/// Pins <see cref="RemexDataPaths.WriteAllTextAtomic"/>, the staging-and-rename every host-state
/// store now writes through.
///
/// <para>
/// WHY (RemEx-kow1). The four writers of host state — the pairing registry, the paired-client name
/// store, the file-transfer trust store and the transfer queue — each staged through the fixed
/// sibling path <c>&lt;store&gt;.tmp</c>. That name is a property of the store, not of the writer,
/// so two writers of one store collide on it: the second <c>WriteAllText</c> either fails outright
/// (the first still holds the handle) or truncates a file the first is about to rename into place.
/// Two writers is the normal case rather than the exotic one — a test run shares the machine-wide
/// directory with an installed agent, and every host inside one test assembly shares a single
/// redirected directory. The stores at stake are credentials (<c>paired_clients.json</c>) and
/// standing filesystem authorisation (<c>file_transfer_trust.json</c>).
/// </para>
/// </summary>
public sealed class RemexDataPathsAtomicWriteTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "remex-atomic-write-tests", Guid.NewGuid().ToString("N"));

    public RemexDataPathsAtomicWriteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string StorePath => Path.Combine(_directory, "store.json");

    private string[] StagingFiles() => Directory
        .GetFiles(_directory, "*.tmp")
        .ToArray();

    [Fact]
    public void WriteAllTextAtomic_WritesTheContents()
    {
        RemexDataPaths.WriteAllTextAtomic(StorePath, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(StorePath));
    }

    [Fact]
    public void WriteAllTextAtomic_ReplacesAnExistingFile()
    {
        File.WriteAllText(StorePath, "stale");

        RemexDataPaths.WriteAllTextAtomic(StorePath, "fresh");

        Assert.Equal("fresh", File.ReadAllText(StorePath));
    }

    [Fact]
    public void WriteAllTextAtomic_LeavesNoStagingFileBehind()
    {
        RemexDataPaths.WriteAllTextAtomic(StorePath, "{}");

        Assert.Empty(StagingFiles());
    }

    /// <summary>
    /// The regression itself, made deterministic: something else already owns
    /// <c>&lt;store&gt;.tmp</c>. A directory stands in for the concurrent writer because a directory
    /// cannot be opened for writing on any platform, so the old fixed-path code fails here every
    /// time instead of once in a hundred runs.
    /// </summary>
    [Fact]
    public void WriteAllTextAtomic_DoesNotStageThroughTheFixedSiblingTempPath()
    {
        var fixedTempPath = StorePath + ".tmp";
        Directory.CreateDirectory(fixedTempPath);

        RemexDataPaths.WriteAllTextAtomic(StorePath, "{\"written\":true}");

        Assert.Equal("{\"written\":true}", File.ReadAllText(StorePath));
        Assert.True(Directory.Exists(fixedTempPath), "The unrelated path was consumed by the write.");
    }

    /// <summary>
    /// A unique staging name is only an improvement if a failed write cleans up after itself —
    /// otherwise every failure leaves a differently-named file that nothing will ever reuse or
    /// collect, and the store directory fills with per-attempt debris.
    /// </summary>
    [Fact]
    public void WriteAllTextAtomic_RemovesTheStagingFile_WhenTheRenameFails()
    {
        // A directory at the target path: staging succeeds, the rename over it cannot.
        Directory.CreateDirectory(StorePath);

        // Not Assert.Throws<T>: the rename over a directory surfaces as IOException on some
        // platforms and UnauthorizedAccessException on others, and which one it is is not the point.
        var thrown = Record.Exception(() => RemexDataPaths.WriteAllTextAtomic(StorePath, "{}"));

        Assert.NotNull(thrown);
        Assert.Empty(StagingFiles());
    }

    /// <summary>
    /// Concurrent writers of one store: whatever ends up on disk is exactly one writer's complete
    /// contents, never a blend of two and never a truncation.
    /// </summary>
    /// <remarks>
    /// AND NOT "concurrent writers all succeed", which is the claim this test was first written to
    /// make and which is false — measured, not reasoned about. Two renames onto one target still
    /// collide, and the loser gets <c>UnauthorizedAccessException</c> out of <c>File.Move</c>. That
    /// is unchanged by this work and is caught by three of the four callers already; what changed is
    /// that a loser can no longer take the winner's staging file down with it. Integrity is the
    /// property being claimed here, so integrity is what is asserted; the collisions are counted
    /// rather than ignored so the test cannot quietly become vacuous if they ever stop happening.
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAtomic_ConcurrentWritersNeverPublishABlendedStore()
    {
        const int writesPerWriter = 20;
        var payloads = Enumerable.Range(0, 8)
            .Select(i => new string((char)('a' + i), 4096))
            .ToArray();
        var collisions = 0;

        await Task.WhenAll(payloads.Select(payload => Task.Run(() =>
        {
            for (var i = 0; i < writesPerWriter; i++)
            {
                try
                {
                    RemexDataPaths.WriteAllTextAtomic(StorePath, payload);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref collisions);
                }
            }
        })));

        Assert.Contains(File.ReadAllText(StorePath), payloads);
        Assert.Empty(StagingFiles());
        Assert.True(
            collisions < payloads.Length * writesPerWriter,
            $"Every write lost the rename race ({collisions}); nothing was actually exercised.");
    }
}
