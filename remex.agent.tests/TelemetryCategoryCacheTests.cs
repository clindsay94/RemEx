using System.Collections.Generic;
using System.Linq;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The per-category fallback cache that serves telemetry when the WindowsPerf read stalls
/// (RemEx-c2g4).
///
/// WHY IT IS KEYED BY CATEGORY. Since RemEx-rxth a tick reads only the categories a healthy HWiNFO is
/// not already covering, so a read is routinely PARTIAL. Cached as one payload, that partial read was
/// what got served on the tick where HWiNFO stopped producing AND the now-full read timed out — a
/// double fault, but it dropped whole categories for that tick, and the cache before rxth had always
/// been complete.
///
/// THE OBVIOUS ALTERNATIVE IS WORSE, and these tests are shaped to make that visible: "only cache
/// complete reads" would mean a machine with good HWiNFO coverage never caches anything after the
/// first tick, so its fallback would freeze permanently rather than for one tick.
///
/// Pure functions over sensor lists, so they need no shared memory or performance counters and run on
/// every platform.
/// </summary>
public sealed class TelemetryCategoryCacheTests
{
    private static SensorReading Perf(string name, string category, double value = 1.0) => new()
    {
        Name = name,
        Value = value,
        Unit = "%",
        Category = category,
        Source = "WindowsPerf",
    };

    private static TelemetryPayload Read(params SensorReading[] sensors) =>
        new() { Sensors = sensors.ToList(), UptimeText = "1d" };

    private static IReadOnlySet<string> Skip(params string[] categories) =>
        new HashSet<string>(categories, System.StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, List<SensorReading>> EmptyCache() =>
        new Dictionary<string, List<SensorReading>>(System.StringComparer.Ordinal);

    [Fact]
    public void ASkippedCategoryKeepsItsPreviousReadingRatherThanBeingLost()
    {
        // THE REGRESSION THIS EXISTS TO PREVENT, as a sequence. Tick 1: HWiNFO covers nothing, so
        // everything is read and cached. Tick 2: HWiNFO covers CPU and Memory, so only Disk and
        // Network are read — and a whole-payload cache would now hold ONLY Disk and Network. A
        // timeout after that must still be able to serve CPU and Memory.
        var afterFullRead = WindowsTelemetryService.CacheReadCategories(
            EmptyCache(),
            Read(Perf("Total CPU Usage", "CPU"), Perf("Physical Memory Load", "Memory"),
                 Perf("Disk Read Rate", "Disk"), Perf("Current DL Rate", "Network")),
            Skip());

        var afterPartialRead = WindowsTelemetryService.CacheReadCategories(
            afterFullRead,
            Read(Perf("Disk Read Rate", "Disk", 5.0), Perf("Current DL Rate", "Network", 6.0)),
            Skip("CPU", "Memory"));

        var served = WindowsTelemetryService.ComposeCachedFallback(afterPartialRead, "1d");
        var categories = served.Sensors.Select(s => s.Category).ToHashSet();

        Assert.Contains("CPU", categories);
        Assert.Contains("Memory", categories);
        Assert.Contains("Disk", categories);
        Assert.Contains("Network", categories);

        // And the categories that WERE read on tick 2 serve the fresher values, not the tick-1 ones.
        Assert.Equal(5.0, served.Sensors.Single(s => s.Category == "Disk").Value);
        Assert.Equal(6.0, served.Sensors.Single(s => s.Category == "Network").Value);
    }

    [Fact]
    public void ACategoryThatWasReadAndProducedNothingIsClearedRatherThanLeftStale()
    {
        // The other direction, and the reason this is not simply "merge everything in". If the
        // machine's only NIC disappears, the Network read still RUNS and yields nothing — so the
        // entry must become empty. Carrying the last rates forward would leave the dashboard showing
        // traffic on an adapter that no longer exists.
        var withNetwork = WindowsTelemetryService.CacheReadCategories(
            EmptyCache(), Read(Perf("Current DL Rate", "Network", 9.0)), Skip());
        Assert.Single(WindowsTelemetryService.ComposeCachedFallback(withNetwork, "1d").Sensors);

        var nicRemoved = WindowsTelemetryService.CacheReadCategories(withNetwork, Read(), Skip());

        Assert.Empty(WindowsTelemetryService.ComposeCachedFallback(nicRemoved, "1d").Sensors);
    }

    [Fact]
    public void AStalledFirstTickServesNothingRatherThanInventingReadings()
    {
        // Nothing has ever been read, so there is nothing honest to serve.
        var served = WindowsTelemetryService.ComposeCachedFallback(EmptyCache(), "");

        Assert.Empty(served.Sensors);
    }

    [Fact]
    public void TheCacheNeverAccumulatesDuplicatesAcrossTicks()
    {
        // Each category is REPLACED, not appended to. Getting this wrong would grow the served
        // payload without bound on a long-running host — and the symptom would be a dashboard slowly
        // filling with duplicate cards rather than an obvious failure.
        var cache = EmptyCache();
        for (var tick = 0; tick < 5; tick++)
        {
            cache = WindowsTelemetryService.CacheReadCategories(
                cache, Read(Perf("Total CPU Usage", "CPU", tick)), Skip());
        }

        var served = WindowsTelemetryService.ComposeCachedFallback(cache, "1d");

        Assert.Single(served.Sensors);
        Assert.Equal(4.0, served.Sensors[0].Value);
    }

    [Fact]
    public void UpdatingTheCacheDoesNotMutateThePreviousMap()
    {
        // The field is swapped wholesale rather than mutated so a concurrent reader sees either the
        // old map or the new one, never a half-updated one. That only holds if the previous map is
        // genuinely left alone.
        //
        // The second update SKIPS Memory deliberately: a carried-over category's List is the same
        // object in both maps, so this is the case where an in-place implementation would corrupt
        // the old map through the shared reference rather than merely rebind a key.
        var first = WindowsTelemetryService.CacheReadCategories(
            EmptyCache(),
            Read(Perf("Total CPU Usage", "CPU", 1.0), Perf("Physical Memory Load", "Memory", 10.0)),
            Skip());

        WindowsTelemetryService.CacheReadCategories(
            first, Read(Perf("Total CPU Usage", "CPU", 2.0)), Skip("Memory"));

        var stillFirst = WindowsTelemetryService.ComposeCachedFallback(first, "1d");
        Assert.Equal(1.0, stillFirst.Sensors.Single(s => s.Category == "CPU").Value);
        Assert.Equal(10.0, stillFirst.Sensors.Single(s => s.Category == "Memory").Value);
    }

    [Fact]
    public void ACategoryNeverReadHasNothingToServe()
    {
        // THE LIMIT OF THE CACHE, stated rather than implied — and the reason GetTelemetryAsync seeds
        // it with one unconditional full read on the first successful tick. A category that has never
        // been read has no entry, so composing serves nothing for it. Left unseeded, a machine whose
        // HWiNFO covered CPU and Memory from process start would never read those categories, never
        // cache them, and a later stall would serve a payload missing them — which is the very hole
        // this cache exists to close.
        var onlyDiskEverRead = WindowsTelemetryService.CacheReadCategories(
            EmptyCache(), Read(Perf("Disk Read Rate", "Disk")), Skip("CPU", "Memory", "Network"));

        var served = WindowsTelemetryService.ComposeCachedFallback(onlyDiskEverRead, "1d");

        Assert.Equal(["Disk"], served.Sensors.Select(s => s.Category).ToArray());
    }
}
