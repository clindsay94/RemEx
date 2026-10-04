using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// The wire records for the PC's sensor alerts on the phone (RemEx-pp4cm.12): one rule as the phone
/// sees it, the list of them, a rule firing, and the phone's two edits. The rules themselves are the
/// PC's <see cref="SensorAlert"/>s; nothing here is a second rule model.
/// </summary>
/// <remarks>
/// <para>
/// **THE PC OWNS EVERY RULE.** <c>DashboardProfile.SensorAlerts</c> is the only list, evaluated by the
/// desktop's one <c>SensorAlertTracker</c>. The phone shows what <see cref="SensorAlertRules"/> says,
/// asks for one change at a time with <see cref="SensorAlertChange"/> / <see cref="SensorAlertRemoval"/>,
/// and hears the result as the next <see cref="SensorAlertRules"/>. It never evaluates anything.
/// </para>
/// <para>
/// **DIRECTION AND SEVERITY TRAVEL AS NAMES, NOT NUMBERS.** The two enums are persisted by number in
/// the dashboard profile, so they are not given a string converter themselves (that would change the
/// profile on disk). The converter sits on each wire PROPERTY instead, so the phone reads and writes
/// <c>"Above"</c> / <c>"Critical"</c> and a reordered enum can never silently flip a rule's meaning on
/// the other side. A name that is not one of them fails the lenient slot reader, which nulls the slot
/// and keeps the envelope.
/// </para>
/// <para>
/// NOTHING HERE IS <c>required</c>, and every member has a default, for the reason written on
/// <see cref="PhoneThemeSnapshot"/>.
/// </para>
/// </remarks>
public sealed record SensorAlertRule
{
    /// <summary>The sensor's name (<c>SensorReading.Name</c>), compared case-insensitively.</summary>
    public string SensorName { get; init; } = string.Empty;

    /// <summary>What the PC calls the sensor on its own cards. Falls back to <see cref="SensorName"/>.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The sensor's unit, when the PC knows it (<c>°C</c>, <c>%</c>, <c>RPM</c>...).</summary>
    public string? Unit { get; init; }

    /// <summary>The sensor's latest reading, when the PC has one. Absent for a sensor that is not reporting.</summary>
    public double? CurrentValue { get; init; }

    /// <summary>The number the reading is compared with.</summary>
    public double Threshold { get; init; }

    /// <summary>Whether the reading must rise above or fall below <see cref="Threshold"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AlertDirection>))]
    public AlertDirection Direction { get; init; }

    /// <summary>How urgently a firing is flagged.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
    public AlertSeverity Severity { get; init; }
}

/// <summary>The PC's rules, host → phone, as <c>sensor_alert_rules</c>.</summary>
public sealed record SensorAlertRules
{
    /// <summary>Every rule, in no meaningful order. The whole list: the phone replaces its own with it.</summary>
    public List<SensorAlertRule> Rules { get; init; } = [];

    /// <summary>Monotonic per host process. The phone drops a list older than the last one it took on a connection.</summary>
    public long Revision { get; init; }

    /// <summary>When the host built the list. For display only; never for ordering.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>A rule firing, host → phone, as <c>sensor_alert_fired</c>.</summary>
/// <remarks>
/// Sent only when the PC's own tracker decides to notify, so it carries the tracker's 60 second
/// per-sensor cooldown: the phone gets exactly the alerts the PC raises, no more.
/// </remarks>
public sealed record SensorAlertFiredEvent
{
    /// <summary>The sensor's name (<c>SensorReading.Name</c>).</summary>
    public string SensorName { get; init; } = string.Empty;

    /// <summary>What the PC calls the sensor on its own cards.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The reading that crossed the threshold.</summary>
    public double Value { get; init; }

    /// <summary>The sensor's unit, when the PC knows it.</summary>
    public string? Unit { get; init; }

    /// <summary>The threshold that was crossed.</summary>
    public double Threshold { get; init; }

    /// <summary>The direction of the rule that fired.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AlertDirection>))]
    public AlertDirection Direction { get; init; }

    /// <summary>The severity of the rule that fired.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
    public AlertSeverity Severity { get; init; }

    /// <summary>When the PC raised the alert.</summary>
    public DateTimeOffset FiredAtUtc { get; init; }
}

/// <summary>One phone-requested rule, phone → host, as <c>sensor_alert_set</c>: add it, or replace the sensor's existing rule.</summary>
public sealed record SensorAlertChange
{
    /// <summary>The sensor's name (<c>SensorReading.Name</c>). The PC resolves it to a sensor it knows.</summary>
    public string SensorName { get; init; } = string.Empty;

    /// <summary>The number the reading is compared with. Must be finite.</summary>
    public double Threshold { get; init; }

    /// <summary>Whether the reading must rise above or fall below <see cref="Threshold"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AlertDirection>))]
    public AlertDirection Direction { get; init; }

    /// <summary>How urgently a firing is flagged.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
    public AlertSeverity Severity { get; init; }
}

/// <summary>One phone-requested removal, phone → host, as <c>sensor_alert_remove</c>.</summary>
public sealed record SensorAlertRemoval
{
    /// <summary>The sensor whose rule to remove (<c>SensorReading.Name</c>).</summary>
    public string SensorName { get; init; } = string.Empty;
}
