using System.Collections.Generic;
using System.Linq;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Perf audit P4-22: the tick computes HWiNFO's covered categories once and hands them to the merge.
/// Passing the set must give exactly the merge that computing it inside did.
/// </summary>
public class TelemetryMergeCoveredSetTests
{
    private static SensorReading Reading(string name, string category, string source) => new()
    {
        Name = name, Category = category, Source = source, Value = 1, Unit = "%", Id = source + ":" + name,
    };

    [Fact]
    public void PassedCoveredSet_ProducesTheSameMergeAsTheComputedOne()
    {
        var perf = new List<SensorReading>
        {
            Reading("Total CPU Usage", "CPU", "WindowsPerf"),
            Reading("Disk Read Rate", "Disk", "WindowsPerf"),
            Reading("Current DL Rate", "Network", "WindowsPerf"),
            Reading("Physical Memory Total", "Memory", "System"),
        };
        var hwinfo = new List<SensorReading>
        {
            Reading("CPU Package", "CPU", "HWiNFO"),
            Reading("GPU Temperature", "GPU", "HWiNFO"),
        };

        var computed = WindowsTelemetryService.MergeHwInfoOverPerf(perf, hwinfo);
        var passed = WindowsTelemetryService.MergeHwInfoOverPerf(
            perf, hwinfo, WindowsTelemetryService.CoveredCategories(hwinfo));

        Assert.Equal(computed.Select(s => s.Id), passed.Select(s => s.Id));
        Assert.Equal(
            new[] { "WindowsPerf:Disk Read Rate", "WindowsPerf:Current DL Rate", "System:Physical Memory Total", "HWiNFO:CPU Package", "HWiNFO:GPU Temperature" },
            passed.Select(s => s.Id).ToArray());
    }
}
