using System;
using System.Collections.Generic;
using System.Linq;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The rule that decides which WindowsPerf sensors a healthy HWiNFO displaces — and therefore which
/// of them do not need reading at all (RemEx-rxth).
///
/// WHY THIS IS THE INTERESTING PART. On a machine with healthy HWiNFO the WindowsPerf read used to
/// run every tick and then be mostly discarded: three PerformanceCounter reads, GlobalMemoryStatusEx,
/// a NIC statistics read and whatever WMI does, at 1 Hz for the life of the process. The obvious fix
/// — skip it whenever HWiNFO is alive — is WRONG, and this file exists to keep it wrong-looking.
/// The overlay is CATEGORY-SCOPED: a WindowsPerf sensor survives unless HWiNFO also supplies its
/// category. HWiNFO's categories come from the user's hardware and configuration, so a machine that
/// reports rich CPU and Memory data but nothing for Disk or Network is ordinary, not exotic — and on
/// that machine a wholesale skip deletes the only Disk and Network sensors there are, silently.
///
/// These are pure functions over sensor lists, so unlike the rest of the HWiNFO tests they need no
/// named shared memory and run on every platform.
/// </summary>
public sealed class HwInfoCategoryOverlayTests
{
    private static SensorReading Perf(string name, string category) => new()
    {
        Name = name,
        Value = 1.0,
        Unit = "%",
        Category = category,
        Source = "WindowsPerf",
    };

    private static SensorReading HwInfo(string name, string category) => new()
    {
        Name = name,
        Value = 2.0,
        Unit = "%",
        Category = category,
        Source = "HWInfo",
    };

    /// <summary>The four categories the WindowsPerf read actually produces.</summary>
    private static List<SensorReading> FullPerfSet() =>
    [
        Perf("Total CPU Usage", "CPU"),
        Perf("Physical Memory Used", "Memory"),
        Perf("Physical Memory Load", "Memory"),
        Perf("Disk Read Rate", "Disk"),
        Perf("Disk Write Rate", "Disk"),
        Perf("Current DL Rate", "Network"),
        Perf("Current UP Rate", "Network"),
    ];

    [Fact]
    public void APerfSensorSurvivesWhenHwInfoDoesNotCoverItsCategory()
    {
        // THE REGRESSION THIS DESIGN EXISTS TO PREVENT. HWiNFO covers CPU and Memory only — the
        // common case on a desktop without drive or NIC sensors configured. Disk and Network must
        // still come from WindowsPerf, which means the read for those categories cannot be skipped.
        var hwinfo = new List<SensorReading> { HwInfo("Core 0", "CPU"), HwInfo("Physical Memory Used", "Memory") };

        var merged = WindowsTelemetryService.MergeHwInfoOverPerf(FullPerfSet(), hwinfo);

        var perfCategories = merged.Where(s => s.Source == "WindowsPerf").Select(s => s.Category).ToHashSet();
        Assert.Contains("Disk", perfCategories);
        Assert.Contains("Network", perfCategories);
        Assert.DoesNotContain("CPU", perfCategories);
        Assert.DoesNotContain("Memory", perfCategories);

        // And the HWiNFO readings are all present, not merely the fallback filtered.
        Assert.Equal(2, merged.Count(s => s.Source == "HWInfo"));
    }

    [Fact]
    public void TheSkipSetIsExactlyWhatTheMergeWouldHaveDiscarded()
    {
        // This is the correctness argument for skipping the read at all, stated as a test: whatever
        // CoveredCategories reports is precisely the set of WindowsPerf sensors MergeHwInfoOverPerf
        // removes. If the two ever disagree, skipping stops being equivalent to reading-then-merging
        // and starts losing data.
        var hwinfo = new List<SensorReading> { HwInfo("Core 0", "CPU"), HwInfo("Drive Temp", "Disk") };
        var perf = FullPerfSet();

        var covered = WindowsTelemetryService.CoveredCategories(hwinfo);
        var merged = WindowsTelemetryService.MergeHwInfoOverPerf(perf, hwinfo);

        var survivingPerf = merged.Where(s => s.Source == "WindowsPerf").ToList();
        var droppedPerf = perf.Except(survivingPerf).ToList();

        Assert.All(droppedPerf, s => Assert.Contains(s.Category, covered));
        Assert.All(survivingPerf, s => Assert.DoesNotContain(s.Category, covered));
    }

    [Fact]
    public void ASensorFromAnotherSourceIsNeverDisplaced()
    {
        // Only WindowsPerf readings are the overlay's business. EnsureRamTotal injects a "System"
        // reading in the Memory category, and HWiNFO covering Memory must not delete it — that card
        // would then sit on "Collecting Data" forever on exactly the machines with the best sensors.
        var fallback = new List<SensorReading>
        {
            Perf("Physical Memory Used", "Memory"),
            new() { Name = "Physical Memory Total", Value = 32, Unit = "GB", Category = "Memory", Source = "System" },
        };
        var hwinfo = new List<SensorReading> { HwInfo("Physical Memory Used", "Memory") };

        var merged = WindowsTelemetryService.MergeHwInfoOverPerf(fallback, hwinfo);

        Assert.Contains(merged, s => s.Source == "System" && s.Name == "Physical Memory Total");
        Assert.DoesNotContain(merged, s => s.Source == "WindowsPerf");
    }

    [Fact]
    public void NoHwInfoReadingsMeansNothingIsSkippedAndNothingIsDropped()
    {
        // A stalled or absent producer must fall ALL the way back. The caller keys the skip set off
        // this being empty, so an HWiNFO that reports nothing this tick cannot quietly suppress the
        // categories it happened to cover on the last one.
        var perf = FullPerfSet();

        Assert.Empty(WindowsTelemetryService.CoveredCategories([]));

        var merged = WindowsTelemetryService.MergeHwInfoOverPerf(perf, []);
        Assert.Equal(perf.Count, merged.Count);
        Assert.All(perf, s => Assert.Contains(merged, m => m.Name == s.Name));
    }

    [Fact]
    public void CategoryMatchingIsOrdinalRatherThanCaseInsensitive()
    {
        // Pinned because it is a real behaviour, not an accident to be "tidied": HWiNFO category
        // strings come from the user's sensor tree, and treating "cpu" as "CPU" would let an
        // unrelated grouping suppress the WindowsPerf CPU reading.
        var hwinfo = new List<SensorReading> { HwInfo("Core 0", "cpu") };

        var merged = WindowsTelemetryService.MergeHwInfoOverPerf([Perf("Total CPU Usage", "CPU")], hwinfo);

        Assert.Contains(merged, s => s.Source == "WindowsPerf" && s.Category == "CPU");
    }
}
