using Remex.Agent.Services.ProcessMonitor;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The process scan reads each executable once instead of once per process (RemEx-qz5z3).
/// </summary>
/// <remarks>
/// <para>
/// Version and publisher come from <c>FileVersionInfo.GetVersionInfo</c>, which opens the PE file and
/// parses its version resource. That ran once per PROCESS rather than once per FILE, so a machine
/// running a dozen svchost instances did a dozen identical parses per scan and the whole set again on
/// the next one — for values that belong to the file and do not change while it does not.
/// </para>
/// <para>
/// **THE LOADER IS A COUNTING FAKE, WHICH IS THE ONLY WAY TO SEE THIS.** Nothing about the returned
/// metadata changes when the cache works — the rows are identical either way — so a test asserting on
/// output would pass just as happily before the change. What changed is how often the file is read,
/// and that is what these count.
/// </para>
/// </remarks>
public class ExecutableMetadataCacheTests
{
    private static readonly DateTime Written = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ExecutableMetadata Sample(string version = "1.0") => new(version, "Contoso");

    [Fact]
    public void TheSameExecutableIsReadOnceNoMatterHowManyProcessesRunIt()
    {
        // THE BEAD. Twelve svchost rows, one file, one parse.
        var cache = new ExecutableMetadataCache();
        var loads = 0;

        for (var i = 0; i < 12; i++)
        {
            var result = cache.GetOrAdd(@"C:\Windows\System32\svchost.exe", Written, _ =>
            {
                loads++;
                return Sample();
            });
            Assert.Equal("1.0", result.Version);
        }

        Assert.Equal(1, loads);
    }

    [Fact]
    public void DifferentExecutablesAreReadSeparately()
    {
        // ANTI-VACUITY, AND THE FLOOR FOR THE WHOLE SUITE. Without it, a cache that returned its first
        // entry for every path would satisfy the count above while reporting one program's version
        // against every row in the task manager.
        var cache = new ExecutableMetadataCache();

        var a = cache.GetOrAdd(@"C:\a.exe", Written, _ => Sample("1.0"));
        var b = cache.GetOrAdd(@"C:\b.exe", Written, _ => Sample("2.0"));

        Assert.Equal("1.0", a.Version);
        Assert.Equal("2.0", b.Version);
    }

    [Fact]
    public void AnExecutableRewrittenInPlaceIsReadAgain()
    {
        // **WHY THE KEY IS NOT JUST THE PATH.** This agent runs for weeks. Cache on the path alone and
        // an updated program reports its old version until someone restarts the service - which nobody
        // would ever think to do to correct a version string. The write time comes free: the scan
        // already stats the file for the install date.
        var cache = new ExecutableMetadataCache();

        var before = cache.GetOrAdd(@"C:\app.exe", Written, _ => Sample("1.0"));
        var after = cache.GetOrAdd(@"C:\app.exe", Written.AddMinutes(1), _ => Sample("2.0"));

        Assert.Equal("1.0", before.Version);
        Assert.Equal("2.0", after.Version);
    }

    [Fact]
    public void AFailedReadIsNotRemembered()
    {
        // **A POISONED ENTRY WOULD OUTLIVE THE PROBLEM THAT CAUSED IT.** The loader throws for cases
        // the scan deliberately tolerates - a file replaced mid-scan, a permissions refusal - and the
        // caller catches them so the process still gets a row. If the cache kept that as an answer,
        // one unlucky moment would blank that program's version for as long as it keeps running.
        var cache = new ExecutableMetadataCache();

        Assert.Throws<UnauthorizedAccessException>(
            () => cache.GetOrAdd(@"C:\denied.exe", Written, _ => throw new UnauthorizedAccessException()));

        // The invariant is about the ENTRY, so assert it directly rather than only inferring it from
        // the reload below.
        Assert.Equal(0, cache.Count);

        var loads = 0;
        var recovered = cache.GetOrAdd(@"C:\denied.exe", Written, _ =>
        {
            loads++;
            return Sample("3.0");
        });

        Assert.Equal(1, loads);
        Assert.Equal("3.0", recovered.Version);
    }

    [Fact]
    public void ExecutablesThatHaveStoppedRunningAreForgotten()
    {
        // The service lives as long as the agent, so an only-growing cache is a leak with a slow fuse -
        // every binary, and every version of every binary, launched since boot.
        var cache = new ExecutableMetadataCache();
        cache.GetOrAdd(@"C:\still-running.exe", Written, _ => Sample());
        cache.GetOrAdd(@"C:\exited.exe", Written, _ => Sample());
        Assert.Equal(2, cache.Count);

        cache.RetainOnly([@"C:\still-running.exe"]);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void EveryVersionOfADepartedExecutableIsForgotten()
    {
        // The trim matches on PATH while entries are keyed by path AND write time, so a program that
        // has been updated several times leaves several entries behind. Dropping only one of them
        // would leak the rest - slowly, invisibly, and precisely on the machines that update most.
        var cache = new ExecutableMetadataCache();
        cache.GetOrAdd(@"C:\updated.exe", Written, _ => Sample("1.0"));
        cache.GetOrAdd(@"C:\updated.exe", Written.AddDays(1), _ => Sample("2.0"));
        cache.GetOrAdd(@"C:\updated.exe", Written.AddDays(2), _ => Sample("3.0"));
        Assert.Equal(3, cache.Count);

        cache.RetainOnly([]);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TheScanKeysOnWriteTimeSoARewrittenFileIsReadAgain()
    {
        // **THE ONE LINE THE BEAD FORBIDS GETTING WRONG, AND IT NEEDS A REAL FILE TO SEE.** Every
        // other test hands the cache two DateTimes it made up, which proves the cache can tell them
        // apart but not that the SCAN passes the write time. Swap LastWriteTimeUtc for CreationTimeUtc
        // in ResolveMetadata and all of them stay green - while NTFS tunnelling preserves a creation
        // time across an in-place replace, so an updated program would report its old version for the
        // life of the agent, which is exactly what this bead exists to prevent.
        var cache = new ExecutableMetadataCache();
        var path = Path.Combine(Path.GetTempPath(), $"remex-metadata-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "v1");
        try
        {
            var loads = 0;
            ExecutableMetadata Load(string _)
            {
                loads++;
                return Sample($"{loads}.0");
            }

            var first = WindowsProcessMonitorService.ResolveMetadata(path, new FileInfo(path), cache, Load);

            // A NEW FileInfo each time, because FileSystemInfo caches its stat on first access and
            // never refreshes - reusing one would report the old timestamp and pass regardless.
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
            var second = WindowsProcessMonitorService.ResolveMetadata(path, new FileInfo(path), cache, Load);

            // And unchanged again: the second read must be caused by the new timestamp, not by the
            // cache simply missing every time.
            var third = WindowsProcessMonitorService.ResolveMetadata(path, new FileInfo(path), cache, Load);

            Assert.Equal("1.0", first.Version);
            Assert.Equal("2.0", second.Version);
            Assert.Equal("2.0", third.Version);
            Assert.Equal(2, loads);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnExecutableWithNoReadableStatIsNotCachedAtAll()
    {
        // The scan bypasses the cache when it could not stat the file, rather than inventing a key.
        // A guessed timestamp would pin an answer under a value the next scan may not reproduce -
        // worse than no cache, because it is wrong rather than merely slow.
        var cache = new ExecutableMetadataCache();
        var loads = 0;

        for (var i = 0; i < 3; i++)
        {
            WindowsProcessMonitorService.ResolveMetadata(@"C:\unstattable.exe", null, cache, _ =>
            {
                loads++;
                return Sample();
            });
        }

        Assert.Equal(3, loads);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ATrimThatKeepsEverythingRemovesNothing()
    {
        // The only case where the cache is non-empty and nothing is stale: covers the early return
        // and pins that an ordinary scan allocates no removal list. An earlier comment here claimed
        // an evict-everything bug would leave every other test passing, which is false - the test
        // above expects a count of 1 and would see 0. Overclaiming a coverage gap is its own defect:
        // it teaches the next reader to delete the test that actually carries the load.
        var cache = new ExecutableMetadataCache();
        cache.GetOrAdd(@"C:\a.exe", Written, _ => Sample());
        cache.GetOrAdd(@"C:\b.exe", Written, _ => Sample());

        cache.RetainOnly([@"C:\a.exe", @"C:\b.exe"]);

        Assert.Equal(2, cache.Count);
    }
}
