using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Theming;
using Remex.Core.Theming.Mcu;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Spec B's distinctness guard (§2, Testing, Acceptance #3): two series on one card must never
/// be visibly indistinguishable, for any seed × variant × mode.
/// </summary>
/// <remarks>
/// RemEx-unsfa RESOLVED (fix round 1): distinctness is a property of the resolved colours, not
/// the variant name. <c>SeriesTwoRole</c> no longer takes a <c>SchemeVariant</c> — it tries the
/// next family's main role, falls back to "outline", then "onSurface" (terminal), checking
/// <c>SensorFamilies.IsDistinct</c> (HCT tone/hue distance) at each step. That closes every
/// collision the previous variant-name-based special case (Monochrome only) missed — Fidelity,
/// Content and the one FruitSalad seed included — without hard-coding any of them. Guard is
/// un-skipped; the comparison is <c>IsDistinct</c>, not raw ARGB inequality, because "distinct"
/// is defined perceptually (tone/hue distance), not as "any bit differs".
/// </remarks>
public class SensorCardSeriesDistinctnessTests
{
    private static readonly uint[] GridSeeds =
    {
        0xFF6750A4, 0xFF386A20, 0xFFB3261E, 0xFF0061A4, 0xFF7D5260, 0xFFFFFFFF, 0xFF000000, 0xFF808080,
    };

    private static readonly SchemeVariant[] Variants =
    {
        SchemeVariant.Monochrome, SchemeVariant.Neutral, SchemeVariant.TonalSpot, SchemeVariant.Vibrant, SchemeVariant.Expressive,
        SchemeVariant.Fidelity, SchemeVariant.Content, SchemeVariant.Rainbow, SchemeVariant.FruitSalad,
    };

    private static readonly SensorFamily[] Families =
    {
        SensorFamily.Primary, SensorFamily.Secondary, SensorFamily.Tertiary, SensorFamily.Neutral,
    };

    [Fact]
    public void SeriesTwoIsAlwaysDistinctFromSeriesOneForAnySeedVariantModeAndFamily()
    {
        var failures = new List<string>();

        foreach (var seed in GridSeeds)
        {
            foreach (var variant in Variants)
            {
                foreach (var isDark in new[] { false, true })
                {
                    var roles = McuScheme.Build(seed, variant, isDark, 0.0);
                    foreach (var family in Families)
                    {
                        var seriesOne = roles[SensorFamilies.MainRole(family)];
                        var seriesTwoRole = SensorFamilies.SeriesTwoRole(family, roles);
                        var seriesTwo = roles[seriesTwoRole];
                        if (!SensorFamilies.IsDistinct(seriesOne, seriesTwo))
                            failures.Add($"seed=0x{seed:X8} variant={variant} dark={isDark} family={family} role={seriesTwoRole}: " +
                                         $"series1=0x{seriesOne:X8} series2=0x{seriesTwo:X8}");
                    }
                }
            }
        }

        failures.Should().BeEmpty("two series on one card must never be visibly indistinguishable (spec B §2, acceptance #3)");
    }
}
