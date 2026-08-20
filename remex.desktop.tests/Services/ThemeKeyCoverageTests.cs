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
    private static string[] ColourKeysIn(string path) =>
        Regex.Matches(File.ReadAllText(path),
                @"<(?:Color|SolidColorBrush|LinearGradientBrush|RadialGradientBrush) x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

    private static string[] AllKeysIn(string path) =>
        Regex.Matches(File.ReadAllText(path), @"x:Key=""([^""]+)""")
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

    private static string[] ThemeFiles()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "remex.desktop", "Themes"), "*.axaml");
        files.Should().HaveCountGreaterThan(1, "the theme directory moved or emptied");
        return files;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
