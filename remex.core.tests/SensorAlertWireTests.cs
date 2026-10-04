using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// The phone telemetry alert messages survive the wire, a malformed one costs its own slot rather than
/// the session, and the rules the host applies to a phone's edit are the ones written down
/// (RemEx-pp4cm.12).
/// </summary>
/// <remarks>
/// The hand-written JSON cases are the shape the Kotlin side builds and parses (camelCase slot names,
/// direction and severity as names, no protocolVersion needed), for the reason
/// <c>HomePinsWireTests</c> gives: a round trip that only compares this serializer to itself would pass
/// on a name only the PC agrees with.
/// </remarks>
public class SensorAlertWireTests
{
    private static string Serialize(RemexMessage m) =>
        RemexJson.Serialize(m, RemexJsonSerializerContext.Relaxed.RemexMessage);

    private static RemexMessage? Deserialize(string json) =>
        RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);

    [Fact]
    public void AFiredAlertSurvivesTheRoundTripWithNamedEnums()
    {
        var at = new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero);
        var sent = new RemexMessage
        {
            Type = MessageTypes.SensorAlertFired,
            SensorAlertFired = new SensorAlertFiredEvent
            {
                SensorName = "CPU Package",
                DisplayName = "CPU temperature",
                Value = 92.4,
                Unit = "°C",
                Threshold = 90,
                Direction = AlertDirection.Above,
                Severity = AlertSeverity.Critical,
                FiredAtUtc = at,
            },
        };

        var json = Serialize(sent);
        var arrived = Deserialize(json)!.SensorAlertFired;

        Assert.Contains("\"sensorAlertFired\":", json, StringComparison.Ordinal);
        Assert.Contains("\"direction\":\"Above\"", json, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"Critical\"", json, StringComparison.Ordinal);
        Assert.Contains("\"firedAtUtc\":", json, StringComparison.Ordinal);
        Assert.NotNull(arrived);
        Assert.Equal("CPU Package", arrived!.SensorName);
        Assert.Equal("CPU temperature", arrived.DisplayName);
        Assert.Equal(92.4, arrived.Value);
        Assert.Equal("°C", arrived.Unit);
        Assert.Equal(90, arrived.Threshold);
        Assert.Equal(AlertDirection.Above, arrived.Direction);
        Assert.Equal(AlertSeverity.Critical, arrived.Severity);
        Assert.Equal(at, arrived.FiredAtUtc);
    }

    [Fact]
    public void TheRuleListSurvivesTheRoundTripIncludingAbsentReadings()
    {
        var sent = new RemexMessage
        {
            Type = MessageTypes.SensorAlertRules,
            SensorAlertRules = new SensorAlertRules
            {
                Revision = 4,
                UpdatedUtc = new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero),
                Rules =
                [
                    new SensorAlertRule
                    {
                        SensorName = "GPU Temp", DisplayName = "GPU temperature", Unit = "°C", CurrentValue = 61.5,
                        Threshold = 85, Direction = AlertDirection.Above, Severity = AlertSeverity.Warning,
                    },
                    new SensorAlertRule
                    {
                        SensorName = "Fan 1", DisplayName = "Fan 1", Threshold = 300,
                        Direction = AlertDirection.Below, Severity = AlertSeverity.Critical,
                    },
                ],
            },
        };

        var json = Serialize(sent);
        var arrived = Deserialize(json)!.SensorAlertRules!;

        Assert.Contains("\"sensorAlertRules\":", json, StringComparison.Ordinal);
        Assert.Equal(4, arrived.Revision);
        Assert.Equal(2, arrived.Rules.Count);
        Assert.Equal(61.5, arrived.Rules[0].CurrentValue);
        Assert.Equal("°C", arrived.Rules[0].Unit);
        Assert.Null(arrived.Rules[1].CurrentValue);
        Assert.Null(arrived.Rules[1].Unit);
        Assert.Equal(AlertDirection.Below, arrived.Rules[1].Direction);
        Assert.Equal(AlertSeverity.Critical, arrived.Rules[1].Severity);
    }

    [Fact]
    public void TheSetAndRemoveTheKotlinSideBuildsDeserialize()
    {
        var set = Deserialize(
            """{"type":"sensor_alert_set","protocolVersion":2,"sensorAlertChange":{"sensorName":"CPU Package","threshold":90.5,"direction":"Above","severity":"Warning"}}""");
        var remove = Deserialize(
            """{"type":"sensor_alert_remove","protocolVersion":2,"sensorAlertRemoval":{"sensorName":"CPU Package"}}""");
        var get = Deserialize("""{"type":"sensor_alerts_get","protocolVersion":2}""");

        Assert.Equal(MessageTypes.SensorAlertSet, set!.Type);
        Assert.Equal("CPU Package", set.SensorAlertChange!.SensorName);
        Assert.Equal(90.5, set.SensorAlertChange.Threshold);
        Assert.Equal(AlertDirection.Above, set.SensorAlertChange.Direction);
        Assert.Equal(AlertSeverity.Warning, set.SensorAlertChange.Severity);
        Assert.Equal("CPU Package", remove!.SensorAlertRemoval!.SensorName);
        Assert.Equal(MessageTypes.SensorAlertsGet, get!.Type);
    }

    [Theory]
    [InlineData("""{"type":"sensor_alert_set","sensorAlertChange":{"sensorName":"CPU","threshold":"hot","direction":"Above","severity":"Warning"}}""")]
    [InlineData("""{"type":"sensor_alert_set","sensorAlertChange":{"sensorName":"CPU","threshold":90,"direction":"Sideways","severity":"Warning"}}""")]
    [InlineData("""{"type":"sensor_alert_set","sensorAlertChange":{"sensorName":"CPU","threshold":90,"direction":"Above","severity":"Panic"}}""")]
    [InlineData("""{"type":"sensor_alert_set","sensorAlertChange":{"sensorName":42,"threshold":90,"direction":"Above","severity":"Warning"}}""")]
    [InlineData("""{"type":"sensor_alert_set","sensorAlertChange":"CPU"}""")]
    [InlineData("""{"type":"sensor_alert_remove","sensorAlertRemoval":{"sensorName":["CPU"]}}""")]
    [InlineData("""{"type":"sensor_alert_rules","sensorAlertRules":{"rules":"none","revision":1}}""")]
    [InlineData("""{"type":"sensor_alert_fired","sensorAlertFired":{"sensorName":"CPU","value":"high"}}""")]
    public void AWrongTypedPayloadNullsItsSlotAndKeepsTheEnvelope(string json)
    {
        // THE ENVELOPE SURVIVING IS THE WHOLE POINT. A null envelope is what makes PingPongHandler's
        // receive loop treat a message as a disconnect, so a strict reader here would let one bad
        // alert edit drop the phone's session for every feature.
        var arrived = Deserialize(json);

        Assert.NotNull(arrived);
        Assert.StartsWith("sensor_alert", arrived!.Type, StringComparison.Ordinal);
        Assert.Null(arrived.SensorAlertChange);
        Assert.Null(arrived.SensorAlertRemoval);
        Assert.Null(arrived.SensorAlertRules);
        Assert.Null(arrived.SensorAlertFired);
    }

    [Fact]
    public void TheHostAdvertisesSensorAlertsAndAnOlderHostDefaultsToFalse()
    {
        var json = RemexJson.Serialize(
            new HostCapabilities { SupportsSensorAlerts = true }, RemexJsonSerializerContext.Default.HostCapabilities);

        Assert.Contains("\"supportsSensorAlerts\":true", json, StringComparison.Ordinal);
        Assert.False(RemexJson.Deserialize("{}", RemexJsonSerializerContext.Default.HostCapabilities)!.SupportsSensorAlerts);
    }

    [Fact]
    public void TheWireValuesAreTheConstants()
    {
        Assert.Equal("sensor_alert_fired", MessageTypes.SensorAlertFired);
        Assert.Equal("sensor_alert_rules", MessageTypes.SensorAlertRules);
        Assert.Equal("sensor_alerts_get", MessageTypes.SensorAlertsGet);
        Assert.Equal("sensor_alert_set", MessageTypes.SensorAlertSet);
        Assert.Equal("sensor_alert_remove", MessageTypes.SensorAlertRemove);
    }

    [Fact]
    public void EveryHostToPhoneSensorAlertTypeRidesTheSensorAlertPrefix()
    {
        var types = MessageAudience.HostToClient
            .Where(e => e.Key.StartsWith("sensor_alert", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, types.Count);
        foreach (var (wire, audience) in types)
        {
            Assert.StartsWith("sensor_alert_", wire, StringComparison.Ordinal);
            Assert.Equal(ClientSurface.AndroidControl, audience);
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static SensorAlertChange Change(
        string name = "CPU Package", double threshold = 90,
        AlertDirection direction = AlertDirection.Above, AlertSeverity severity = AlertSeverity.Warning) =>
        new() { SensorName = name, Threshold = threshold, Direction = direction, Severity = severity };

    [Fact]
    public void AWellFormedSetIsAccepted()
    {
        Assert.Equal(SensorAlertVerdict.Accepted, SensorAlertValidation.Check(Change(), true, false, 0));
        Assert.Equal(SensorAlertVerdict.Accepted, SensorAlertValidation.Check(Change(threshold: -40), true, true, 5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CPU\u0007Temp")]
    public void ABlankOrHostileNameIsMalformed(string name) =>
        Assert.Equal(SensorAlertVerdict.Malformed, SensorAlertValidation.Check(Change(name: name), true, false, 0));

    [Fact]
    public void ATooLongNameIsMalformed() =>
        Assert.Equal(
            SensorAlertVerdict.Malformed, SensorAlertValidation.Check(Change(name: new string('x', 201)), true, false, 0));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e13)]
    [InlineData(-1e13)]
    public void ANonFiniteOrAbsurdThresholdIsMalformed(double threshold) =>
        Assert.Equal(SensorAlertVerdict.Malformed, SensorAlertValidation.Check(Change(threshold: threshold), true, false, 0));

    [Fact]
    public void AnUndefinedDirectionOrSeverityIsMalformed()
    {
        Assert.Equal(
            SensorAlertVerdict.Malformed,
            SensorAlertValidation.Check(Change(direction: (AlertDirection)7), true, false, 0));
        Assert.Equal(
            SensorAlertVerdict.Malformed,
            SensorAlertValidation.Check(Change(severity: (AlertSeverity)(-1)), true, false, 0));
    }

    [Fact]
    public void ANullPayloadIsMalformed() =>
        Assert.Equal(SensorAlertVerdict.Malformed, SensorAlertValidation.Check(null, true, false, 0));

    [Fact]
    public void AnUnknownSensorIsRefusedUnlessItAlreadyHasARule()
    {
        Assert.Equal(SensorAlertVerdict.UnknownSensor, SensorAlertValidation.Check(Change(), false, false, 0));
        Assert.Equal(SensorAlertVerdict.Accepted, SensorAlertValidation.Check(Change(), false, true, 1));
    }

    [Fact]
    public void TheRuleCountIsCappedButAnEditAtTheCapIsStillAllowed()
    {
        Assert.Equal(
            SensorAlertVerdict.TooManyRules,
            SensorAlertValidation.Check(Change(), true, false, SensorAlertValidation.MaxRules));
        Assert.Equal(
            SensorAlertVerdict.Accepted,
            SensorAlertValidation.Check(Change(), true, true, SensorAlertValidation.MaxRules));
        Assert.Equal(
            SensorAlertVerdict.Accepted,
            SensorAlertValidation.Check(Change(), true, false, SensorAlertValidation.MaxRules - 1));
    }

    [Fact]
    public void ARemovalNeedsAUsableName()
    {
        Assert.True(SensorAlertValidation.IsWellFormedRemoval(new SensorAlertRemoval { SensorName = "CPU" }));
        Assert.False(SensorAlertValidation.IsWellFormedRemoval(new SensorAlertRemoval()));
        Assert.False(SensorAlertValidation.IsWellFormedRemoval(null));
    }

    [Fact]
    public void ToRuleCarriesTheChangeIntoThePcRuleUnderTheCanonicalName()
    {
        var rule = SensorAlertValidation.ToRule(
            Change(name: "cpu package", threshold: 71.5, direction: AlertDirection.Below, severity: AlertSeverity.Critical),
            "CPU Package");

        Assert.Equal("CPU Package", rule.SensorName);
        Assert.Equal(71.5, rule.Threshold);
        Assert.Equal(AlertDirection.Below, rule.Direction);
        Assert.Equal(AlertSeverity.Critical, rule.Severity);
    }

    [Fact]
    public void NormalizeRulesDropsBadOnesDedupesAndCaps()
    {
        var good = new SensorAlertRule { SensorName = "CPU", Threshold = 90 };
        var rules = new List<SensorAlertRule?>
        {
            good,
            null,
            new() { SensorName = "cpu", Threshold = 50 },
            new() { SensorName = "  ", Threshold = 1 },
            new() { SensorName = "Bad", Threshold = double.NaN },
            new() { SensorName = "Worse", Threshold = 1, Direction = (AlertDirection)9 },
            new() { SensorName = "Reading", Threshold = 1, CurrentValue = double.NaN },
        };
        for (var i = 0; i < 200; i++)
        {
            rules.Add(new SensorAlertRule { SensorName = $"Sensor {i}", Threshold = i });
        }

        var normalized = SensorAlertValidation.NormalizeRules(rules);

        Assert.Equal(SensorAlertValidation.MaxRules, normalized.Count);
        Assert.Equal(90, normalized[0].Threshold);
        Assert.Single(normalized, r => string.Equals(r.SensorName, "CPU", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(normalized, r => r.SensorName is "Bad" or "Worse" or "  ");
        Assert.Null(normalized.Single(r => r.SensorName == "Reading").CurrentValue);
        Assert.Empty(SensorAlertValidation.NormalizeRules(null));
    }
}
