using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Perf audit P4-19: the hwmon topology (which chips, which sensors, their names and labels) is
/// scanned once and reused; only the *_input value files are read every tick. Runs against a fake
/// sysfs tree, so it covers the Linux path on any OS.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxTelemetryTopologyCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remex-hwmon-" + Guid.NewGuid().ToString("N"));
    private readonly string _hwmon;
    private readonly string _proc;
    private long _nowMs = 1_000;

    public LinuxTelemetryTopologyCacheTests()
    {
        _hwmon = Path.Combine(_root, "hwmon");
        _proc = Path.Combine(_root, "proc");
        Directory.CreateDirectory(_proc);
        File.WriteAllText(Path.Combine(_proc, "stat"), "cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 100 0 100 800 0 0 0 0 0 0\nintr 12345 0 0\n");
        File.WriteAllText(Path.Combine(_proc, "meminfo"), "MemTotal:       16777216 kB\nMemFree:         4194304 kB\nMemAvailable:    8388608 kB\nBuffers:          524288 kB\nCached:          2097152 kB\n");
        File.WriteAllText(Path.Combine(_proc, "uptime"), "93784.50 1000.00\n");

        // hwmon0: CPU chip with a labelled temp, an unlabelled temp, and a bogus 127 C placeholder.
        Write("hwmon0", "name", "k10temp");
        Write("hwmon0", "temp1_input", "45500");
        Write("hwmon0", "temp1_label", "Tctl");
        Write("hwmon0", "temp2_input", "50000");
        Write("hwmon0", "temp3_input", "127000");
        // hwmon1: a board chip with one spinning and one stopped fan.
        Write("hwmon1", "name", "nct6798");
        Write("hwmon1", "fan1_input", "1200");
        Write("hwmon1", "fan2_input", "0");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Write(string chip, string file, string content)
    {
        var dir = Path.Combine(_hwmon, chip);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content + "\n");
    }

    private LinuxTelemetryService Create() =>
        new(NullLogger<LinuxTelemetryService>.Instance, _hwmon, _proc, () => _nowMs);

    private static SensorReading[] Hwmon(TelemetryPayload payload) =>
        payload.Sensors!.Where(s => s.Id!.StartsWith("linux:hwmon:", StringComparison.Ordinal)).OrderBy(s => s.Id).ToArray();

    [Fact]
    public async Task Output_MatchesTheUncachedShape()
    {
        var service = Create();
        var hw = Hwmon(await service.GetTelemetryAsync());

        // The 127 C placeholder is filtered; the stopped fan is filtered.
        Assert.Equal(new[] { "linux:hwmon:hwmon0:temp1_input", "linux:hwmon:hwmon0:temp2_input", "linux:hwmon:hwmon1:fan1_input" },
            hw.Select(s => s.Id).ToArray());
        Assert.Equal(45.5, hw[0].Value);
        Assert.Equal("°C", hw[0].Unit);
        Assert.Equal(1200, hw[2].Value);
        Assert.Equal("RPM", hw[2].Unit);
        Assert.Equal("Fan", hw[2].Category);
        Assert.Equal(MetricKind.FanRpm, hw[2].Kind);
    }

    [Fact]
    public async Task Values_AreReadEveryTick_ButTheTopologyIsScannedOnce()
    {
        var service = Create();
        var first = Hwmon(await service.GetTelemetryAsync());

        Write("hwmon0", "temp1_input", "61000");
        Write("hwmon0", "temp3_input", "70000");   // placeholder became a real value
        Write("hwmon1", "fan2_input", "900");      // stopped fan spun up
        Write("hwmon0", "temp1_label", "Renamed"); // label changes are topology, not values
        _nowMs += 1_000;

        var second = Hwmon(await service.GetTelemetryAsync());

        Assert.Equal(1, service.TopologyScanCount);
        Assert.Equal(61.0, second.Single(s => s.Id == "linux:hwmon:hwmon0:temp1_input").Value);
        Assert.Equal(70.0, second.Single(s => s.Id == "linux:hwmon:hwmon0:temp3_input").Value);
        Assert.Equal(900, second.Single(s => s.Id == "linux:hwmon:hwmon1:fan2_input").Value);
        Assert.Equal(
            first.Single(s => s.Id == "linux:hwmon:hwmon0:temp1_input").Name,
            second.Single(s => s.Id == "linux:hwmon:hwmon0:temp1_input").Name);
    }

    [Fact]
    public async Task Topology_IsRescannedAfterTheRefreshInterval_PickingUpNewSensors()
    {
        var service = Create();
        await service.GetTelemetryAsync();

        Write("hwmon2", "name", "nvme");
        Write("hwmon2", "temp1_input", "38000");
        _nowMs += (long)LinuxTelemetryService.TopologyRefreshInterval.TotalMilliseconds + 1;

        var hw = Hwmon(await service.GetTelemetryAsync());

        Assert.Equal(2, service.TopologyScanCount);
        Assert.Contains(hw, s => s.Id == "linux:hwmon:hwmon2:temp1_input" && s.Value == 38.0);
    }

    [Fact]
    public async Task VanishedSensor_IsSkipped_NotFatal()
    {
        var service = Create();
        await service.GetTelemetryAsync();

        File.Delete(Path.Combine(_hwmon, "hwmon1", "fan1_input"));
        var hw = Hwmon(await service.GetTelemetryAsync());

        Assert.DoesNotContain(hw, s => s.Id == "linux:hwmon:hwmon1:fan1_input");
        Assert.Contains(hw, s => s.Id == "linux:hwmon:hwmon0:temp1_input");
    }

    [Fact]
    public async Task ProcValues_StillParse()
    {
        var service = Create();
        var payload = await service.GetTelemetryAsync();

        Assert.Equal(16.0, payload.Sensors!.Single(s => s.Id == "linux:mem:total").Value);
        Assert.Equal("1d 2h 3m", payload.UptimeText);
        Assert.Contains(payload.Sensors!, s => s.Id == "linux:cpu:load");
    }
}
