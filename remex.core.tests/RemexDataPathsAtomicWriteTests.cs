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

    /// <summary>Everything in the store directory that is not the store itself.</summary>
    /// <remarks>
    /// NOT A "*.tmp" GLOB (review). Matching the extension coupled every "no debris" assertion in
    /// this file to the very naming convention the two atomic writers duplicate — so a temp name that
    /// drifted to ".staging", or lost its extension, would leave an orphan beside the store with all
    /// of these still green. Enumerating everything is name-independent, and catches a leftover
    /// DIRECTORY too, which a file glob never would.
    /// </remarks>
    private string[] StagingFiles() => Directory
        .GetFileSystemEntries(_directory)
        .Where(entry => !string.Equals(entry, StorePath, StringComparison.Ordinal))
        .OrderBy(entry => entry, StringComparer.Ordinal)
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
    /// The async sibling stages the same way, because a sibling that is only nearly the same is how
    /// the staging rule ends up written twice and diverging (RemEx-fqzp).
    /// </summary>
    /// <remarks>
    /// It exists because two of the four stores adopting the atomic write are async: dropping the
    /// synchronous call into them would block their caller, and removing their only await leaves an
    /// async method this repo compiles as an error. These assertions are deliberately the same three
    /// the synchronous one carries — contents, no debris, and cleanup on failure — so that a change
    /// to one that is not made to the other shows up here rather than in a truncated store.
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAtomicAsync_WritesTheContents_AndLeavesNoStagingFileBehind()
    {
        await RemexDataPaths.WriteAllTextAtomicAsync(StorePath, "{\"written\":true}");

        Assert.Equal("{\"written\":true}", File.ReadAllText(StorePath));
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_ReplacesAnExistingFile()
    {
        File.WriteAllText(StorePath, "old");

        await RemexDataPaths.WriteAllTextAtomicAsync(StorePath, "new");

        Assert.Equal("new", File.ReadAllText(StorePath));
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_RemovesTheStagingFile_WhenTheRenameFails()
    {
        Directory.CreateDirectory(StorePath);

        var thrown = await Record.ExceptionAsync(
            () => RemexDataPaths.WriteAllTextAtomicAsync(StorePath, "{}"));

        Assert.NotNull(thrown);
        Assert.Empty(StagingFiles());
    }

    /// <summary>
    /// The four stores this bead named write through the helper rather than over the live file.
    /// </summary>
    /// <remarks>
    /// NAMED FILES, NOT A REPO-WIDE BAN ON File.WriteAllText (RemEx-fqzp). Plenty of writes are
    /// legitimately direct — exports the user chose a path for, diagnostics dumps — so a blanket rule
    /// would be a false-positive machine, and one of those cost an iteration two commits ago
    /// (RemEx-dnn2q). This is a regression guard on four specific stores, which is what the bead is.
    /// </remarks>
    [Theory]
    [InlineData("remex.core/Services/DashboardProfileStorageService.cs", "_filePath", "WriteAllTextAtomicAsync")]
    [InlineData("remex.desktop/Services/FileTransfer/FileTransferRootSettingsService.cs", "_configPath", "WriteAllTextAtomicAsync")]
    [InlineData("remex.agent/Services/FileTransfer/FileTransferService.cs", "_configPath", "WriteAllTextAtomic")]
    [InlineData("remex.agent/Services/Session/SessionGuardSettings.cs", "FlagPath", "WriteAllTextAtomic")]
    public void TheHostStateStoresDoNotWriteOverTheirLiveFile(string relativePath, string target, string helper)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // THE TARGET, NOT THE API (review). A blanket ban on File.WriteAllText inside a file whose
        // job is writing files would fail on the next correct write somebody adds — a resume
        // manifest, a diagnostics sidecar — which is the false-positive shape that cost an iteration
        // under RemEx-dnn2q. Naming the store's own path variable says what the bead actually says.
        Assert.DoesNotContain($"File.WriteAllText({target}", source, StringComparison.Ordinal);
        Assert.DoesNotContain($"File.WriteAllTextAsync({target}", source, StringComparison.Ordinal);

        // And the RIGHT sibling: "WriteAllTextAtomic" alone is satisfied by a doc comment, a dead
        // branch, or the sync helper standing in for the async one.
        Assert.Contains($"{helper}({target}", source, StringComparison.Ordinal);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

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
    ///
    /// It is also NOT the regression detector for the fixed staging path — measured: restoring
    /// <c>&lt;store&gt;.tmp</c> leaves this test green, because corruption there needs an interleaving
    /// it does not reliably produce. <see cref="WriteAllTextAtomic_DoesNotStageThroughTheFixedSiblingTempPath"/>
    /// and the four store-level tests in <c>HostStateAtomicWriteTests</c> are what catch it.
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

        // Collisions first: if every write lost the race there is no file to read, and a
        // FileNotFoundException out of the assertion below would bury the reason.
        Assert.True(
            collisions < payloads.Length * writesPerWriter,
            $"Every write lost the rename race ({collisions}); nothing was actually exercised.");
        Assert.Contains(File.ReadAllText(StorePath), payloads);
        Assert.Empty(StagingFiles());
    }
}
