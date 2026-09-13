using Remex.Core.Messages;
using Remex.Core.Theming.Mcu;

namespace Remex.Core.Theming;

/// <summary>The palette family a sensor card's category renders in (Colour epic, spec B §1).</summary>
public enum SensorFamily
{
    Primary,
    Secondary,
    Tertiary,
    Neutral,
}

/// <summary>Category → palette family and component → tone for sensor cards (spec B §1–2).</summary>
public static class SensorFamilies
{
    /// <summary>
    /// The family a <see cref="MetricKind"/> renders in (spec B §1 table). Every named member is
    /// matched explicitly, no default arm: a new <see cref="MetricKind"/> without a row here falls
    /// into the trailing discard and throws, which fails <c>SensorFamiliesTests.EveryMetricKindHasAFamily</c>
    /// (the enforcement point — a discard-free switch over an enum's full underlying range does not
    /// compile under this repo's <c>TreatWarningsAsErrors</c> regardless of named-member coverage,
    /// so the discard is required, not a design choice).
    /// </summary>
    public static SensorFamily For(MetricKind kind) => kind switch
    {
        MetricKind.CpuLoad or MetricKind.CpuTempC => SensorFamily.Primary,
        MetricKind.RamLoad or MetricKind.RamUsedGb or MetricKind.RamTotalGb => SensorFamily.Secondary,
        MetricKind.GpuLoad or MetricKind.GpuTempC => SensorFamily.Tertiary,
        MetricKind.NetThroughputMbps or MetricKind.NetDownMbps or MetricKind.NetUpMbps => SensorFamily.Primary,
        MetricKind.DiskRateMBs => SensorFamily.Secondary,
        MetricKind.PowerW or MetricKind.VoltageV or MetricKind.FanRpm or MetricKind.TempC or MetricKind.ClockMhz => SensorFamily.Tertiary,
        MetricKind.Unknown => SensorFamily.Neutral,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled MetricKind — add a SensorFamily row (spec B §1)."),
    };

    /// <summary>Primary→Secondary→Tertiary→Primary; Neutral joins the cycle at Primary.</summary>
    public static SensorFamily Next(SensorFamily family) => family switch
    {
        SensorFamily.Primary => SensorFamily.Secondary,
        SensorFamily.Secondary => SensorFamily.Tertiary,
        SensorFamily.Tertiary => SensorFamily.Primary,
        SensorFamily.Neutral => SensorFamily.Primary,
        _ => SensorFamily.Primary,
    };

    /// <summary>The M3 role name (<see cref="MaterialRoles.RoleNames"/> spelling) a family's series 1 / main tone uses.</summary>
    public static string MainRole(SensorFamily family) => family switch
    {
        SensorFamily.Primary => "primary",
        SensorFamily.Secondary => "secondary",
        SensorFamily.Tertiary => "tertiary",
        _ => "outline",
    };

    /// <summary>
    /// The role a card's second series uses so it never shares a hue with series 1: the next
    /// family's main role, collapsed to "outline" under Monochrome (all three colour families are
    /// one grey there) and to "primary" for Neutral (series 1 is already "outline").
    /// </summary>
    public static string SeriesTwoRole(SensorFamily family, SchemeVariant variant)
    {
        if (family == SensorFamily.Neutral) return "primary";
        if (variant == SchemeVariant.Monochrome) return "outline";
        return MainRole(Next(family));
    }
}
