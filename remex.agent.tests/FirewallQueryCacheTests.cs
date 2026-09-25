using Remex.Agent.Services.Readiness;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// P1-23: every launch shelled out (powershell.exe / firewall-cmd+ufw) to re-answer a question whose
/// answer almost never changes. Cached to disk, keyed on exe path + mtime + port, bounded by a TTL so
/// a real change with no matching mtime signal (e.g. an MSI repair that leaves the binary untouched)
/// still self-heals. Only a `true` (allowed) verdict is ever cached — see the round-2 review fix on
/// `IsInboundAllowed` — so a refused/unknown state always re-queries and a user's fix-then-Refresh
/// sequence is never masked by a stale cached "refused".
/// </summary>
/// <remarks>
/// Run counts are compared RELATIVELY (before vs. after), never against a fixed constant: the Linux
/// query path issues up to 2 process spawns per fresh query (<c>firewall-cmd --state</c> then
/// <c>--query-port</c>) where the Windows path issues exactly 1, so a hardcoded expected count would
/// be platform-dependent and silently wrong on Linux CI.
/// </remarks>
public sealed class FirewallQueryCacheTests
{
    private const string ExePath = @"C:\Program Files\RemEx\Remex.Agent.exe";
    private static readonly DateTime ExeWriteTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Always resolves as a genuine "allowed" verdict on both platforms: on Windows a single call
    /// returning a matching enabled/allow rule; on Linux both possible calls (`--state`, then
    /// `--query-port`, made only when `--state` succeeds) return exit 0, which
    /// <c>FirewallReadiness.InterpretFirewalld(0, 0)</c> resolves to <see langword="true"/>.
    /// </summary>
    private static CommandResult AllowedRun(string fileName, string _) =>
        fileName.Contains("powershell", StringComparison.OrdinalIgnoreCase)
            ? new CommandResult(true, 0, "RULE|RemexHostInbound|1|1|" + ExePath + "\nREMEX-FW-DONE\n")
            : new CommandResult(true, 0, string.Empty);

    private static (FirewallQuery query, Func<int> runCount) Build(
        string? cachedJson = null,
        DateTime? exeLastWriteUtc = null,
        Func<string, string, CommandResult>? run = null,
        DateTime? utcNow = null,
        Action<string>? onWriteCache = null)
    {
        var runs = 0;
        var storedCache = cachedJson;

        var query = new FirewallQuery(
            (fileName, args) =>
            {
                runs++;
                return (run ?? AllowedRun)(fileName, args);
            },
            static _ => null,
            () => ExePath,
            _ => exeLastWriteUtc ?? ExeWriteTime,
            () => storedCache,
            json =>
            {
                storedCache = json;
                onWriteCache?.Invoke(json);
            },
            () => utcNow ?? Now);

        return (query, () => runs);
    }

    [Fact]
    public void FirstCallQueries()
    {
        var (query, runCount) = Build();

        query.IsInboundAllowed(51820);

        Assert.True(runCount() > 0, "The first call must reach the real query path.");
    }

    [Fact]
    public void ASecondCallWithNothingChangedUsesTheCacheNotAFreshQuery()
    {
        var (query, runCount) = Build();

        query.IsInboundAllowed(51820);
        var afterFirst = runCount();
        query.IsInboundAllowed(51820);

        Assert.Equal(afterFirst, runCount()); // a cache hit adds zero further runs
    }

    [Fact]
    public void AChangedExeMtimeInvalidatesTheCache()
    {
        var callCount = 0;
        DateTime CurrentMtime() => callCount == 0 ? ExeWriteTime : ExeWriteTime.AddDays(1);

        // Build() doesn't expose a per-call mtime override, so this test constructs its own
        // instance directly to control the mtime read per call.
        string? sharedCache = null;
        var runs = 0;
        var mtimeQuery = new FirewallQuery(
            (fileName, args) =>
            {
                runs++;
                return AllowedRun(fileName, args);
            },
            static _ => null,
            () => ExePath,
            _ =>
            {
                var t = CurrentMtime();
                callCount++;
                return t;
            },
            () => sharedCache,
            json => sharedCache = json,
            () => Now);

        mtimeQuery.IsInboundAllowed(51820);
        var afterFirst = runs;
        // mtime now reads as ExeWriteTime.AddDays(1) - simulates an install/repair overwriting the
        // agent binary between the two calls - so the cache must miss and the run count must climb.
        mtimeQuery.IsInboundAllowed(51820);

        Assert.True(runs > afterFirst, "A changed exe mtime must force a fresh query, not a cache hit.");
    }

    [Fact]
    public void ADifferentPortInvalidatesTheCache()
    {
        var (query, runCount) = Build();

        query.IsInboundAllowed(51820);
        var afterFirst = runCount();
        query.IsInboundAllowed(51821); // a different port must not reuse the first port's verdict

        Assert.True(runCount() > afterFirst, "A different port must force a fresh query, not reuse the other port's cached verdict.");
    }

    [Fact]
    public void ACacheOlderThanTheTtlIsRefreshedNotReused()
    {
        string? sharedCache = null;
        var runs = 0;
        Func<string, string, CommandResult> countingRun = (fileName, args) =>
        {
            runs++;
            return AllowedRun(fileName, args);
        };

        // First query "runs" at Now and caches its verdict.
        var firstQuery = new FirewallQuery(
            countingRun, static _ => null, () => ExePath, _ => ExeWriteTime,
            () => sharedCache, json => sharedCache = json, () => Now);
        firstQuery.IsInboundAllowed(51820);
        var afterFirst = runs;

        // A second instance sharing the SAME on-disk cache, but whose clock has advanced past the
        // 1-hour TTL - exactly what a later app launch on the same day would see.
        var secondQuery = new FirewallQuery(
            countingRun, static _ => null, () => ExePath, _ => ExeWriteTime,
            () => sharedCache, json => sharedCache = json, () => Now.AddHours(2));
        secondQuery.IsInboundAllowed(51820);

        Assert.True(runs > afterFirst, "A cache entry older than the TTL must be refreshed, not reused.");
    }

    [Fact]
    public void ATransientFailureIsNeverCached()
    {
        var writeCalled = false;
        var (query, runCount) = Build(
            run: static (_, _) => CommandResult.NotRun, // PowerShell blocked/absent -> null verdict
            onWriteCache: _ => writeCalled = true);

        var first = query.IsInboundAllowed(51820);
        var afterFirst = runCount();
        var second = query.IsInboundAllowed(51820);

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(runCount() > afterFirst, "A null verdict must never short-circuit the next call via the cache.");
        Assert.False(writeCalled, "A null verdict must never be persisted.");
    }

    [Fact]
    public void ARefusedVerdictIsNeverCached()
    {
        // Confirms the round-2 review fix directly: a `false` (refused) verdict must always
        // re-query, so a user who fixes their firewall and presses Refresh is never shown a stale
        // "refused" left over from before the fix.
        var writeCalled = false;
        var (query, runCount) = Build(
            run: static (fileName, _) =>
                fileName.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                    ? new CommandResult(true, 0, "REMEX-FW-DONE\n") // no RULE lines -> refused
                    : new CommandResult(true, 1, string.Empty), // firewall-cmd exit 1 -> not running
            onWriteCache: _ => writeCalled = true);

        query.IsInboundAllowed(51820);
        var afterFirst = runCount();
        query.IsInboundAllowed(51820);

        Assert.True(runCount() > afterFirst, "A refused verdict must always re-query, never be served from cache.");
        Assert.False(writeCalled, "A refused verdict must never be persisted.");
    }

    [Fact]
    public void ACorruptCacheFileIsTreatedAsAMissAndStillQueries()
    {
        var (query, runCount) = Build(cachedJson: "{ not valid json");

        query.IsInboundAllowed(51820);

        Assert.True(runCount() > 0, "A corrupt cache file must fall through to a real query, not throw or hang.");
    }

    [Fact]
    public void ADifferentExePathInvalidatesTheCache()
    {
        var seeded = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                ExePath = @"C:\Some\Other\Path.exe",
                ExeLastWriteUtcTicks = ExeWriteTime.Ticks,
                Port = 51820,
                Verdict = true,
                ComputedAtUtcTicks = Now.Ticks,
            });
        var (query, runCount) = Build(cachedJson: seeded);

        query.IsInboundAllowed(51820); // different exe path from the cached entry's

        Assert.True(runCount() > 0, "A cache entry for a different exe path must not be treated as a hit.");
    }

    [Fact]
    public void AnOutOfRangeCachedTimestampIsTreatedAsStaleNotAThrow()
    {
        var seeded = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                ExePath,
                ExeLastWriteUtcTicks = ExeWriteTime.Ticks,
                Port = 51820,
                Verdict = true,
                ComputedAtUtcTicks = long.MaxValue, // corrupt/tampered - out of DateTime's valid range
            });
        var (query, runCount) = Build(cachedJson: seeded);

        var verdict = query.IsInboundAllowed(51820); // must not throw

        Assert.True(runCount() > 0, "An out-of-range cached timestamp must be treated as stale, not trusted.");
    }
}
