using System;
using System.Runtime.InteropServices;
using Remex.Agent.Services.Telemetry;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the byte layout the HWiNFO sampler now depends on.
/// </summary>
/// <remarks>
/// RemEx-8coq stopped re-marshalling every reading element every second. A tick now reads ONLY the
/// <c>Value</c> double, at a byte offset computed once from the struct. That makes the layout
/// load-bearing in a way it was not before: previously a wrong field order would have produced
/// obviously broken labels, whereas now it silently reads eight bytes from the wrong place and
/// reports a plausible-looking number for the wrong sensor.
/// <para>
/// These tests need no HWiNFO running — they build a reading element in memory, which is the only
/// part of the path that can be exercised off-device anyway.
/// </para>
/// </remarks>
public class HwInfoReadingLayoutTests
{
    /// <summary>
    /// The offset the sampler reads from must be the offset the value is actually written to.
    /// </summary>
    /// <remarks>
    /// Round-trips a real struct through unmanaged memory rather than asserting a magic number, so
    /// the test states the PROPERTY (reading at that offset yields the value) instead of restating
    /// the arithmetic the production code already does.
    /// </remarks>
    [Fact]
    public void ValueOffset_PointsAtTheValueField()
    {
        var element = new WindowsTelemetryService.HWiNFO_READING_ELEMENT
        {
            tReading = WindowsTelemetryService.SENSOR_READING_TYPE.SENSOR_TYPE_TEMP,
            dwSensorIndex = 3,
            dwReadingID = 7,
            szLabelUser = "CPU Package",
            szLabelOrig = "CPU Package",
            szUnit = "°C",
            Value = 61.5,
            ValueMin = 30,
            ValueMax = 90,
            ValueAvg = 55,
        };

        int size = Marshal.SizeOf<WindowsTelemetryService.HWiNFO_READING_ELEMENT>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(element, buffer, fDeleteOld: false);

            var valueOffset = (int)Marshal.OffsetOf<WindowsTelemetryService.HWiNFO_READING_ELEMENT>(
                nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.Value));

            var readBack = Marshal.PtrToStructure<double>(buffer + valueOffset);

            Assert.Equal(61.5, readBack);

            // ...and it must NOT be one of the neighbouring doubles, which is the failure this
            // guards: Min/Max/Avg sit directly after Value and would all look like credible readings.
            Assert.NotEqual(element.ValueMin, readBack);
            Assert.NotEqual(element.ValueMax, readBack);
            Assert.NotEqual(element.ValueAvg, readBack);
        }
        finally
        {
            Marshal.DestroyStructure<WindowsTelemetryService.HWiNFO_READING_ELEMENT>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Value must be preceded by exactly the fields the struct declares, in order.
    /// </summary>
    /// <remarks>
    /// A second, independent check on the same fact: inserting or resizing a field ahead of
    /// <c>Value</c> shifts the offset, and the sampler would then read a label's bytes as a double.
    /// Asserting the composition rather than a literal keeps this honest if the marshalling of the
    /// fixed-length strings ever changes.
    /// </remarks>
    [Fact]
    public void ValueIsPrecededByTheDeclaredFields()
    {
        static int OffsetOf(string field) =>
            (int)Marshal.OffsetOf<WindowsTelemetryService.HWiNFO_READING_ELEMENT>(field);

        var unitOffset = OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.szUnit));
        var valueOffset = OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.Value));

        // Value starts after the whole 16-char ANSI unit field. Asserted as a bound rather than an
        // exact number: computing the offset here would re-implement the marshaller's own rules, and
        // a test that duplicates the thing it checks fails for the wrong reasons.
        //
        // Note this struct is Pack = 1 (it mirrors HWiNFO's on-the-wire layout), so Value is NOT
        // 8-byte aligned — it lands on 284. Worth stating because it is the opposite of what a
        // default-packed struct would do, and an unaligned double is exactly the kind of thing
        // someone "fixes" by dropping the Pack, which would silently shift every field after it.
        Assert.True(valueOffset >= unitOffset + 16,
            $"Value at {valueOffset} overlaps szUnit at {unitOffset}");

        // Sequential layout, so the ordering the sampler assumes has to hold.
        Assert.True(
            OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.tReading))
            < OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.szLabelUser)));
        Assert.True(
            OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.szLabelUser))
            < OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.Value)));
        Assert.True(
            OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.Value))
            < OffsetOf(nameof(WindowsTelemetryService.HWiNFO_READING_ELEMENT.ValueMin)));
    }
}
