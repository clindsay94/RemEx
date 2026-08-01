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

    private readonly string _name = "Local\\RemExHwInfoTest-" + Guid.NewGuid().ToString("N");
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _writer;
    private readonly int _headerSize = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_SHARED_MEM2>();
    private readonly int _sensorSize = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_SENSOR_ELEMENT>();
    private readonly int _readingSize = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_READING_ELEMENT>();

    private long _pollTime = 1;

    public HwInfoSamplerTests()
    {
        _mmf = MemoryMappedFile.CreateNew(_name, 1 << 20);
        _writer = _mmf.CreateViewAccessor();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _mmf.Dispose();
    }

    private WindowsTelemetryService NewSampler() =>
        new(NullLogger<WindowsTelemetryService>.Instance, _name, StaleAfterMs);

    /// <summary>Writes a whole region: one sensor "device", and one reading per (label, unit, value).</summary>
    private void WriteRegion(params (string Label, string Unit, double Value)[] readings)
    {
        long sensorOffset = _headerSize;
        long readingOffset = sensorOffset + _sensorSize;

        WriteStruct(0, new WindowsTelemetryService.HWiNFO_SHARED_MEM2
        {
            dwSignature = Signature,
            dwVersion = 1,
            dwRevision = 0,
            poll_time = _pollTime,
            dwOffsetOfSensorSection = (uint)sensorOffset,
            dwSizeOfSensorElement = (uint)_sensorSize,
            dwNumSensorElements = 1,
            dwOffsetOfReadingSection = (uint)readingOffset,
            dwSizeOfReadingElement = (uint)_readingSize,
            dwNumReadingElements = (uint)readings.Length,
        });

        WriteStruct(sensorOffset, new WindowsTelemetryService.HWiNFO_SENSOR_ELEMENT
        {
            szSensorNameOrig = "CPU [#0]: Test CPU",
            szSensorNameUser = "CPU [#0]: Test CPU",
        });

        for (int i = 0; i < readings.Length; i++)
        {
            WriteReading(readingOffset + (i * (long)_readingSize), readings[i], (uint)i);
        }
    }

    private void WriteReading(long offset, (string Label, string Unit, double Value) r, uint id) =>
        WriteStruct(offset, new WindowsTelemetryService.HWiNFO_READING_ELEMENT
        {
            tReading = WindowsTelemetryService.SENSOR_READING_TYPE.SENSOR_TYPE_TEMP,
            dwSensorIndex = 0,
            dwReadingID = id,
            szLabelUser = r.Label,
            szLabelOrig = r.Label,
            szUnit = r.Unit,
            Value = r.Value,
            ValueMin = -999,
            ValueMax = 999,
            ValueAvg = 0,
        });

    private void WriteStruct<T>(long offset, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buffer, fDeleteOld: false);
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            _writer.WriteArray(offset, bytes, 0, size);
        }
        finally
        {
            Marshal.DestroyStructure<T>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Advances HWiNFO's poll clock, as a live producer does every cycle.</summary>
    private void Poll()
    {
        _pollTime++;
        _writer.Write(
            Marshal.OffsetOf<WindowsTelemetryService.HWiNFO_SHARED_MEM2>(
                nameof(WindowsTelemetryService.HWiNFO_SHARED_MEM2.poll_time)).ToInt64(),
            _pollTime);
    }

    private static TelemetryPayload EmptyPayload() => new() { Sensors = new List<SensorReading>() };

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void ReadsSensorsFromTheSharedRegion()
    {
        WriteRegion(("Core 0", "°C", 42.0), ("Core 1", "°C", 43.5));
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
        WriteRegion(("Core 0", "°C", 42.0), ("Core 1", "°C", 43.5));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out _));

        WriteReading(_headerSize + _sensorSize, ("Core 0", "°C", 61.0), 0);
        Poll();

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
        WriteRegion(("Core 0", "°C", 127.0));
        var sampler = NewSampler();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var hot));
        Assert.Empty(hot.Sensors);

        WriteReading(_headerSize + _sensorSize, ("Core 0", "°C", 55.0), 0);
        Poll();

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
        WriteRegion(("Core 0", "°C", 42.0));
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
        WriteRegion(("Core 0", "°C", 42.0));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out _));

        Thread.Sleep((int)StaleAfterMs + 100);
        Assert.False(sampler.TryReadHwInfo(EmptyPayload(), out _));

        // HWiNFO comes back. The mapping was dropped, so this exercises the reopen path too.
        WriteReading(_headerSize + _sensorSize, ("Core 0", "°C", 50.0), 0);
        Poll();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var resumed));
        Assert.Equal(50.0, resumed.Sensors.Single().Value);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void LayoutChangeRebuildsTheTemplates()
    {
        // A cached template set keyed on stale geometry would read values from the wrong offsets.
        WriteRegion(("Core 0", "°C", 42.0));
        var sampler = NewSampler();
        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var before));
        Assert.Single(before.Sensors);

        WriteRegion(("Core 0", "°C", 42.0), ("Core 1", "°C", 44.0));
        Poll();

        Assert.True(sampler.TryReadHwInfo(EmptyPayload(), out var after));
        Assert.Equal(2, after.Sensors.Count);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void BadSignatureIsRejected()
    {
        WriteRegion(("Core 0", "°C", 42.0));
        _writer.Write(0, 0xDEADBEEFu);

        Assert.False(NewSampler().TryReadHwInfo(EmptyPayload(), out _));
    }
}
