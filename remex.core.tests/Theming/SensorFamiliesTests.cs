using Remex.Core.Messages;
using Remex.Core.Theming;
using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>Spec B §1–2 (RemEx-4kv0g.3.1): category → family, component → tone for sensor cards.</summary>
public class SensorFamiliesTests
{
    [Fact]
    public void EveryMetricKindHasAFamily()
    {
        foreach (MetricKind k in Enum.GetValues<MetricKind>())
            Assert.True(Enum.IsDefined(SensorFamilies.For(k)));
    }

    [Theory]
    [InlineData(MetricKind.CpuLoad, SensorFamily.Primary)]
    [InlineData(MetricKind.GpuLoad, SensorFamily.Tertiary)]
    [InlineData(MetricKind.RamLoad, SensorFamily.Secondary)]
    [InlineData(MetricKind.RamUsedGb, SensorFamily.Secondary)]
    [InlineData(MetricKind.RamTotalGb, SensorFamily.Secondary)]
    [InlineData(MetricKind.CpuTempC, SensorFamily.Primary)]
    [InlineData(MetricKind.GpuTempC, SensorFamily.Tertiary)]
    [InlineData(MetricKind.TempC, SensorFamily.Tertiary)]
    [InlineData(MetricKind.ClockMhz, SensorFamily.Tertiary)]
    [InlineData(MetricKind.PowerW, SensorFamily.Tertiary)]
    [InlineData(MetricKind.FanRpm, SensorFamily.Tertiary)]
    [InlineData(MetricKind.NetThroughputMbps, SensorFamily.Primary)]
    [InlineData(MetricKind.NetDownMbps, SensorFamily.Primary)]
    [InlineData(MetricKind.NetUpMbps, SensorFamily.Primary)]
    [InlineData(MetricKind.VoltageV, SensorFamily.Tertiary)]
    [InlineData(MetricKind.DiskRateMBs, SensorFamily.Secondary)]
    [InlineData(MetricKind.Unknown, SensorFamily.Neutral)]
    public void TheSpecTableIsWhatShips(MetricKind kind, SensorFamily expected)
    {
        Assert.Equal(expected, SensorFamilies.For(kind));
    }

    [Fact]
    public void NextIsAThreeCycleAndNeutralJoinsAtPrimary()
    {
        Assert.Equal(SensorFamily.Secondary, SensorFamilies.Next(SensorFamily.Primary));
        Assert.Equal(SensorFamily.Tertiary, SensorFamilies.Next(SensorFamily.Secondary));
        Assert.Equal(SensorFamily.Primary, SensorFamilies.Next(SensorFamily.Tertiary));
        Assert.Equal(SensorFamily.Primary, SensorFamilies.Next(SensorFamily.Neutral));
    }

    private static readonly SensorFamily[] AllFamilies =
    {
        SensorFamily.Primary, SensorFamily.Secondary, SensorFamily.Tertiary, SensorFamily.Neutral,
    };

    private static readonly uint[] GridSeeds =
    {
        0xFF6750A4, 0xFF386A20, 0xFFB3261E, 0xFF0061A4, 0xFF7D5260, 0xFFFFFFFF, 0xFF000000, 0xFF808080,
    };

    private static readonly SchemeVariant[] AllVariants =
    {
        SchemeVariant.Monochrome, SchemeVariant.Neutral, SchemeVariant.TonalSpot, SchemeVariant.Vibrant, SchemeVariant.Expressive,
        SchemeVariant.Fidelity, SchemeVariant.Content, SchemeVariant.Rainbow, SchemeVariant.FruitSalad,
    };

    // ── IsDistinct, pinned on hand-picked pairs (spec B Testing) ────────────────────────────

    [Fact]
    public void IsDistinct_EqualArgbIsNeverDistinct()
    {
        Assert.False(SensorFamilies.IsDistinct(0xFF6750A4, 0xFF6750A4));
    }

    [Fact]
    public void IsDistinct_TonesEightOrMoreApartAreDistinct()
    {
        var grey40 = Hct.From(0, 0, 40).ToInt();
        var grey80 = Hct.From(0, 0, 80).ToInt();
        Assert.True(SensorFamilies.IsDistinct(grey40, grey80));
    }

    [Fact]
    public void IsDistinct_SameToneHighChromaNinetyDegreesApartIsDistinct()
    {
        var a = Hct.From(0, 40, 50).ToInt();
        var b = Hct.From(90, 40, 50).ToInt();
        Assert.True(SensorFamilies.IsDistinct(a, b));
    }

    [Fact]
    public void IsDistinct_SameToneLowChromaOppositeHuesIsNotDistinct()
    {
        var a = Hct.From(0, 4, 50).ToInt();
        var b = Hct.From(180, 4, 50).ToInt();
        Assert.False(SensorFamilies.IsDistinct(a, b));
    }

    // ── SeriesTwoRole, resolved against real palettes ───────────────────────────────────────

    [Fact]
    public void SeriesTwoRoleIsAlwaysDistinctFromSeriesOne_ForEveryFamilyAndGridTuple()
    {
        foreach (var seed in GridSeeds)
        {
            foreach (var variant in AllVariants)
            {
                foreach (var isDark in new[] { false, true })
                {
                    var roles = McuScheme.Build(seed, variant, isDark, 0.0);
                    foreach (var family in AllFamilies)
                    {
                        var seriesOne = roles[SensorFamilies.MainRole(family)];
                        var seriesTwoRole = SensorFamilies.SeriesTwoRole(family, roles);
                        var seriesTwo = roles[seriesTwoRole];
                        Assert.True(SensorFamilies.IsDistinct(seriesOne, seriesTwo),
                            $"seed=0x{seed:X8} variant={variant} dark={isDark} family={family} role={seriesTwoRole}: " +
                            $"series1=0x{seriesOne:X8} series2=0x{seriesTwo:X8}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Verified against the real MCU port, not assumed: under TonalSpot, "#6750A4"'s primary and
    /// secondary share the source hue at the same tone (only chroma differs), so the plain
    /// candidate ("secondary" for Primary) is NOT distinct and falls through to "outline" (tone
    /// 50 vs primary's tone 40 clears the 8-point threshold). Neutral's candidate ("primary") IS
    /// already distinct from "outline" (its own series 1), so it needs no fallback.
    /// </summary>
    [Fact]
    public void SeriesTwoRoleFallsThroughWhenThePlainCandidateIsNotDistinct()
    {
        var roles = McuScheme.Build(0xFF6750A4, SchemeVariant.TonalSpot, isDark: false, contrastLevel: 0.0);
        Assert.Equal("outline", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, roles));
        Assert.Equal("primary", SensorFamilies.SeriesTwoRole(SensorFamily.Neutral, roles));
    }

    /// <summary>
    /// Verified against the real MCU port, not assumed: MaterialDynamicColors forces Monochrome's
    /// Primary role to tone 0 in light mode (pure black) regardless of seed, while Secondary sits
    /// around tone 40 — a ~40-point tone gap, which clears the distinctness threshold on its own.
    /// So the plain candidate ("secondary") is already distinct; Monochrome needs no special case
    /// any more; distinctness is a property of the resolved colours, not the variant name.
    /// </summary>
    [Theory]
    [InlineData(0xFF6750A4)]
    [InlineData(0xFF386A20)]
    [InlineData(0xFFB3261E)]
    [InlineData(0xFF0061A4)]
    [InlineData(0xFF7D5260)]
    [InlineData(0xFFFFFFFF)]
    [InlineData(0xFF000000)]
    [InlineData(0xFF808080)]
    public void MonochromeResolvesToThePlainCandidateBecauseItsPrimaryIsForcedToToneZero(uint seed)
    {
        var roles = McuScheme.Build(seed, SchemeVariant.Monochrome, isDark: false, contrastLevel: 0.0);
        Assert.Equal("secondary", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, roles));
    }

    [Fact]
    public void EveryRoleNameIsAMaterialRole()
    {
        var roleNames = new HashSet<string>(MaterialRoles.RoleNames);
        Assert.Contains("onSurface", roleNames);

        foreach (var f in AllFamilies)
            Assert.Contains(SensorFamilies.MainRole(f), roleNames);

        foreach (var seed in GridSeeds)
        {
            foreach (var variant in AllVariants)
            {
                foreach (var isDark in new[] { false, true })
                {
                    var roles = McuScheme.Build(seed, variant, isDark, 0.0);
                    foreach (var f in AllFamilies)
                        Assert.Contains(SensorFamilies.SeriesTwoRole(f, roles), roleNames);
                }
            }
        }
    }
}
