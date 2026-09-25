using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf audit P3-56: the startup update check used to hit the GitHub API on every launch, including
/// every minimized logon start. A recent successful answer is now reused instead, re-judged against
/// the running build so an install that has since been updated never shows a stale "update available".
/// </summary>
public class UpdateCheckStartupThrottleTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "remex-update-throttle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void AFreshCachedAnswerIsReusedAndRejudgedAgainstTheRunningBuild()
    {
        var cache = new UpdateCheckCache(Now.AddHours(-2), "2.6.0", "https://example.invalid/r");

        var older = UpdateCheckService.FromCache(cache, Now, currentVersion: "2.5.0.0");
        older!.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        older.LatestVersion.Should().Be("2.6.0");
        older.DownloadUrl.Should().Be("https://example.invalid/r");

        var updated = UpdateCheckService.FromCache(cache, Now, currentVersion: "2.6.0.0");
        updated!.Status.Should().Be(UpdateCheckStatus.UpToDate,
            "the user installed the release the cached check found");
    }

    [Fact]
    public void AStaleOrFutureCachedAnswerIsIgnored()
    {
        UpdateCheckService.FromCache(
            new UpdateCheckCache(Now - UpdateCheckService.StartupCheckInterval - TimeSpan.FromMinutes(1), "2.6.0", "u"),
            Now, "2.5.0.0").Should().BeNull();

        // A clock that jumped backwards must not pin the cache for the whole gap.
        UpdateCheckService.FromCache(
            new UpdateCheckCache(Now.AddHours(3), "2.6.0", "u"),
            Now, "2.5.0.0").Should().BeNull();
    }

    [Fact]
    public void AnUnparseableCachedVersionIsIgnored()
    {
        UpdateCheckService.FromCache(new UpdateCheckCache(Now.AddHours(-1), "garbage", "u"), Now, "2.5.0.0")
            .Should().BeNull();
    }

    [Fact]
    public async Task TheCacheRoundTripsThroughItsFileAndAMissingOrCorruptFileReadsAsNone()
    {
        var path = Path.Combine(_tempDirectory, "update_check.json");
        UpdateCheckService.TryReadCache(path).Should().BeNull();

        var cache = new UpdateCheckCache(Now, "2.6.0", "https://example.invalid/r");
        UpdateCheckService.TryWriteCache(path, cache);
        UpdateCheckService.TryReadCache(path).Should().Be(cache);

        await File.WriteAllTextAsync(path, "{ not json");
        UpdateCheckService.TryReadCache(path).Should().BeNull();
    }
}
