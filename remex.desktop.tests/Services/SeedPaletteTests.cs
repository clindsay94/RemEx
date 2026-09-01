using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Media;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The desktop palette is generated from one seed (RemEx-zg8ws). These tests hold the generator to
/// the guarantees the hand-authored theme dictionaries used to provide by hand.
/// </summary>
/// <remarks>
/// <para>
/// WHY THESE EXIST ALONGSIDE <see cref="AccentForegroundContrastTests"/> RATHER THAN INSTEAD OF IT.
/// That class measures the hex literals in the four <c>Themes/*.axaml</c> files. Those literals are
/// still real — they are what paints before <c>ApplyCustomization</c> lands, and a window shown in
/// that window would be unreadable if they were wrong — but they are no longer what the user sees
/// once a profile is applied, which is always. What the user sees comes out of
/// <see cref="DynamicColorGenerator"/>, from a seed that is not in any file. Only a test that
/// generates palettes can cover that, and only across seeds: a single seed proves nothing about the
/// next one the user picks out of the colour wheel.
/// </para>
/// <para>
/// THE SEEDS BELOW ARE ADVERSARIAL ON PURPOSE. Pure yellow and pure white are where an "on" role
/// has the least room to move, and near-black is where a dark palette has none.
/// </para>
/// </remarks>
public class SeedPaletteTests
{
    private static readonly Color[] Seeds =
    {
        Color.Parse("#6C4CFF"),   // the shipped default
        Color.Parse("#00E5FF"),   // CyberNOC-ish cyan
        Color.Parse("#F59E0B"),   // amber — a light seed
        Color.Parse("#FFFF00"),   // pure yellow — worst case for a light "on" role
        Color.Parse("#FFFFFF"),   // white — zero chroma, maximum tone
        Color.Parse("#000000"),   // black — zero chroma, minimum tone
        Color.Parse("#8B0000"),   // dark red — collides with the error role
    };

    private static readonly string[] Variants =
    {
        "TonalSpot", "Vibrant", "Expressive", "Rainbow", "FruitSalad", "Content", "Spritz",
    };

    /// <summary>Every (foreground, background) pair the generator is responsible for.</summary>
    private static IEnumerable<(string Name, Color Fg, Color Bg)> Pairs(DynamicColorGenerator.M3Palette p)
    {
        yield return ("OnPrimary/Primary", p.OnPrimary, p.Primary);
        yield return ("OnPrimaryContainer/PrimaryContainer", p.OnPrimaryContainer, p.PrimaryContainer);
        yield return ("OnSecondary/Secondary", p.OnSecondary, p.Secondary);
        yield return ("OnSecondaryContainer/SecondaryContainer", p.OnSecondaryContainer, p.SecondaryContainer);
        yield return ("OnTertiary/Tertiary", p.OnTertiary, p.Tertiary);
        yield return ("OnSurface/Surface", p.OnSurface, p.Surface);
        yield return ("OnSurfaceVariant/SurfaceVariant", p.OnSurfaceVariant, p.SurfaceVariant);
        yield return ("OnError/Error", p.OnError, p.Error);
        yield return ("OnSuccess/Success", p.OnSuccess, p.Success);
        yield return ("OnWarning/Warning", p.OnWarning, p.Warning);
    }

    private static double Ratio(Color a, Color b) => DynamicColorGenerator.ContrastRatio(a, b);

    [Fact]
    public void EveryOnRoleClearsWcagAaAgainstItsOwnFill()
    {
        // THE GUARANTEE THE HAND-MEASURED LITERALS USED TO PROVIDE, now demanded of every seed the
        // user can choose rather than of the four the repo happened to ship. This is the assertion
        // that makes deriving SuccessForegroundBrush/ErrorForegroundBrush/AccentForegroundBrush
        // safe: it is the same property their comments recorded, checked over the whole input space.
        var failures = new List<string>();

        foreach (var seed in Seeds)
        foreach (var variant in Variants)
        foreach (var dark in new[] { true, false })
        {
            var palette = DynamicColorGenerator.Generate(seed, variant, dark);
            foreach (var (name, fg, bg) in Pairs(palette))
            {
                double r = Ratio(fg, bg);
                if (r < 4.5)
                {
                    failures.Add($"{seed} {variant} {(dark ? "dark" : "light")} {name} = {r:F2}:1");
                }
            }
        }

        failures.Should().BeEmpty(
            "a foreground derived from its own fill has no excuse for failing AA against it");
    }

    [Fact]
    public void ContrastZeroIsStillExactlyTheLibraryScheme()
    {
        // THE PROPERTY THAT LET CONTRAST BE ADDED TO A PALETTE CONNOR HAD ALREADY APPROVED. Every
        // install carries ThemeContrast = 0.0 until somebody moves the slider; if zero were merely
        // *close* to the old output, shipping this would have repainted every one of them.
        //
        // Checked against the library, not against a re-derivation through the same helper the
        // generator uses, so the oracle cannot agree with a broken generator. This also catches the
        // duller failure the literals would have missed: a role wired to the wrong scheme member.
        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        {
            var core = new CorePalette();
            core.Fill(((uint)seed.A << 24) | ((uint)seed.R << 16) | ((uint)seed.G << 8) | seed.B, Style.TonalSpot);
            Scheme<uint> expected = dark
                ? new DarkSchemeMapper().Map(core)
                : new LightSchemeMapper().Map(core);

            var actual = DynamicColorGenerator.Generate(seed, "TonalSpot", dark, contrast: 0.0);
            var where = $"seed {seed}, {(dark ? "dark" : "light")}";

            Argb(actual.Primary).Should().Be(expected.Primary, where);
            Argb(actual.OnPrimary).Should().Be(expected.OnPrimary, where);
            Argb(actual.PrimaryContainer).Should().Be(expected.PrimaryContainer, where);
            Argb(actual.OnPrimaryContainer).Should().Be(expected.OnPrimaryContainer, where);
            Argb(actual.Secondary).Should().Be(expected.Secondary, where);
            Argb(actual.OnSecondary).Should().Be(expected.OnSecondary, where);
            Argb(actual.SecondaryContainer).Should().Be(expected.SecondaryContainer, where);
            Argb(actual.OnSecondaryContainer).Should().Be(expected.OnSecondaryContainer, where);
            Argb(actual.Tertiary).Should().Be(expected.Tertiary, where);
            Argb(actual.OnTertiary).Should().Be(expected.OnTertiary, where);
            Argb(actual.Surface).Should().Be(expected.Surface, where);
            Argb(actual.SurfaceVariant).Should().Be(expected.SurfaceVariant, where);
            Argb(actual.SurfaceContainerLow).Should().Be(expected.SurfaceContainerLow, where);
            Argb(actual.SurfaceContainer).Should().Be(expected.SurfaceContainer, where);
            Argb(actual.SurfaceContainerHigh).Should().Be(expected.SurfaceContainerHigh, where);
            Argb(actual.OnSurface).Should().Be(expected.OnSurface, where);
            Argb(actual.OnSurfaceVariant).Should().Be(expected.OnSurfaceVariant, where);
            Argb(actual.Outline).Should().Be(expected.Outline, where);
            Argb(actual.OutlineVariant).Should().Be(expected.OutlineVariant, where);
            Argb(actual.Error).Should().Be(expected.Error, where);
            Argb(actual.OnError).Should().Be(expected.OnError, where);
        }
    }

    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    [Fact]
    public void RaisingContrastNeverLowersAMeasuredRatio()
    {
        // A "contrast" slider that reduces contrast somewhere in its range is worse than no slider,
        // because the user cannot tell which end is which. Monotonicity is the whole contract.
        var levels = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        var regressions = new List<string>();

        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        {
            var previous = new Dictionary<string, double>();
            foreach (var level in levels)
            {
                var palette = DynamicColorGenerator.Generate(seed, "TonalSpot", dark, level);
                foreach (var (name, fg, bg) in Pairs(palette))
                {
                    double r = Ratio(fg, bg);
                    // A hair of tolerance: a tonal palette's chroma is not constant along tone, so
                    // the best available step can measure a rounding-width worse than the last one.
                    if (previous.TryGetValue(name, out var before) && r < before - 0.05)
                    {
                        regressions.Add($"{seed} {(dark ? "dark" : "light")} {name}: {before:F2} -> {r:F2} at contrast {level}");
                    }
                    previous[name] = r;
                }
            }
        }

        regressions.Should().BeEmpty("raising the contrast setting must never lower a contrast ratio");
    }

    [Fact]
    public void MaximumContrastActuallyMovesTheForegroundsItCanMove()
    {
        // ANTI-VACUITY FOR THE TEST ABOVE. Monotonicity is trivially satisfied by a function that
        // ignores its contrast argument, which is exactly the bug this replaced (RemEx-68ynp: the
        // parameter existed, was passed, and was never read). Demand that the extreme end of the
        // slider is visibly different from the middle.
        var flat = DynamicColorGenerator.Generate(Color.Parse("#6C4CFF"), "TonalSpot", isDark: true, contrast: 0.0);
        var loud = DynamicColorGenerator.Generate(Color.Parse("#6C4CFF"), "TonalSpot", isDark: true, contrast: 1.0);

        Pairs(loud).Zip(Pairs(flat))
            .Count(p => p.First.Fg != p.Second.Fg)
            .Should().BeGreaterThan(3, "contrast 1.0 has to change most foregrounds or it does nothing");

        Ratio(loud.OnPrimary, loud.Primary)
            .Should().BeGreaterThan(Ratio(flat.OnPrimary, flat.Primary));
    }

    [Fact]
    public void LoweringContrastStopsAtTheLargeTextFloor()
    {
        // The reduced end of the slider is the dangerous one: it is a setting whose purpose is to
        // make text harder to read, so it needs a hard stop. 3.0:1 is WCAG AA for large text — the
        // lowest ratio any guideline calls acceptable for anything.
        var failures = new List<string>();

        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        {
            var palette = DynamicColorGenerator.Generate(seed, "TonalSpot", dark, -1.0);
            foreach (var (name, fg, bg) in Pairs(palette))
            {
                double r = Ratio(fg, bg);
                if (r < 3.0) failures.Add($"{seed} {(dark ? "dark" : "light")} {name} = {r:F2}:1");
            }
        }

        failures.Should().BeEmpty("minimum contrast must still clear AA for large text");
    }

    [Fact]
    public void EveryVariantProducesADistinctPaletteFromTheSameSeed()
    {
        // THE HEADLINE ACCEPTANCE CRITERION OF RemEx-lrxyo, which until this test was verified only
        // by looking at a screenshot. The variant row shows seven strips painted from one seed; if
        // two variants collapse onto identical output, the picker silently offers the user a choice
        // that is not a choice, and every one of the 2989 other tests stays green while it does.
        //
        // Asserted on the six values the strip and its surface actually paint, not on the whole
        // M3Palette: two variants agreeing on some interior role is normal and not a defect. What
        // must never happen is two strips being indistinguishable to the code that draws them.
        //
        // This locks in behaviour that is correct TODAY — measured 7/7 distinct across these seeds
        // and both modes — rather than chasing a known bug. The risk it guards is a future tweak to
        // StyleFor or to the neutral-chroma handling quietly merging two variants.
        foreach (var seed in Seeds)
        {
            foreach (var isDark in new[] { true, false })
            {
                var seen = new Dictionary<string, string>();

                foreach (var variant in Variants)
                {
                    var p = DynamicColorGenerator.Generate(seed, variant, isDark: isDark, contrast: 0.0);
                    var fingerprint = string.Join(
                        "|", p.Surface, p.Primary, p.Secondary, p.Tertiary, p.OnSurface, p.Outline);

                    seen.Should().NotContainKey(fingerprint,
                        $"variant '{variant}' must not render identically to '{(seen.TryGetValue(fingerprint, out var other) ? other : "?")}' " +
                        $"for seed {seed} in {(isDark ? "dark" : "light")} mode — the variant row would " +
                        "show the user two strips that are the same choice");

                    seen[fingerprint] = variant;
                }

                seen.Should().HaveCount(Variants.Length,
                    $"all {Variants.Length} variants have to be distinguishable for seed {seed}");
            }
        }
    }

    [Fact]
    public void SuccessAndWarningStayGreenAndAmberWhateverTheSeedIs()
    {
        // SEMANTIC COLOURS ARE NOT THEME COLOURS. If success drifted with the accent, a user who
        // seeded the app red would get a red "connected" badge — the exact confusion the colour
        // exists to prevent. This is what the separate seeds in the generator buy.
        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        {
            var palette = DynamicColorGenerator.Generate(seed, "TonalSpot", dark);
            HueOf(palette.Success).Should().BeInRange(70, 170,
                $"success must read as green, seed {seed} ({(dark ? "dark" : "light")})");
            HueOf(palette.Warning).Should().BeInRange(20, 70,
                $"warning must read as amber, seed {seed} ({(dark ? "dark" : "light")})");
        }
    }

    [Fact]
    public void TheSuccessSeedIsStillByteIdenticalToAndroids()
    {
        // Two platforms, one idea of "success". They cannot share a constant — different languages,
        // different repos-within-a-repo — so the only thing keeping them together is this test
        // reading the other one's source. Drift here is silent and only visible side by side.
        var kotlin = File.ReadAllText(Path.Combine(RepoRoot(),
            "remex.android", "app", "src", "main", "java", "com", "clindsay94", "remex", "ui", "theme", "Theme.kt"));

        var androidSeed = Regex.Match(kotlin, @"val successSeed = Hct\.fromInt\((0x[0-9A-Fa-f]{8})\.toInt\(\)\)");
        androidSeed.Success.Should().BeTrue("Theme.kt's success seed moved or was renamed — find it and re-point this test");

        var csharp = File.ReadAllText(Path.Combine(RepoRoot(),
            "remex.desktop", "Services", "DynamicColorGenerator.cs"));
        var desktopSeed = Regex.Match(csharp, @"const uint SuccessSeed = (0x[0-9A-Fa-f]{8});");
        desktopSeed.Success.Should().BeTrue("the desktop success seed moved or was renamed");

        desktopSeed.Groups[1].Value.Should().BeEquivalentTo(androidSeed.Groups[1].Value,
            "the desktop and Android success colours are supposed to be the same green");
    }

    /// <summary>HSL hue in degrees. Enough to say "this is green"; not a colour-science claim.</summary>
    private static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        if (d < 1e-9) return 0;

        double h = max == r ? (g - b) / d % 6
                 : max == g ? (b - r) / d + 2
                            : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
