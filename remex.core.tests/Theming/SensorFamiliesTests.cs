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
    /// Verified against the real MCU port, not assumed (fix round 2): under TonalSpot, "#6750A4"'s
    /// primary (hue 299.2°, chroma 36.2, tone 40.0) and secondary (hue 299.6°, chroma 16.3, tone
    /// 40.0) share the source hue at the same tone — the first candidate is NOT distinct — so the
    /// search moves to the second candidate, tertiary (hue 359.3°, chroma 24.1, tone 40.1): ~60°
    /// apart from primary at chroma ≥ 12 on both sides, which clears the 25° threshold and wins.
    /// Tonal Spot's dual cards now get an actual colour instead of falling through to grey.
    /// Neutral's first candidate ("primary") is already distinct from "outline" (its own series
    /// 1), so it needs no fallback.
    /// </summary>
    [Fact]
    public void SeriesTwoRoleTriesTheSecondColourFamilyBeforeGreyFallbacks()
    {
        var roles = McuScheme.Build(0xFF6750A4, SchemeVariant.TonalSpot, isDark: false, contrastLevel: 0.0);
        Assert.Equal("tertiary", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, roles));
        Assert.Equal("primary", SensorFamilies.SeriesTwoRole(SensorFamily.Neutral, roles));
    }

    /// <summary>
    /// Verified against the real MCU port: under Expressive, "#0061A4"'s primary and secondary are
    /// already distinct (hue 134.0° vs 344.2°, ~150° apart at chroma ≥ 12 both sides), so the
    /// first candidate wins without needing the second.
    /// </summary>
    [Fact]
    public void SeriesTwoRoleReturnsTheFirstCandidateWhenItIsAlreadyDistinct()
    {
        var roles = McuScheme.Build(0xFF0061A4, SchemeVariant.Expressive, isDark: false, contrastLevel: 0.0);
        Assert.Equal("secondary", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, roles));
    }

    /// <summary>
    /// Verified against the real MCU port, not assumed: MaterialDynamicColors forces Monochrome's
    /// Primary role to tone 0 in light mode (pure black) regardless of seed, while Secondary sits
    /// around tone 40 — a ~40-point tone gap, which clears the distinctness threshold on its own.
    /// So the first candidate ("secondary") is already distinct; Monochrome needs no special case
    /// any more (unchanged by fix round 2 — the first candidate is still tried first and still
    /// wins here); distinctness is a property of the resolved colours, not the variant name.
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
    public void MonochromeResolvesToTheFirstCandidateBecauseItsPrimaryIsForcedToToneZero(uint seed)
    {
        var roles = McuScheme.Build(seed, SchemeVariant.Monochrome, isDark: false, contrastLevel: 0.0);
        Assert.Equal("secondary", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, roles));
    }

    /// <summary>
    /// The candidate ORDER itself, isolated from any real palette: a hand-built <see cref="MaterialRoles"/>
    /// where the first colour candidate collides but the second doesn't must pick the second, not
    /// jump straight to "outline"; only when both colour candidates collide does "outline" win.
    /// </summary>
    [Fact]
    public void SeriesTwoRoleHonoursTheCandidateOrder()
    {
        var primary = Hct.From(0, 40, 40).ToInt();
        var distinctTertiary = Hct.From(0, 40, 50).ToInt();   // tone diff 10 from primary: distinct
        var distinctOutline = Hct.From(0, 0, 60).ToInt();     // tone diff 20 from primary: distinct

        var secondCandidateWins = new MaterialRoles
        {
            Primary = primary,
            Secondary = primary,           // collides: same ARGB as primary
            Tertiary = distinctTertiary,   // distinct: should be picked
            Outline = distinctOutline,
        };
        Assert.Equal("tertiary", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, secondCandidateWins));

        var bothColourCandidatesCollide = new MaterialRoles
        {
            Primary = primary,
            Secondary = primary,   // collides
            Tertiary = primary,    // collides too
            Outline = distinctOutline,
        };
        Assert.Equal("outline", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, bothColourCandidatesCollide));
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
