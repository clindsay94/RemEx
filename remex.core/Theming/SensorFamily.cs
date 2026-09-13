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
    /// The role a card's second series uses so it never shares a colour with series 1. The
    /// candidate is the next family's main role (Neutral: "primary", since its own series 1 is
    /// already "outline"); it is used only if <see cref="IsDistinct"/> from series 1 in HCT,
    /// else "outline", else "onSurface" (terminal — always distinct enough by construction).
    /// Takes the resolved <see cref="MaterialRoles"/> rather than a <c>SchemeVariant</c>:
    /// distinctness is a property of the resolved colours, not the variant name (a variant other
    /// than Monochrome can still collapse two families' tones for an edge-case seed).
    /// </summary>
    public static string SeriesTwoRole(SensorFamily family, MaterialRoles roles)
    {
        var candidate = family == SensorFamily.Neutral ? "primary" : MainRole(Next(family));
        var mainArgb = roles[MainRole(family)];
        if (IsDistinct(mainArgb, roles[candidate])) return candidate;
        if (IsDistinct(mainArgb, roles["outline"])) return "outline";
        return "onSurface";
    }

    /// <summary>
    /// Whether two ARGB colours are visibly distinct in HCT: equal ARGB is never distinct;
    /// otherwise distinct if their tones differ by at least 8, or both have chroma at least 12
    /// and their hues are at least 25° apart (circular distance, 0–180°).
    /// </summary>
    public static bool IsDistinct(uint argbA, uint argbB)
    {
        if (argbA == argbB) return false;

        var a = Hct.FromInt(argbA);
        var b = Hct.FromInt(argbB);

        if (Math.Abs(a.Tone - b.Tone) >= 8) return true;

        if (a.Chroma >= 12 && b.Chroma >= 12)
        {
            var hueDiff = Math.Abs(a.Hue - b.Hue) % 360.0;
            var circularHueDiff = hueDiff > 180.0 ? 360.0 - hueDiff : hueDiff;
            if (circularHueDiff >= 25.0) return true;
        }

        return false;
    }
}
