using System;
using System.Collections.Generic;
using Avalonia.Media;
using MaterialColorUtilities.ColorAppearance;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The shell backdrop's three gradient stops, held to being a gradient a person can actually see.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS A TEST AND NOT A SCREENSHOT. <c>RemEx-bv9bu</c> shipped a "gradient" whose first two
/// stops were two tone steps apart and whose last two were both near-zero-chroma neutrals — flat
/// black with one faintly tinted corner. Nothing was red: every palette test in the suite measured
/// foreground/background PAIRS, and the three stops of a background wash are not a pair. A backdrop
/// that has quietly collapsed onto one colour is invisible to assertions about contrast ratios and
/// invisible to a reviewer reading a diff of tone indices, which is the exact combination that lets
/// it happen again.
/// </para>
/// <para>
/// WHAT "PERCEPTIBLE" IS PINNED TO. Two independent axes, because a seed can lose either one. Tone
/// is the axis that always exists — <c>Style.Content</c> on an achromatic seed produces palettes
/// with a chroma of about 1, so there is no hue there to sweep and separation has to come from tone
/// alone. Hue is the axis the hand-authored backdrop this replaced actually used: #1A0A2E → #0D1B2A
/// → #000000 reads as violet-to-navy-to-black, and all three of those sit near tone 8, so it was
/// never a tone sweep. Both are asserted, each over the inputs where it is meaningful.
/// </para>
/// </remarks>
public class BackgroundGradientTests
{
    /// <summary>The same adversarial seeds <see cref="SeedPaletteTests"/> uses, for the same reason.</summary>
    private static readonly Color[] Seeds =
    {
        Color.Parse("#6C4CFF"),   // the shipped default
        Color.Parse("#00E5FF"),   // CyberNOC-ish cyan
        Color.Parse("#F59E0B"),   // amber — a light seed
        Color.Parse("#FFFF00"),   // pure yellow
        Color.Parse("#FFFFFF"),   // white — zero chroma, maximum tone
        Color.Parse("#000000"),   // black — zero chroma, minimum tone
        Color.Parse("#8B0000"),   // dark red
    };

    private static readonly string[] Variants =
    {
        "TonalSpot", "Vibrant", "Expressive", "Rainbow", "FruitSalad", "Content", "Spritz",
    };

    /// <summary>
    /// The two stacked gradient layers in <c>DashboardBackgroundControl.axaml</c>: a base rectangle
    /// at 0.8 opacity with an identically-filled rectangle pulsing 0.7–0.9 on top of it. Worst case
    /// — the trough of the pulse — the pair covers what is behind them by this much.
    /// </summary>
    private const double WorstCaseShellCoverage = 1.0 - (1.0 - 0.8) * (1.0 - 0.7);

    /// <summary>
    /// Minimum separation, in 8-bit channel units, that has to survive the shell's compositing.
    /// Compositing two layers of one brush over a common backdrop scales every stop by the same
    /// factor, so the distance between stops after the shell is exactly
    /// <see cref="WorstCaseShellCoverage"/> times the distance before it, whatever is underneath.
    /// </summary>
    private const double MinCompositedChannelSeparation = 12.0;

    /// <summary>Tone steps required between consecutive stops. The generator aims for ~9 in light and ~10 in dark.</summary>
    private const double MinToneStep = 8.0;

    /// <summary>Tone steps required across the whole sweep, first stop to last.</summary>
    private const double MinToneSpread = 16.0;

    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Hct Hct_(Color c) => Hct.FromInt(Argb(c));

    private static IEnumerable<(string Name, Color From, Color To)> ConsecutivePairs(
        DynamicColorGenerator.M3Palette p)
    {
        yield return ("Start→Mid", p.BackgroundStart, p.BackgroundMid);
        yield return ("Mid→End", p.BackgroundMid, p.BackgroundEnd);
    }

    [Fact]
    public void ConsecutiveGradientStopsAreSeparatedInTone()
    {
        // THE ASSERTION THAT WOULD HAVE CAUGHT THE ORIGINAL DEFECT. Primary[10] → Neutral[8] is a
        // step of two, which is inside the rounding noise of the tonal scale — the two stops render
        // as the same colour. Demanded of every seed and every variant, because a wash that
        // separates for the shipped default and collapses for the next colour out of the wheel is
        // still a broken backdrop for the user who picked that colour.
        var failures = new List<string>();

        foreach (var seed in Seeds)
        foreach (var variant in Variants)
        foreach (var dark in new[] { true, false })
        {
            var p = DynamicColorGenerator.Generate(seed, variant, dark);
            var where = $"{seed} {variant} {(dark ? "dark" : "light")}";

            foreach (var (name, from, to) in ConsecutivePairs(p))
            {
                double step = Math.Abs(Hct_(from).Tone - Hct_(to).Tone);
                if (step < MinToneStep) failures.Add($"{where} {name}: {step:F1} tone steps");
            }

            double spread = Math.Abs(Hct_(p.BackgroundStart).Tone - Hct_(p.BackgroundEnd).Tone);
            if (spread < MinToneSpread) failures.Add($"{where} Start→End spread: {spread:F1} tone steps");
        }

        failures.Should().BeEmpty("a background gradient whose stops share a tone is a solid fill");
    }

    [Fact]
    public void TheSweepChangesHueAndNotOnlyBrightness()
    {
        // WHAT THE HAND-AUTHORED BACKDROP DID THAT THE FIRST GENERATED ONE DROPPED. Taking Mid off
        // the neutral palette is what buys this: neutral is by definition the greyest palette the
        // seed produces, so a Primary→Neutral→Neutral sweep can only ever change brightness.
        //
        // Asserted on TonalSpot, the default and the variant the four shipped themes correspond to.
        // The other six are deliberately out of scope: Content and Spritz exist precisely to hold
        // one hue (Content keeps the seed's own hue on every palette, Spritz drains chroma to near
        // zero), so demanding a hue sweep of them would be demanding they stop being themselves.
        // Their backdrops are carried by the tone assertion above.
        var failures = new List<string>();

        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        {
            var p = DynamicColorGenerator.Generate(seed, "TonalSpot", dark);
            var start = Hct_(p.BackgroundStart);
            var mid = Hct_(p.BackgroundMid);
            var where = $"{seed} {(dark ? "dark" : "light")}";

            // Hue is meaningless on a colour with no chroma to carry it, so only claim it where
            // there is some. Every TonalSpot stop clears this comfortably; the guard is here so the
            // test says "not applicable" rather than something false if that ever stops being true.
            if (start.Chroma < 8 || mid.Chroma < 8)
            {
                failures.Add($"{where}: stops lost their chroma (start {start.Chroma:F1}, mid {mid.Chroma:F1})");
                continue;
            }

            double hueDelta = HueDelta(start.Hue, mid.Hue);
            if (hueDelta < 30) failures.Add($"{where} Start→Mid hue: {hueDelta:F0}°");
        }

        failures.Should().BeEmpty("the backdrop is supposed to sweep colour, not just brightness");
    }

    [Fact]
    public void TheSeparationSurvivesTheShellsTwoGradientLayers()
    {
        // ANTI-VACUITY FOR BOTH TESTS ABOVE, IN THE UNITS THE SCREEN ACTUALLY WORKS IN. Tone and
        // hue are perceptual coordinates; what reaches the framebuffer is eight bits per channel
        // through two semi-transparent rectangles. A pair of stops can be a respectable ten tones
        // apart down at the black end and still land on adjacent byte values.
        var failures = new List<string>();

        foreach (var seed in Seeds)
        foreach (var variant in Variants)
        foreach (var dark in new[] { true, false })
        {
            var p = DynamicColorGenerator.Generate(seed, variant, dark);
            var where = $"{seed} {variant} {(dark ? "dark" : "light")}";

            foreach (var (name, from, to) in ConsecutivePairs(p))
            {
                double separation = WorstCaseShellCoverage * MaxChannelDistance(from, to);
                if (separation < MinCompositedChannelSeparation)
                {
                    failures.Add($"{where} {name}: {separation:F1}/255 after compositing");
                }
            }
        }

        failures.Should().BeEmpty(
            "the shell paints the gradient through a 0.8 base and a 0.7–0.9 pulse; the stops have " +
            "to still differ once that is done");
    }

    private static double MaxChannelDistance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>Shortest angular distance between two hues, in degrees.</summary>
    private static double HueDelta(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }
}
