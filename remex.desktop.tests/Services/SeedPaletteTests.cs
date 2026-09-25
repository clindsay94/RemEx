using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Theming.Mcu;
using Remex.Desktop.Models;
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

    private static readonly IReadOnlyList<string> Variants = SchemeVariants.All;

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
    public void EveryMaterialRoleIsExactlyTheVectorScheme()
    {
        // The oracle used to be the albi005 library in-process; it is now Google's output, committed as
        // mcu-vectors.json (RemEx-4kv0g.6). Every tuple, the 21 M3 roles M3Palette carries as Colors AND all 62 in Roles.
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "remex.core.tests", "Fixtures", "mcu-vectors.json")));
        var roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();
        var failures = new List<string>();
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var seed = Color.Parse(v.GetProperty("seed").GetString()!);
            var variant = SchemeVariants.FromMcu(SchemeVariantWire.FromWireOrDefault(v.GetProperty("variant").GetString()));
            bool dark = v.GetProperty("dark").GetBoolean();
            double contrast = v.GetProperty("contrast").GetDouble();
            var expected = v.GetProperty("argb").EnumerateArray().Select(a => Convert.ToUInt32(a.GetString()!.Substring(1), 16)).ToArray();
            var where = $"{seed} {variant} {(dark ? "dark" : "light")} contrast {contrast}";

            var actual = DynamicColorGenerator.Generate(seed, variant, dark, contrast);
            for (int i = 0; i < roles.Length; i++)
                if (actual.Roles[roles[i]] != expected[i]) failures.Add($"{where} Roles[{roles[i]}]");

            uint Want(string role) => expected[Array.IndexOf(roles, role)];
            foreach (var (name, colour, role) in new[]
            {
                ("Primary", actual.Primary, "primary"), ("OnPrimary", actual.OnPrimary, "onPrimary"), ("PrimaryContainer", actual.PrimaryContainer, "primaryContainer"),
                ("OnPrimaryContainer", actual.OnPrimaryContainer, "onPrimaryContainer"), ("Secondary", actual.Secondary, "secondary"), ("OnSecondary", actual.OnSecondary, "onSecondary"),
                ("SecondaryContainer", actual.SecondaryContainer, "secondaryContainer"), ("OnSecondaryContainer", actual.OnSecondaryContainer, "onSecondaryContainer"),
                ("Tertiary", actual.Tertiary, "tertiary"), ("OnTertiary", actual.OnTertiary, "onTertiary"), ("Surface", actual.Surface, "surface"),
                ("SurfaceVariant", actual.SurfaceVariant, "surfaceVariant"), ("SurfaceContainerLow", actual.SurfaceContainerLow, "surfaceContainerLow"),
                ("SurfaceContainer", actual.SurfaceContainer, "surfaceContainer"), ("SurfaceContainerHigh", actual.SurfaceContainerHigh, "surfaceContainerHigh"),
                ("OnSurface", actual.OnSurface, "onSurface"), ("OnSurfaceVariant", actual.OnSurfaceVariant, "onSurfaceVariant"), ("Outline", actual.Outline, "outline"),
                ("OutlineVariant", actual.OutlineVariant, "outlineVariant"), ("Error", actual.Error, "error"), ("OnError", actual.OnError, "onError"),
            })
                if (Argb(colour) != Want(role)) failures.Add($"{where} {name}");
        }
        failures.Should().BeEmpty($"first 20: {string.Join("; ", failures.Take(20))}");
    }

    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    [Fact]
    public void EveryOnRoleMeetsItsCurveTargetOrHitsTheEndOfTheScale()
    {
        // MCU's contract (DynamicColor.getTone): a foreground is re-toned until Contrast.ratioOfTones meets its
        // ContrastCurve.get(level); if neither the lighter nor the darker solution can, it lands on tone 0 or 100.
        // Curves from MaterialDynamicColors.java (material 1.14.0). Measured on HCT tone, MCU's own metric.
        var onRole = new ContrastCurve(4.5, 7.0, 11.0, 21.0);          // onPrimary, onSecondary, onTertiary, onSurface, onError
        var onVariant = new ContrastCurve(3.0, 4.5, 7.0, 11.0);        // onSurfaceVariant
        var failures = new List<string>();
        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        foreach (var level in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
        {
            var p = DynamicColorGenerator.Generate(seed, "TonalSpot", dark, level);
            uint highestSurface = dark ? p.Roles.SurfaceBright : p.Roles.SurfaceDim;   // onSurface's background is highestSurface, not surface
            foreach (var (name, fg, bg, curve) in new[]
            {
                ("OnPrimary/Primary", Argb(p.OnPrimary), Argb(p.Primary), onRole),
                ("OnSecondary/Secondary", Argb(p.OnSecondary), Argb(p.Secondary), onRole),
                ("OnTertiary/Tertiary", Argb(p.OnTertiary), Argb(p.Tertiary), onRole),
                ("OnError/Error", Argb(p.OnError), Argb(p.Error), onRole),
                ("OnSurface/highestSurface", Argb(p.OnSurface), highestSurface, onRole),
                ("OnSurfaceVariant/highestSurface", Argb(p.OnSurfaceVariant), highestSurface, onVariant),
            })
            {
                double fgTone = Hct.FromInt(fg).Tone, bgTone = Hct.FromInt(bg).Tone;
                double achieved = Contrast.RatioOfTones(fgTone, bgTone);
                double target = curve.Get(level);
                bool atEnd = fgTone <= 0.5 || fgTone >= 99.5;
                if (achieved < target - 0.15 && !atEnd)
                    failures.Add($"{seed} {(dark ? "dark" : "light")} level {level} {name}: {achieved:F2} < {target:F2}, tone {fgTone:F1}");
            }
        }
        failures.Should().BeEmpty("every on-role either meets its ContrastCurve target or is already at tone 0/100");
    }

    [Fact]
    public void MaximumContrastActuallyMovesTheForegroundsItCanMove()
    {
        // ANTI-VACUITY FOR THE TEST ABOVE. Monotonicity is trivially satisfied by a function that
        // ignores its contrast argument, which is exactly the bug this replaced (RemEx-68ynp: the
        // parameter existed, was passed, and was never read) — level 1.0 targets ratio 11 against
        // level 0's 7, so it still moves. Demand that the extreme end of the slider is visibly
        // different from the middle. The contrast now comes from MCU's own ContrastCurves.
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
        // make text harder to read, so it needs a hard stop. MCU's lowest curve anchor for any
        // on-role is 3.0 (ContrastCurve(3.0, …) on the container/variant on-roles) and 4.5 for the
        // rest, so level -1 still clears AA-large; ReducedContrastFloor no longer exists — the
        // curves are the floor.
        var failures = new List<string>();

        foreach (var seed in Seeds)
        foreach (var dark in new[] { true, false })
        {
            var palette = DynamicColorGenerator.Generate(seed, "TonalSpot", dark, -1.0);
            foreach (var (name, fg, bg) in Pairs(palette))
            {
                // OnSurfaceVariant/SurfaceVariant is measured against SurfaceVariant everywhere else in this
                // file, but MCU's own contract for onSurfaceVariant targets the highest surface (surfaceBright
                // in dark, surfaceDim in light), not the surfaceVariant container fill — those two can sit much
                // closer in tone than onSurfaceVariant's real background, so the fill measurement is the wrong
                // oracle at the floor and is skipped here in favour of the correct pairing.
                if (name == "OnSurfaceVariant/SurfaceVariant")
                {
                    uint highestSurface = dark ? palette.Roles.SurfaceBright : palette.Roles.SurfaceDim;
                    double rv = Ratio(fg, DynamicColorGeneratorTestColor(highestSurface));
                    if (rv < 3.0) failures.Add($"{seed} {(dark ? "dark" : "light")} OnSurfaceVariant/highestSurface = {rv:F2}:1");
                    continue;
                }

                double r = Ratio(fg, bg);
                if (r < 3.0) failures.Add($"{seed} {(dark ? "dark" : "light")} {name} = {r:F2}:1");
            }
        }

        failures.Should().BeEmpty("minimum contrast must still clear AA for large text");
    }

    private static Color DynamicColorGeneratorTestColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    [Fact]
    public void EveryVariantProducesADistinctPaletteFromTheSameSeed()
    {
        // THE HEADLINE ACCEPTANCE CRITERION OF RemEx-lrxyo, which until this test was verified only
        // by looking at a screenshot. The variant row shows nine strips painted from one seed; if
        // two variants collapse onto identical output, the picker silently offers the user a choice
        // that is not a choice, and every one of the other tests stays green while it does.
        //
        // Asserted on the six values the strip and its surface actually paint, not on the whole
        // M3Palette: two variants agreeing on some interior role is normal and not a defect. What
        // must never happen is two strips being indistinguishable to the code that draws them.
        //
        // This locks in behaviour that is correct TODAY across these seeds and both modes — rather
        // than chasing a known bug. The risk it guards is a future tweak to the variant mapping or
        // the neutral-chroma handling quietly merging two variants. One pair is a known, accepted
        // exception: see the carve-out below — bounded to the two explainable causes, not to "the
        // fingerprints happened to match", so a real bug (e.g. ToMcu mapping both names to one
        // enum) cannot hide behind it (review finding, RemEx-4kv0g.12).
        bool everFidelityDistinctFromContent = false;

        foreach (var seed in Seeds)
        {
            foreach (var isDark in new[] { true, false })
            {
                var seen = new Dictionary<string, string>();
                bool collapsedFidelityIntoContent = false;
                string? fidelityFingerprint = null;

                foreach (var variant in Variants)
                {
                    var p = DynamicColorGenerator.Generate(seed, variant, isDark: isDark, contrast: 0.0);
                    var fingerprint = string.Join(
                        "|", p.Surface, p.Primary, p.Secondary, p.Tertiary, p.OnSurface, p.Outline);

                    if (variant == SchemeVariants.Fidelity) fidelityFingerprint = fingerprint;

                    if (variant == SchemeVariants.Content)
                    {
                        everFidelityDistinctFromContent |= fidelityFingerprint != fingerprint;
                    }

                    // SchemeFidelity and SchemeContent differ ONLY in the tertiary palette, which they derive
                    // from the seed's hue temperature (complement vs. analogous) — EXCEPT when that difference
                    // never reaches the rendered colour. Two explainable causes, both Google's own construction:
                    //
                    //  1. TONE SATURATION VIA ToneDeltaPair. tertiaryContainer's ToneDeltaPair(tertiaryContainer,
                    //     tertiary, 10, NEARER) pushes tertiary toward the container by a fixed 10-tone gap; for a
                    //     seed near the top of the tone axis in dark mode (pure yellow, tone ~97, measured) that
                    //     second round saturates tertiary at tone 100 — pure white, #FFFFFFFF — for BOTH variants
                    //     regardless of which hue (complement vs. analogous) fed it. Detected on the actual
                    //     rendered ARGB (0xFFFFFFFF or 0xFF000000 at the other tone extreme), not inferred.
                    //  2. ACHROMATIC SEED. Chroma < 5 leaves no usable hue at all, so complement and analogous
                    //     derive the same (near-)zero-chroma tertiary by construction.
                    //
                    // Anything outside these two causes is a real collision and must fail below, not be skipped —
                    // that is what keeps this carve-out from going vacuous (a ToMcu bug mapping both names onto
                    // one enum would otherwise pass silently for every seed).
                    uint tertiaryArgb = Argb(p.Tertiary);
                    bool toneSaturated = tertiaryArgb == 0xFFFFFFFFu || tertiaryArgb == 0xFF000000u;
                    bool achromatic = Hct.FromInt(Argb(seed)).Chroma < 5.0;
                    if (variant == SchemeVariants.Content && (toneSaturated || achromatic)
                        && seen.TryGetValue(fingerprint, out var twin) && twin == SchemeVariants.Fidelity)
                    {
                        collapsedFidelityIntoContent = true;
                        continue;
                    }

                    seen.Should().NotContainKey(fingerprint,
                        $"variant '{variant}' must not render identically to '{(seen.TryGetValue(fingerprint, out var other) ? other : "?")}' " +
                        $"for seed {seed} in {(isDark ? "dark" : "light")} mode — the variant row would " +
                        "show the user two strips that are the same choice");

                    seen[fingerprint] = variant;
                }

                seen.Should().HaveCount(collapsedFidelityIntoContent ? Variants.Count - 1 : Variants.Count,
                    $"all {Variants.Count} variants have to be distinguishable for seed {seed}");
            }
        }

        // ANTI-VACUITY: the carve-out above must never be the reason EVERY (seed, mode) agrees Fidelity
        // and Content are the same. If this fires, the carve-out (or a real ToMcu bug) is swallowing the
        // very distinction this test exists to protect.
        everFidelityDistinctFromContent.Should().BeTrue(
            "Fidelity and Content must actually differ for at least one seed/mode in the sweep");
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
    public void SuccessAndWarningAreIdenticalAcrossDifferentSeedsAtTheSameIsDarkAndContrast()
    {
        // Perf audit P2-3: Success/Warning are cached by (isDark, contrast) since they never depend
        // on the caller's seed or variant. Proves the seed/variant genuinely don't leak into the
        // cached output - the sibling test below covers isDark/contrast actually discriminating.
        var a = DynamicColorGenerator.Generate(Color.Parse("#3366CC"), "Vibrant", isDark: true, contrast: 0.4);
        var b = DynamicColorGenerator.Generate(Color.Parse("#CC3366"), "Expressive", isDark: true, contrast: 0.4);

        a.Success.Should().Be(b.Success);
        a.OnSuccess.Should().Be(b.OnSuccess);
        a.Warning.Should().Be(b.Warning);
        a.OnWarning.Should().Be(b.OnWarning);
        // But the seed-derived roles must still differ - otherwise this test would pass even if
        // Generate accidentally ignored the seed entirely.
        a.Primary.Should().NotBe(b.Primary);
    }

    [Fact]
    public void SuccessAndWarningDifferByIsDarkAndByContrastDespiteTheCache()
    {
        // The cache is keyed on (isDark, contrast) - this proves the key actually discriminates,
        // not just that two calls at the SAME key return the same answer (the test above).
        var seed = Color.Parse("#3366CC");

        var dark = DynamicColorGenerator.Generate(seed, "TonalSpot", isDark: true, contrast: 0.0);
        var light = DynamicColorGenerator.Generate(seed, "TonalSpot", isDark: false, contrast: 0.0);
        dark.Success.Should().NotBe(light.Success, "isDark must still be part of the cache key");

        var lowContrast = DynamicColorGenerator.Generate(seed, "TonalSpot", isDark: true, contrast: 0.0);
        var highContrast = DynamicColorGenerator.Generate(seed, "TonalSpot", isDark: true, contrast: 1.0);
        lowContrast.Success.Should().NotBe(highContrast.Success, "contrast must still be part of the cache key");
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
