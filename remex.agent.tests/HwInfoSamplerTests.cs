using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Drives the HWiNFO sampler against a shared-memory region this test writes itself.
/// </summary>
/// <remarks>
/// <para>
/// RemEx-8coq stopped reopening the mapping and re-marshalling every reading each tick. The perf win
/// is easy; the danger is that the interesting failures are ALL invisible against a live HWiNFO.
/// In particular, once a handle is held the section stays alive after HWiNFO exits — so a cached
/// mapping keeps returning the last bytes it ever wrote, with a valid signature and unchanged
/// geometry, and nothing throws. A smoke test with HWiNFO running looks perfect.
/// </para>
/// <para>
/// Synthesising the region is what makes that testable: this fixture owns the producer side, so it
/// can freeze <c>poll_time</c> the way a dead HWiNFO does and assert the sampler reports unavailable
/// instead of replaying stale numbers.
/// </para>
/// </remarks>
public sealed class HwInfoSamplerTests : IDisposable
{
    /// <summary>
    /// EVERY test in this class must be <c>[WindowsOnlyFact]</c>, not only the ones that obviously
    /// touch shared memory: the fixture CONSTRUCTOR creates the named map, so an unmarked test throws
    /// on Linux at construction — and the error points at the constructor rather than at the missing
    /// attribute, which is a confusing place to start debugging (RemEx-z17h).
    /// </summary>
    private const string WindowsOnlyBecause =
        "the sampler under test lives in WindowsTelemetryService, which is [SupportedOSPlatform(windows)] " +
        "and reads HWiNFO through MemoryMappedFile.OpenExisting on a NAMED map. Named maps are Windows-only " +
        "in .NET, so there is no Linux code path here left untested — this test fabricates the same named " +
        "region to drive it";

    private const uint Signature = 0x53695748;   // "HWiS"
    private const long StaleAfterMs = 150;

    // The region moved to SyntheticHwInfoRegion when RemEx-cxel needed the same fixture for the
    // read-skipping question. Behaviour is unchanged — it is the same writer, the same layout and
    // the same "CPU [#0]: Test CPU" device.
    private readonly SyntheticHwInfoRegion _region = new();

    public void Dispose() => _region.Dispose();

    private WindowsTelemetryService NewSampler() =>
        new(NullLogger<WindowsTelemetryService>.Instance, _region.Name, StaleAfterMs);


    private static TelemetryPayload EmptyPayload() => new() { Sensors = new List<SensorReading>() };

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void ReadsSensorsFromTheSharedRegion()
    {
        _region.Write(("Core 0", "°C", 42.0), ("Core 1", "°C", 43.5));
        var sampler = NewSampler();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var result));

        var names = result.Sensors.Select(s => s.Name).ToList();
        Assert.Equal(2, result.Sensors.Count);
        Assert.Contains(names, n => n.Contains("Core 0"));
        Assert.Equal(42.0, result.Sensors.First(s => s.Name.Contains("Core 0")).Value);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void SecondTickPicksUpNewValues_WithoutRebuildingLabels()
    {
        // The core claim of the refactor: a tick re-reads ONLY the value doubles, and the cached
        // template still supplies the right label. If the value address were wrong by one element
        // this is where it shows.
        _region.Write(("Core 0", "°C", 42.0), ("Core 1", "°C", 43.5));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out _));

        _region.RewriteFirstReading(("Core 0", "°C", 61.0));
        _region.Poll();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var second));
        Assert.Equal(61.0, second.Sensors.First(s => s.Name.Contains("Core 0")).Value);

        // 44, not the 43.5 that was written: FormatSensorValue rounds temperatures, which is
        // pre-existing behaviour and worth stating here — the point of the assertion is that the
        // UNCHANGED reading kept its own value while its neighbour moved, i.e. the value addresses
        // are not off by one element.
        Assert.Equal(44.0, second.Sensors.First(s => s.Name.Contains("Core 1")).Value);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void OutOfRangeTemperatureIsDroppedThenRecovers()
    {
        // The temperature sanity filter is deliberately NOT baked into the template, so a sensor
        // reading a placeholder 127°C now can come back later. Previously asserted only in a comment.
        _region.Write(("Core 0", "°C", 127.0));
        var sampler = NewSampler();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var hot));
        Assert.Empty(hot.Sensors);

        _region.RewriteFirstReading(("Core 0", "°C", 55.0));
        _region.Poll();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var recovered));
        Assert.Single(recovered.Sensors);
        Assert.Equal(55.0, recovered.Sensors[0].Value);
    }

    /// <summary>
    /// THE ONE THIS FIXTURE EXISTS FOR: a producer that stopped must not be replayed as live.
    /// </summary>
    /// <remarks>
    /// The section object outlives HWiNFO for as long as anyone holds a handle — and after this
    /// change the sampler holds one permanently. So "HWiNFO exited" looks exactly like "HWiNFO has
    /// not polled since": the signature is still valid, the geometry is unchanged, and every value
    /// still reads back. Without the poll_time check the dashboard would show frozen temperatures
    /// forever AND keep suppressing the WindowsPerf fallback, because TryReadHwInfo would keep
    /// returning true. This test is that scenario, minus the 30-second wait.
    /// </remarks>
    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void StalePollTimeReportsUnavailable_RatherThanReplayingTheLastValues()
    {
        _region.Write(("Core 0", "°C", 42.0));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out _));

        // Producer stops: the region stays perfectly readable, poll_time simply never moves again.
        Thread.Sleep((int)StaleAfterMs + 100);

        Assert.False(sampler.TryReadHwInfo(EmptyPayload(), out var result));
        Assert.Empty(result.Sensors);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void ResumedPollingIsPickedUpAgain()
    {
        _region.Write(("Core 0", "°C", 42.0));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out _));

        Thread.Sleep((int)StaleAfterMs + 100);
        Assert.False(sampler.TryReadHwInfo(EmptyPayload(), out _));

        // HWiNFO comes back. The mapping was dropped, so this exercises the reopen path too.
        _region.RewriteFirstReading(("Core 0", "°C", 50.0));
        _region.Poll();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var resumed));
        Assert.Equal(50.0, resumed.Sensors.Single().Value);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void LayoutChangeRebuildsTheTemplates()
    {
        // A cached template set keyed on stale geometry would read values from the wrong offsets.
        _region.Write(("Core 0", "°C", 42.0));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var before));
        Assert.Single(before.Sensors);

        _region.Write(("Core 0", "°C", 42.0), ("Core 1", "°C", 44.0));
        _region.Poll();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var after));
        Assert.Equal(2, after.Sensors.Count);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void BadSignatureIsRejected()
    {
        _region.Write(("Core 0", "°C", 42.0));
        _region.CorruptSignature();

        Assert.False(NewSampler().TryReadHwInfo(EmptyPayload(), out _));
    }
}
