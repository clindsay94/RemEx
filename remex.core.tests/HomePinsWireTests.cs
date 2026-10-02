using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// <c>home_pins_sync</c> / <c>home_pins_change</c> survive the wire, and a malformed one costs its own
/// slot rather than the session (RemEx-wqo7a.5).
/// </summary>
/// <remarks>
/// The hand-written JSON cases are the shape the Kotlin side builds and parses (camelCase slot names,
/// no protocolVersion needed), for the reason <c>PhoneThemeSnapshotRoundTripTests</c> gives: a round
/// trip that only compares this serializer to itself would pass on a name only the PC agrees with.
/// </remarks>
public class HomePinsWireTests
{
    private static string Serialize(RemexMessage m) =>
        RemexJson.Serialize(m, RemexJsonSerializerContext.Relaxed.RemexMessage);

    private static RemexMessage? Deserialize(string json) =>
        RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);

    [Fact]
    public void ASyncSurvivesTheRoundTrip()
    {
        var updated = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var sent = new RemexMessage
        {
            Type = MessageTypes.HomePinsSync,
            HomePins = new HomePinnedSensors
            {
                SensorNames = ["CPU Package", "GPU Core"],
                PinnableSensorNames = ["CPU Package", "GPU Core", "Ünïcødé fan"],
                Revision = 7,
                UpdatedUtc = updated,
            },
        };

        var json = Serialize(sent);
        var arrived = Deserialize(json)!.HomePins;

        Assert.Contains("\"homePins\":", json, StringComparison.Ordinal);
        Assert.Contains("\"sensorNames\":", json, StringComparison.Ordinal);
        Assert.Contains("\"pinnableSensorNames\":", json, StringComparison.Ordinal);
        Assert.NotNull(arrived);
        Assert.Equal(["CPU Package", "GPU Core"], arrived!.SensorNames);
        Assert.Equal(["CPU Package", "GPU Core", "Ünïcødé fan"], arrived.PinnableSensorNames);
        Assert.Equal(7, arrived.Revision);
        Assert.Equal(updated, arrived.UpdatedUtc);
    }

    [Fact]
    public void TheChangeThePhoneSendsDeserializes()
    {
        var arrived = Deserialize(
            """{"type":"home_pins_change","protocolVersion":2,"homePinChange":{"sensorName":"CPU Package","pinned":true}}""");

        Assert.Equal(MessageTypes.HomePinsChange, arrived!.Type);
        Assert.Equal("CPU Package", arrived.HomePinChange!.SensorName);
        Assert.True(arrived.HomePinChange.Pinned);
        Assert.Null(arrived.HomePins);
    }

    [Theory]
    [InlineData("""{"type":"home_pins_change","homePinChange":{"sensorName":"CPU","pinned":"yes"}}""")]
    [InlineData("""{"type":"home_pins_change","homePinChange":{"sensorName":42,"pinned":true}}""")]
    [InlineData("""{"type":"home_pins_change","homePinChange":"CPU"}""")]
    [InlineData("""{"type":"home_pins_sync","homePins":{"sensorNames":[1,2],"revision":3}}""")]
    [InlineData("""{"type":"home_pins_sync","homePins":{"sensorNames":["a"],"revision":"three"}}""")]
    public void AWrongTypedPayloadNullsItsSlotAndKeepsTheEnvelope(string json)
    {
        // THE ENVELOPE SURVIVING IS THE WHOLE POINT. A null envelope is what makes PingPongHandler's
        // receive loop treat a message as a disconnect, so a strict reader here would let one bad
        // pin toggle drop the phone's session for every feature.
        var arrived = Deserialize(json);

        Assert.NotNull(arrived);
        Assert.StartsWith("home_pins_", arrived!.Type, StringComparison.Ordinal);
        Assert.Null(arrived.HomePins);
        Assert.Null(arrived.HomePinChange);
    }

    [Fact]
    public void AbsentListsNormalizeToEmptyRatherThanNull()
    {
        // The source-generated reader leaves an ABSENT init-only list null despite its initializer,
        // which is why consumers go through Normalize instead of trusting the defaults.
        var arrived = Deserialize("""{"type":"home_pins_sync","homePins":{"revision":1}}""")!.HomePins;
        Assert.NotNull(arrived);

        var normalized = HomePinsValidation.Normalize(arrived)!;
        Assert.Empty(normalized.SensorNames);
        Assert.Empty(normalized.PinnableSensorNames);
        Assert.Equal(1, normalized.Revision);
    }

    [Fact]
    public void AnUnknownExtraFieldIsIgnored()
    {
        var arrived = Deserialize(
            """{"type":"home_pins_sync","homePins":{"sensorNames":["CPU"],"pinnableSensorNames":["CPU"],"revision":2,"future":true}}""");

        Assert.Equal(["CPU"], arrived!.HomePins!.SensorNames);
    }

    [Fact]
    public void ASyncOmitsEveryOtherSlot()
    {
        var json = Serialize(new RemexMessage { Type = MessageTypes.HomePinsSync, HomePins = new HomePinnedSensors() });

        Assert.DoesNotContain("homePinChange", json, StringComparison.Ordinal);
        Assert.DoesNotContain("themeSync", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsHomePinsSyncTravelsAndDefaultsFalse()
    {
        var json = RemexJson.Serialize(
            new HostCapabilities { SupportsHomePinsSync = true },
            RemexJsonSerializerContext.Relaxed.HostCapabilities);

        Assert.Contains("\"supportsHomePinsSync\":true", json, StringComparison.Ordinal);
        Assert.False(RemexJson.Deserialize("{}", RemexJsonSerializerContext.Default.HostCapabilities)!.SupportsHomePinsSync);
    }
}

/// <summary>The sensor-name rule both ends of the pin sync apply (RemEx-wqo7a.5).</summary>
public class HomePinsValidationTests
{
    [Theory]
    [InlineData("CPU Package")]
    [InlineData(" trailing space is part of the name ")]
    [InlineData("Ünïcødé fan")]
    public void ARealNameIsValid(string name) => Assert.True(HomePinsValidation.IsValidSensorName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CPU\nPackage")]
    [InlineData("CPU\u0000")]
    [InlineData("tab\there")]
    public void ABlankOrControlCharacterNameIsInvalid(string? name) =>
        Assert.False(HomePinsValidation.IsValidSensorName(name));

    [Fact]
    public void TheLengthLimitIsInclusive()
    {
        Assert.True(HomePinsValidation.IsValidSensorName(new string('a', HomePinsValidation.MaxNameLength)));
        Assert.False(HomePinsValidation.IsValidSensorName(new string('a', HomePinsValidation.MaxNameLength + 1)));
    }

    [Fact]
    public void AChangeNeedsAValidName()
    {
        Assert.True(HomePinsValidation.IsValidChange(new HomePinChange { SensorName = "CPU", Pinned = false }));
        Assert.False(HomePinsValidation.IsValidChange(new HomePinChange()));
        Assert.False(HomePinsValidation.IsValidChange(null));
    }

    [Fact]
    public void DuplicatesAreRemovedCaseInsensitivelyKeepingTheFirst()
    {
        var normalized = HomePinsValidation.NormalizeNames(["GPU Core", "CPU", "gpu core", "cpu", "Fan"]);

        Assert.Equal(["GPU Core", "CPU", "Fan"], normalized);
    }

    [Fact]
    public void InvalidNamesAreDroppedAndTheOrderOfTheRestKept()
    {
        var normalized = HomePinsValidation.NormalizeNames(["B", null, "", "bad\u0007", "A"]);

        Assert.Equal(["B", "A"], normalized);
    }

    [Fact]
    public void AListIsCappedKeepingTheFirstNames()
    {
        var names = Enumerable.Range(0, HomePinsValidation.MaxListCount + 20).Select(i => $"Sensor {i}").ToList();

        var normalized = HomePinsValidation.NormalizeNames(names);

        Assert.Equal(HomePinsValidation.MaxListCount, normalized.Count);
        Assert.Equal("Sensor 0", normalized[0]);
        Assert.Equal($"Sensor {HomePinsValidation.MaxListCount - 1}", normalized[^1]);
    }

    [Fact]
    public void TheCapCountsKeptNamesNotRawEntries()
    {
        // Duplicates and junk before the cap must not push real names out of the list.
        var names = new List<string?> { "x", "X", null, "" };
        names.AddRange(Enumerable.Range(0, HomePinsValidation.MaxListCount).Select(i => $"S{i}"));

        var normalized = HomePinsValidation.NormalizeNames(names);

        Assert.Equal(HomePinsValidation.MaxListCount, normalized.Count);
        Assert.Equal("x", normalized[0]);
        Assert.Equal($"S{HomePinsValidation.MaxListCount - 2}", normalized[^1]);
    }

    [Fact]
    public void NormalizeNullIsNullAndKeepsRevision()
    {
        Assert.Null(HomePinsValidation.Normalize(null));

        var normalized = HomePinsValidation.Normalize(new HomePinnedSensors
        {
            SensorNames = ["A", "a"],
            PinnableSensorNames = ["A"],
            Revision = 9,
        })!;

        Assert.Equal(["A"], normalized.SensorNames);
        Assert.Equal(9, normalized.Revision);
    }
}
