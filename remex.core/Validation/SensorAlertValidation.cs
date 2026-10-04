using Remex.Core.Models;

namespace Remex.Core.Validation;

/// <summary>What the host decided about a phone's <c>sensor_alert_set</c>.</summary>
public enum SensorAlertVerdict
{
    /// <summary>Apply it.</summary>
    Accepted,

    /// <summary>A missing payload, a blank or hostile sensor name, a threshold that is not a finite number, or an unknown direction or severity.</summary>
    Malformed,

    /// <summary>The PC does not know a sensor by that name, so a rule on it could never fire.</summary>
    UnknownSensor,

    /// <summary>The PC already holds <see cref="SensorAlertValidation.MaxRules"/> rules and this would add another.</summary>
    TooManyRules,
}

/// <summary>
/// The one set of rules the host applies to a phone's alert edits (RemEx-pp4cm.12), and the one
/// normalization it applies to the rule list it sends back.
/// </summary>
/// <remarks>
/// <para>
/// Split in two on purpose. <see cref="IsWellFormedChange"/> needs nothing but the payload, so the
/// connection handler can refuse bad input at the door. <see cref="Check"/> adds what only the PC
/// knows — which sensors exist and how many rules it holds — and runs on the desktop, where that
/// state lives. A refusal at either point is answered by resending the unchanged rules, which is what
/// puts the phone's optimistic edit back.
/// </para>
/// <para>
/// NativeAOT-safe: no reflection, no regex.
/// </para>
/// </remarks>
public static class SensorAlertValidation
{
    /// <summary>
    /// Most rules the PC will hold. A rule is one per sensor, and a PC reports a few hundred sensors at
    /// most; 100 leaves room for every one anyone would alert on and keeps a hostile phone from growing
    /// the profile file without bound.
    /// </summary>
    public const int MaxRules = 100;

    /// <summary>
    /// Largest threshold magnitude accepted. No sensor reads anywhere near it; it exists so a value
    /// like 1e308 cannot reach the PC's number formatting.
    /// </summary>
    public const double MaxThresholdMagnitude = 1e12;

    /// <summary>Whether <paramref name="direction"/> is one of the two the PC evaluates.</summary>
    public static bool IsValidDirection(AlertDirection direction) =>
        direction is AlertDirection.Above or AlertDirection.Below;

    /// <summary>Whether <paramref name="severity"/> is one of the two the PC raises.</summary>
    public static bool IsValidSeverity(AlertSeverity severity) =>
        severity is AlertSeverity.Warning or AlertSeverity.Critical;

    /// <summary>Whether <paramref name="threshold"/> is a finite number within <see cref="MaxThresholdMagnitude"/>.</summary>
    public static bool IsValidThreshold(double threshold) =>
        double.IsFinite(threshold) && Math.Abs(threshold) <= MaxThresholdMagnitude;

    /// <summary>Everything about a set request that can be judged without asking the PC anything.</summary>
    public static bool IsWellFormedChange(SensorAlertChange? change) =>
        change is not null
        && HomePinsValidation.IsValidSensorName(change.SensorName)
        && IsValidThreshold(change.Threshold)
        && IsValidDirection(change.Direction)
        && IsValidSeverity(change.Severity);

    /// <summary>Whether a removal names a sensor at all.</summary>
    public static bool IsWellFormedRemoval(SensorAlertRemoval? removal) =>
        removal is not null && HomePinsValidation.IsValidSensorName(removal.SensorName);

    /// <summary>
    /// The full verdict on a set request.
    /// </summary>
    /// <param name="change">The phone's request.</param>
    /// <param name="isKnownSensor">Whether the PC knows a sensor by <paramref name="change"/>'s name.</param>
    /// <param name="replacesExisting">Whether the PC already holds a rule for that sensor (an edit, not an add).</param>
    /// <param name="ruleCount">How many rules the PC holds now.</param>
    public static SensorAlertVerdict Check(
        SensorAlertChange? change, bool isKnownSensor, bool replacesExisting, int ruleCount)
    {
        if (!IsWellFormedChange(change))
        {
            return SensorAlertVerdict.Malformed;
        }

        // A sensor that already has a rule is known by definition: the PC may have lost sight of it
        // (a disconnected sensor), and the phone must still be able to edit or remove its rule.
        if (!isKnownSensor && !replacesExisting)
        {
            return SensorAlertVerdict.UnknownSensor;
        }

        if (!replacesExisting && ruleCount >= MaxRules)
        {
            return SensorAlertVerdict.TooManyRules;
        }

        return SensorAlertVerdict.Accepted;
    }

    /// <summary>The PC's own rule for an accepted change.</summary>
    public static SensorAlert ToRule(SensorAlertChange change, string canonicalSensorName) => new()
    {
        SensorName = canonicalSensorName,
        Threshold = change.Threshold,
        Direction = change.Direction,
        Severity = change.Severity,
    };

    /// <summary>
    /// The list as the phone may rely on it: a rule with a bad name, threshold or enum dropped,
    /// duplicates removed case-insensitively keeping the first, and at most <see cref="MaxRules"/>.
    /// Null (an absent JSON list) reads as empty.
    /// </summary>
    public static List<SensorAlertRule> NormalizeRules(IEnumerable<SensorAlertRule?>? rules)
    {
        var result = new List<SensorAlertRule>();
        if (rules is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (result.Count == MaxRules)
            {
                break;
            }

            if (rule is not null
                && HomePinsValidation.IsValidSensorName(rule.SensorName)
                && IsValidThreshold(rule.Threshold)
                && IsValidDirection(rule.Direction)
                && IsValidSeverity(rule.Severity)
                && seen.Add(rule.SensorName))
            {
                // A reading that is not a finite number cannot be written as JSON at all, so it is
                // sent as "no reading" rather than failing the whole list.
                result.Add(rule.CurrentValue is { } v && !double.IsFinite(v) ? rule with { CurrentValue = null } : rule);
            }
        }

        return result;
    }
}
