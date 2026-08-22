using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// No colour in the desktop shell may be hand-authored per theme any more (RemEx-zg8ws).
/// </summary>
/// <remarks>
/// <para>
/// THE MEASUREMENT THAT MOTIVATED THIS. The four theme dictionaries carried 53 keys each. Twenty-six
/// were already overwritten at runtime from the M3 seed, so their hand-tuned values were dead text.
/// Of the remaining 27, exactly SEVEN differed between the four files. Four themes that agree on 46
/// of 53 keys are one theme with four accent colours, and the seven keys they disagreed on were the
/// only thing a user could actually change — which is why the app looked half-generated and why
/// Connor asked for a colour picker instead of a theme list.
/// </para>
/// <para>
/// SO THE RULE IS ALL-OR-NOTHING, and this test is what makes it enforceable rather than aspirational.
/// A colour key that exists in a theme file but is never overridden is a colour that ignores the
/// user's seed, and it will not look wrong in the default theme — it will look wrong for someone who
/// picked orange. That is a bug nobody on this machine can see.
/// </para>
/// </remarks>
public class ThemeKeyCoverageTests
{
    /// <summary>
    /// The keys that are deliberately NOT seed-derived. All five are geometry, not palette: they
    /// have their own sliders on the customization page and no colour meaning at all.
    /// </summary>
    private static readonly string[] GeometryKeys =
    {
        "CornerRadiusSmall", "CornerRadiusMedium", "CornerRadiusLarge", "CornerRadiusExtraLarge",
        "CardBorderThickness",
    };

    [Fact]
    public void EveryColourKeyInEveryThemeIsOverriddenFromTheSeed()
    {
        var overridden = OverriddenKeys();

        var uncovered = ThemeFiles()
            .SelectMany(ColourKeysIn)
            .Distinct()
            .Where(key => !overridden.Contains(key))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        uncovered.Should().BeEmpty(
            "a colour key the seed does not reach keeps whichever hex the theme file happened to "
            + "carry, so it stays correct for the default accent and wrong for every other one");
    }

    [Fact]
    public void TheGeometryKeysAreTheOnlyThingLeftHandAuthored()
    {
        // ANTI-VACUITY AND SCOPE CONTROL IN ONE. The test above is satisfied by finding no colour
        // keys at all — a changed element name, a moved directory, a regex that stopped matching.
        // It is ALSO satisfied by someone widening the exemption list until the rule means nothing.
        // Pin both: the scan really does find the theme surface, and the exemptions really are only
        // the five geometry tokens.
        var allKeys = ThemeFiles().SelectMany(AllKeysIn).Distinct().ToArray();
        var colourKeys = ThemeFiles().SelectMany(ColourKeysIn).Distinct().ToArray();

        colourKeys.Length.Should().BeGreaterThan(40,
            "the four theme dictionaries carry ~44 colour keys; finding far fewer means the scan broke");

        // The four beyond the geometry exemptions are seed-derived too — ThemeService writes all of
        // them — they are simply not declared with a colour-valued element, so the colour scan above
        // cannot see them. Listing them here is what keeps that gap from widening quietly.
        allKeys.Except(colourKeys).Except(GeometryKeys)
            .Should().BeEquivalentTo(new[] { "CardCornerRadius", "RemoteCardCornerRadius", "CardShadow", "CardHoverShadow" },
                "anything else left outside the seed pipeline needs a reason recorded here");
    }

    [Fact]
    public void NoPresetDeclaresAColourOfItsOwn()
    {
        // THE INVARIANT THE MERGE EXISTS FOR (RemEx-07jij), and it needs its own guard because
        // nothing else would notice it breaking. Every test in this file and in
        // AccentForegroundContrastTests reads a preset RESOLVED - preset plus merged fallback - so a
        // colour pasted back into one preset would simply shadow the shared value, resolve fine,
        // measure fine, and pass everything. It would also be invisible: the key it overrides is
        // overwritten from the seed a moment later, so the wrong value paints for two frames on one
        // preset. That is the "four copies drift" failure returning one key at a time.
        foreach (var preset in ThemeFiles())
        {
            var ownText = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Themes", preset + ".axaml"));

            Regex.Matches(ownText,
                    @"<(?:Color|SolidColorBrush|LinearGradientBrush|RadialGradientBrush) x:Key=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .Should().BeEmpty(
                    $"{preset}.axaml must take its colours from Themes/Shared/FallbackPalette.axaml, "
                    + "not carry its own copy");

            // MATCHED ON THE ELEMENT, NOT THE NAME. Every preset file also NAMES this path in its
            // header comment, so a substring check is satisfiable by prose - delete the merge, leave
            // the comment, and the guard stays green. The build would not catch it either: a mistyped
            // Source is AVLN2000, but a preset with no include at all compiles fine and simply
            // resolves nothing.
            ownText.Should().MatchRegex(
                @"<ResourceInclude[^>]*Source=""avares://Remex\.Desktop/Themes/Shared/FallbackPalette\.axaml""",
                $"{preset}.axaml resolves no colours at all unless it merges the shared palette");
        }
    }

    [Fact]
    public void TheGeometryAPresetKeepsIsTheGeometryThatDiffers()
    {
        // SCOPE CONTROL FOR THE TEST ABOVE. "No colours in a preset" is also satisfied by a preset
        // that has been hollowed out entirely, which would silently take every card in that theme to
        // the fallback's shape. These four keys are the ones whose values genuinely differ between
        // presets, and they are the reason a preset file still exists.
        foreach (var preset in ThemeFiles())
        {
            var ownKeys = Regex.Matches(
                    File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Themes", preset + ".axaml")),
                    @"x:Key=""([^""]+)""")
                .Select(m => m.Groups[1].Value);

            ownKeys.Should().BeEquivalentTo(
                new[] { "CardCornerRadius", "CardBorderThickness", "CardShadow", "CardHoverShadow" },
                $"{preset}.axaml is geometry plus a merge; anything else belongs in the shared palette");
        }
    }

    [Fact]
    public void AnUnparseableAccentStillGetsAPalette()
    {
        // FAIL-OPEN, AND IT GOT WORSE WHEN THE FALLBACK BECAME SHARED (RemEx-07jij). The generator
        // call sat inside `if (Color.TryParse(settings.AccentColor, ...))` with no else, so a saved
        // accent that will not parse skipped every SetResourceOverrideInternal below it. That used to
        // leave the selected preset's own complete palette showing. It now leaves the one SHARED DARK
        // fallback showing - underneath a RequestedThemeVariant that was already set to Light from
        // UseLightPalette, because that assignment is outside the guard. Near-white text on Fluent's
        // white chrome, no exception, no log.
        //
        // ASSERTED ON THE SOURCE because ApplyCustomization needs an Avalonia Application to run and
        // there is none in a unit test. What is pinned is the shape that cannot fail open: the parse
        // is negated and assigns a fallback, rather than gating the palette.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        source.Should().MatchRegex(
            @"(?s)if\s*\(\s*!\s*Color\.TryParse\(\s*settings\.AccentColor.*?accentColor\s*=\s*Color\.Parse\(\s*FallbackAccentSeed",
            "an accent that will not parse must fall back to a seed, not skip the palette");

        source.Should().NotMatchRegex(
            @"if\s*\(\s*Color\.TryParse\(\s*settings\.AccentColor",
            "gating the palette on the parse is exactly the fail-open shape this replaced");

        // The fallback must be the record's own default, so a user with a broken accent lands where a
        // user with no accent lands. A third value would be a state nothing else in the app produces.
        var settingsSource = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.core", "Models", "DashboardProfile.cs"));
        var recordDefault = Regex.Match(settingsSource, @"AccentColor\s*\{\s*get;\s*init;\s*\}\s*=\s*""(#[0-9A-Fa-f]{6,8})""");
        recordDefault.Success.Should().BeTrue("CustomizationSettings.AccentColor lost its default");

        source.Should().Contain($"FallbackAccentSeed = \"{recordDefault.Groups[1].Value}\"",
            "ThemeService's fallback seed and the record's default must not drift apart");
    }

    [Fact]
    public void TheCustomAccentBoxRejectsAHexThatWillNotParse()
    {
        // THE OTHER HALF OF THE SAME BUG. ConfirmCustomAccent validated the LENGTH of the typed hex
        // and nothing else, so "#FF0O00" - capital O for zero - was seven characters, became the
        // accent, and was saved into CustomAccentColors as a permanent swatch that survives a restart.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));

        var body = Regex.Match(source, @"private void ConfirmCustomAccent\(\).*?\n    \}", RegexOptions.Singleline);
        body.Success.Should().BeTrue("ConfirmCustomAccent moved or changed shape");

        body.Value.Should().MatchRegex(@"Color\.TryParse\(\s*hex",
            "a length check cannot tell a colour from a typo; ask the parser before assigning");
        body.Value.Should().MatchRegex(@"Color\.TryParse\(\s*hex[^)]*\)\s*\)\s*return;",
            "the parse result has to gate the assignment, not merely be computed");
    }

    [Fact]
    public void ThemeServiceFeedsBothSettingsIntoTheGenerator()
    {
        // THE BUG THIS EXISTS TO PREVENT IS A MISSING ARGUMENT, WHICH COMPILES. Generate's contrast
        // parameter is optional and defaults to 0.0, so dropping it at the call site leaves a
        // working build, a working app, and a settings slider wired to nothing — the state
        // RemEx-68ynp described. Light/dark is now the same shape of risk: an optional bool that
        // silently falls back to "dark" if the call site stops passing it.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        var call = Regex.Match(source, @"DynamicColorGenerator\.Generate\((.*?)\);", RegexOptions.Singleline);
        call.Success.Should().BeTrue("ThemeService no longer calls the generator — find out why before touching this");

        var arguments = call.Groups[1].Value;
        arguments.Should().Contain("settings.SchemeVariant");
        arguments.Should().MatchRegex(@"contrast:.*settings\.ThemeContrast",
            "the contrast setting has to reach the generator, not just be persisted");
        arguments.Should().MatchRegex(@"isDark:\s*!isLightTheme",
            "light/dark has to reach the generator from the resolved setting");

        source.Should().Contain("settings.UseLightPalette",
            "light/dark is a setting now; deriving it from the preset name again is the regression");
    }

    /// <summary>Keys declared with a colour-valued element: <c>Color</c> and the brush types.</summary>
    private static string[] ColourKeysIn(string preset) =>
        Regex.Matches(ThemeDictionary.ResolvedText(preset),
                @"<(?:Color|SolidColorBrush|LinearGradientBrush|RadialGradientBrush) x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

    private static string[] AllKeysIn(string preset) =>
        Regex.Matches(ThemeDictionary.ResolvedText(preset), @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

    /// <summary>Every key <c>ThemeService</c> writes into its override dictionary.</summary>
    private static string[] OverriddenKeys()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));
        var keys = Regex.Matches(source, @"SetResourceOverrideInternal\(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

        keys.Should().NotBeEmpty("if this finds nothing, the test above passes for the wrong reason");
        return keys;
    }

    /// <summary>
    /// The selectable presets, by name. Resolved through <see cref="ThemeDictionary"/>, because a
    /// preset file has held only its geometry since RemEx-07jij and the colours it resolves come
    /// from the shared fallback it merges.
    /// </summary>
    private static string[] ThemeFiles()
    {
        var presets = ThemeDictionary.PresetNames;
        presets.Should().HaveCountGreaterThan(1, "the theme directory moved or emptied");
        return presets;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
