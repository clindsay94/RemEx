using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins <see cref="LastSeedSidecar"/> (RemEx-alwfa.1, decision (b)): the round trip through
/// <c>last-seed.json</c>, and that the write is atomic (staged sibling, no debris) the same way
/// every other host-state store in this repo is. <see cref="RemexDataPaths.PerUserDirectory"/> is
/// already redirected assembly-wide by <c>build/TestHostStateRedirect.cs</c> before any test runs
/// (see <c>HostStateRedirectionTests</c>), so <see cref="LastSeedSidecar.FilePath"/> here never
/// touches the developer's real per-user directory. Each test deletes the sidecar (and any stray
/// staging sibling) before and after it runs so tests never see each other's file.
/// </summary>
public sealed class LastSeedSidecarTests : IDisposable
{
    public LastSeedSidecarTests() => CleanUp();

    public void Dispose() => CleanUp();

    private static void CleanUp()
    {
        // P1-25: the dedupe/debounce cache is static (process-wide, matching the sidecar's
        // one-file-per-machine shape) and must be reset alongside the file, or a test's write can be
        // silently skipped as "unchanged" against a value an EARLIER test left in the in-memory cache.
        LastSeedSidecar.ResetCacheForTests();

        var path = LastSeedSidecar.FilePath;
        if (File.Exists(path)) File.Delete(path);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        foreach (var stray in Directory.EnumerateFiles(directory, $".{Path.GetFileName(path)}.*.tmp"))
            File.Delete(stray);
    }

    private static CustomizationSettings SampleSettings => new()
    {
        AccentColor = "#6C4CFF",
        SchemeVariant = SchemeVariants.Vibrant,
        ThemeMode = ThemeModes.Dark,
        ThemeContrast = 0.25,
    };

    [Fact]
    public async Task WriteAsync_ThenTryRead_RoundTripsSeedVariantModeAndContrast()
    {
        await LastSeedSidecar.WriteAsync(SampleSettings);

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Seed.Should().Be("#6C4CFF");
        seed.Variant.Should().Be(SchemeVariants.Vibrant);
        seed.Mode.Should().Be(ThemeModes.Dark);
        seed.Contrast.Should().Be(0.25);
    }

    [Fact]
    public async Task WriteAsync_IsAtomic_NoStagingFileSurvives()
    {
        await LastSeedSidecar.WriteAsync(SampleSettings);

        File.Exists(LastSeedSidecar.FilePath).Should().BeTrue();
        var directory = Path.GetDirectoryName(LastSeedSidecar.FilePath)!;
        Directory.EnumerateFiles(directory, $".{Path.GetFileName(LastSeedSidecar.FilePath)}.*.tmp")
            .Should().BeEmpty("a completed write must leave no staging sibling behind");
    }

    [Fact]
    public void TryRead_WithNoSidecarFile_ReturnsFalse()
    {
        LastSeedSidecar.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_WithCorruptJson_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastSeedSidecar.FilePath)!);
        File.WriteAllText(LastSeedSidecar.FilePath, "{ this is not valid json");

        LastSeedSidecar.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_ThenWriteAsyncAgain_ReplacesTheContents()
    {
        await LastSeedSidecar.WriteAsync(SampleSettings);
        await LastSeedSidecar.WriteAsync(SampleSettings with { AccentColor = "#FF0000" });

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Seed.Should().Be("#FF0000");
    }

    [Fact]
    public async Task WriteAsync_WithIdenticalSeedVariantModeAndContrast_SkipsTheSecondWrite()
    {
        // P1-25: a slider drag on a field the sidecar does NOT store (e.g. background opacity) still
        // calls ApplyAndSave -> WriteAsync every tick with an otherwise-unchanged Seed/Variant/Mode/
        // Contrast. Proven here by writing, deleting the file out from under the cache, then writing
        // an EQUAL document again - if the dedupe is real, the file must not come back.
        await LastSeedSidecar.WriteAsync(SampleSettings);
        File.Delete(LastSeedSidecar.FilePath);

        await LastSeedSidecar.WriteAsync(SampleSettings with { }); // a distinct object, equal value

        File.Exists(LastSeedSidecar.FilePath).Should()
            .BeFalse("an unchanged Seed/Variant/Mode/Contrast must not trigger a second write");
    }

    [Fact]
    public async Task WriteAsync_RapidCallsWithDifferentContrast_CoalesceToOneWriteOfTheLatestValue()
    {
        // P1-25: a contrast slider drag calls WriteAsync on every tick with a genuinely changing
        // Contrast value, so the dedupe above cannot help - this is what the debounce is for. Firing
        // several calls back-to-back (well under the debounce window) must land on the LAST value,
        // not an intermediate one, and must not write once per call.
        var tasks = new List<Task>();
        for (var i = 1; i <= 5; i++)
        {
            tasks.Add(LastSeedSidecar.WriteAsync(SampleSettings with { ThemeContrast = i * 0.1 }));
        }

        await Task.WhenAll(tasks);

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Contrast.Should().Be(0.5, "the debounce must apply the LAST value passed during the burst, not an earlier tick's");
    }

    [Fact]
    public async Task WriteAsync_RevertToTheOnDiskValueWhileADebounceIsPending_StillWritesTheRevert()
    {
        // Round-1 review HIGH: A -> B (starts a debounce) -> A again, all within the debounce window.
        // The second A must NOT be silently dropped by the "equals _lastWritten" fast path just
        // because A already happens to be what's on disk - it must still become the pending value,
        // or the file ends up on B while the live theme is back on A.
        await LastSeedSidecar.WriteAsync(SampleSettings); // A, written and settled
        var changeToB = LastSeedSidecar.WriteAsync(SampleSettings with { AccentColor = "#FF0000" }); // B, starts a debounce
        var revertToA = LastSeedSidecar.WriteAsync(SampleSettings); // back to A, before the debounce fires

        await Task.WhenAll(changeToB, revertToA);

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Seed.Should().Be("#6C4CFF", "the revert to A must win, not the superseded B");
    }

    [Fact]
    public async Task WriteAsync_AValueArrivingWhileTheDiskWriteIsInFlight_IsNotDropped()
    {
        // Round-2 review HIGH: proves the loop-back branch in DebounceThenWriteAsync deterministically
        // - no timing dependency - by holding the underlying write open with a gate until a SECOND,
        // different value has already been registered as pending, then releasing it. Round 1's fix
        // must notice the mismatch after the first write completes and loop for a second write of the
        // newer value; the pre-fix single-write code would exit after writing only A.
        var firstWriteStarted = new TaskCompletionSource();
        var releaseFirstWrite = new TaskCompletionSource();
        var writeCount = 0;

        LastSeedSidecar.WriteFileForTests = async (path, contents) =>
        {
            var isFirst = Interlocked.Increment(ref writeCount) == 1;
            if (isFirst)
            {
                firstWriteStarted.TrySetResult();
                await releaseFirstWrite.Task; // held open until the test says go
            }
            await File.WriteAllTextAsync(path, contents);
        };

        try
        {
            var changeToA = LastSeedSidecar.WriteAsync(SampleSettings); // A - will be the held write

            await firstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // in flight, blocked

            var changeToB = LastSeedSidecar.WriteAsync(SampleSettings with { AccentColor = "#00FF00" }); // B - arrives mid-write

            releaseFirstWrite.TrySetResult(); // let A's write complete
            await Task.WhenAll(changeToA, changeToB);

            LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
            seed.Seed.Should().Be(
                "#00FF00",
                "a value that arrived while the previous write was in flight must still be written, not silently dropped");
            writeCount.Should().Be(2, "the loop must perform a SECOND write for the value that arrived mid-flight");
        }
        finally
        {
            LastSeedSidecar.WriteFileForTests = RemexDataPaths.WriteAllTextAtomicAsync;
        }
    }
}
