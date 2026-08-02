using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Remex.Agent.Services.Telemetry;

namespace Remex.Agent.Tests;

/// <summary>
/// A fabricated HWiNFO shared-memory region: one CPU "device" and a reading per entry, laid out
/// exactly as <see cref="WindowsTelemetryService"/> expects to find it.
/// </summary>
/// <remarks>
/// <para>
/// WINDOWS ONLY BY CONSTRUCTION. The sampler reads HWiNFO through
/// <c>MemoryMappedFile.OpenExisting</c> on a NAMED map, and named maps do not exist on Linux in .NET,
/// so anything driving this needs a <c>WindowsOnlyFact</c>. Constructing this type on Linux throws
/// from <c>CreateNew</c>.
/// </para>
/// <para>
/// Extracted from <c>HwInfoSamplerTests</c>, which owned it privately, when RemEx-cxel needed the same
/// region for a different question. That bead exists because <c>TelemetryWarmUpGateTests</c> points
/// HWiNFO at a region that does NOT exist — deliberately, to keep its assertions machine-independent —
/// and that choice makes the read-skipping logic unobservable, since the skip set is
/// <c>_fallbackCacheSeeded &amp;&amp; hwinfoSensors.Count > 0</c> and the right conjunct is then
/// permanently false. Pinning the skip needs the opposite fixture, which is this one.
/// </para>
/// <para>
/// The device is named <c>"CPU [#0]: Test CPU"</c> so <c>ClassifyDevice</c> files its readings under
/// the <c>CPU</c> category. That is load-bearing for RemEx-cxel rather than cosmetic: CPU is a
/// category the WindowsPerf fallback ALSO emits, so it is one the skip set can actually cover.
/// </para>
/// </remarks>
internal sealed class SyntheticHwInfoRegion : IDisposable
{
    private const uint Signature = 0x53695748;   // "HWiS"

    private static readonly int HeaderSize = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_SHARED_MEM2>();
    private static readonly int SensorSize = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_SENSOR_ELEMENT>();
    private static readonly int ReadingSize = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_READING_ELEMENT>();

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _writer;

    public SyntheticHwInfoRegion()
    {
        Name = "Local\\RemExHwInfoTest-" + Guid.NewGuid().ToString("N");
        _mmf = MemoryMappedFile.CreateNew(Name, 1 << 20);
        _writer = _mmf.CreateViewAccessor();
    }

    /// <summary>The named map's name, to hand to the service under test.</summary>
    public string Name { get; }

    /// <summary>
    /// The region's <c>poll_time</c>, which the sampler compares against its staleness window. Settable
    /// because the staleness tests move it deliberately; it is written on the next <see cref="Write"/>.
    /// </summary>
    public long PollTime { get; set; } = 1;

    public void Dispose()
    {
        _writer.Dispose();
        _mmf.Dispose();
    }

    /// <summary>Writes a whole region: one sensor "device", and one reading per (label, unit, value).</summary>
    public void Write(params (string Label, string Unit, double Value)[] readings)
    {
        long sensorOffset = HeaderSize;
        long readingOffset = sensorOffset + SensorSize;

        WriteStruct(0, new WindowsTelemetryService.HWiNFO_SHARED_MEM2
        {
            dwSignature = Signature,
            dwVersion = 1,
            dwRevision = 0,
            poll_time = PollTime,
            dwOffsetOfSensorSection = (uint)sensorOffset,
            dwSizeOfSensorElement = (uint)SensorSize,
            dwNumSensorElements = 1,
            dwOffsetOfReadingSection = (uint)readingOffset,
            dwSizeOfReadingElement = (uint)ReadingSize,
            dwNumReadingElements = (uint)readings.Length,
        });

        WriteStruct(sensorOffset, new WindowsTelemetryService.HWiNFO_SENSOR_ELEMENT
        {
            szSensorNameOrig = "CPU [#0]: Test CPU",
            szSensorNameUser = "CPU [#0]: Test CPU",
        });

        for (int i = 0; i < readings.Length; i++)
        {
            WriteReading(readingOffset + (i * (long)ReadingSize), readings[i], (uint)i);
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

    /// <summary>
    /// Overwrites the FIRST reading without touching the header, which is how a live producer
    /// publishes a new value between polls.
    /// </summary>
    public void RewriteFirstReading((string Label, string Unit, double Value) reading) =>
        WriteReading(HeaderSize + SensorSize, reading, 0);

    /// <summary>Corrupts the signature, so the region looks like it belongs to something else.</summary>
    public void CorruptSignature() => _writer.Write(0, 0xDEADBEEFu);

    /// <summary>Advances HWiNFO's poll clock in place, as a live producer does every cycle.</summary>
    public void Poll()
    {
        PollTime++;
        _writer.Write(
            Marshal.OffsetOf<WindowsTelemetryService.HWiNFO_SHARED_MEM2>(
                nameof(WindowsTelemetryService.HWiNFO_SHARED_MEM2.poll_time)).ToInt64(),
            PollTime);
    }
}
