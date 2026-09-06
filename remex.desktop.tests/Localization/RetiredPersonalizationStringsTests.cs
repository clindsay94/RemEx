using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// The strings the redesigned Personalization sheet retired (spec section 10, plus the keys
/// Task 8's reflow orphaned) are gone from every language and asked for by nothing, and the keys
/// the sheet still shows — including <c>Custom_TonalRamp_*</c>, which share a prefix with the
/// retired bare <c>Custom_TonalRamp</c> — are still present in every language.
/// </summary>
public class RetiredPersonalizationStringsTests
{
    private static readonly string[] Retired =
    {
        "Custom_SeedTone", "Custom_Scheme_Content", "Custom_Scheme_Spritz", "Custom_BgType_Mica",
        "Custom_BasePresets", "Custom_PaletteStudio", "Custom_Atmosphere", "Custom_Typography",
        "Custom_SeedChroma", "Custom_SchemeVariant", "Custom_TonalRamp", "Custom_AccentColor",
        "Custom_SelectColorPaletteTooltip", "Custom_SelectNeonVioletTooltip",
        "Custom_SelectCyberCyanTooltip", "Custom_SelectHotPinkTooltip", "Custom_SelectSolarGoldTooltip",
        "Custom_SelectEmeraldGreenTooltip", "Custom_SelectCrimsonRoseTooltip",
        "Custom_AddCustomColorTooltip", "Custom_CustomAccentColorTitle", "Custom_SystemSeedHeader",
        "Custom_SharePaletteHeader", "Custom_MatchWindowsAccent", "Custom_SeedFromWallpaper",
    };

    /// <summary>
    /// Keys the sheet still uses that a careless removal could catch as collateral damage —
    /// notably <c>Custom_TonalRamp_Primary</c> and its siblings, which share a prefix with the
    /// retired bare key <c>Custom_TonalRamp</c>. The removal script matches the whole
    /// <c>&lt;data name="…"&gt;</c> attribute, so a prefix cannot take its siblings with it;
    /// this test is what proves that.
    /// </summary>
    private static readonly string[] StillInUse =
    {
        "Custom_TonalRamp_Primary", "Custom_TonalRamp_Secondary", "Custom_TonalRamp_Tertiary",
        "Custom_TonalRamp_Neutral", "Custom_TonalRamp_PrimaryPair", "Custom_TonalRamp_SurfacePair",
        "Custom_TonalRamp_ErrorPair", "Custom_BgType_Aurora", "Custom_Scheme_TonalSpot",
    };

    private static readonly string[] Files =
    {
        "Strings.resx", "Strings.es.resx", "Strings.fr.resx", "Strings.hi.resx", "Strings.id.resx",
        "Strings.pl.resx", "Strings.pt-BR.resx", "Strings.tr.resx", "Strings.uk.resx",
    };

    [Fact]
    public void TheRetiredKeysAreAbsentFromAllNineFiles()
    {
        foreach (var file in Files)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Localization", file));
            foreach (var key in Retired)
                Regex.IsMatch(text, $@"<data name=""{Regex.Escape(key)}""").Should().BeFalse($"{file} still defines {key}");
        }
    }

    [Fact]
    public void TheKeysTheSheetStillUsesSurviveInAllNineFiles()
    {
        foreach (var file in Files)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Localization", file));
            foreach (var key in StillInUse)
                Regex.IsMatch(text, $@"<data name=""{Regex.Escape(key)}""").Should().BeTrue($"{file} is missing {key}");
        }
    }

    [Fact]
    public void NothingInTheDesktopStillAsksForThem()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs") || p.EndsWith(".axaml"))
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.EndsWith("Strings.Designer.cs"));

        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (var key in Retired)
            {
                // Whole identifier: '_' is a word character, so \b does not fall between
                // Custom_TonalRamp and _Primary, and the live Custom_TonalRamp_* references do
                // not read as references to the retired bare key.
                Regex.IsMatch(text, $@"\b{Regex.Escape(key)}\b").Should().BeFalse($"{path} references a retired string {key}");
            }
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
