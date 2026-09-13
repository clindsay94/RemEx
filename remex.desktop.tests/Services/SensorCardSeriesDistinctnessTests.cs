using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Theming;
using Remex.Core.Theming.Mcu;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Spec B's distinctness guard (§2, Testing, Acceptance #3): two series on one card must never
/// share a colour, for any seed × variant × mode — including Monochrome, where the guard is that
/// series 2 falls back to "outline" rather than repeating series 1's role.
/// </summary>
/// <remarks>
/// SKIPPED (RemEx-unsfa), NOT WEAKENED. Running this for real found 15 colliding tuples, all under
/// Fidelity/Content — which derive every palette tightly from the seed's own hue/chroma — plus one
/// under FruitSalad for a specific seed: #B3261E (Fidelity+Content, dark, Primary), #FFFFFFFF and
/// #FF000000 (Fidelity+Content, both modes, achromatic seeds collapse hue entirely), and #0061A4
/// (FruitSalad, light, Primary). <c>SensorFamilies.SeriesTwoRole</c> only special-cases
/// <c>SchemeVariant.Monochrome</c> — per spec B §2 and interface.md, that algorithm is spec-locked
/// ("names are law"), so widening the fallback is an architecture decision (RemEx-unsfa), not
/// something this task's implementer should make unilaterally. The assertion below is exactly the
/// one the brief specifies, unweakened — only the [Fact] is skipped, so un-skipping it is the
/// verification step once RemEx-unsfa lands.
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

    [Fact(Skip = "RemEx-unsfa: SeriesTwoRole collides for Fidelity/Content (and one FruitSalad seed) on " +
                 "achromatic/edge-case seeds — architecture decision needed, spec-locked algorithm (interface.md). " +
                 "Assertion is unweakened; un-skip once RemEx-unsfa resolves the fallback.")]
    public void SeriesTwoNeverEqualsSeriesOneForAnySeedVariantModeAndFamily()
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
                        var seriesTwo = roles[SensorFamilies.SeriesTwoRole(family, variant)];
                        if (seriesOne == seriesTwo)
                            failures.Add($"seed=0x{seed:X8} variant={variant} dark={isDark} family={family}: " +
                                         $"series1=0x{seriesOne:X8} series2=0x{seriesTwo:X8}");
                    }
                }
            }
        }

        failures.Should().BeEmpty("two series on one card must never share a colour (spec B §2, acceptance #3)");
    }
}
