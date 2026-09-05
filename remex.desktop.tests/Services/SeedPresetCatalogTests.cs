using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The preset gallery (RemEx-2gjwn): eight seed+variant+mode combinations, four of them homages to
/// the retired named themes, each previewing itself in its own generated palette.
/// </summary>
public class SeedPresetCatalogTests
{
    /// <summary>
    /// The bead's acceptance criterion, stated as arithmetic: picking a preset is indistinguishable
    /// from picking its seed by hand.
    /// </summary>
    /// <remarks>
    /// TWO CALL SITES GENERATE THE PALETTE, and they must not drift. The tile renders itself with
    /// <c>SeedPresetTileViewModel.PaletteFor</c>; the shell renders with
    /// <c>ThemeService.ApplyCustomization</c>'s own <c>Generate</c> call on the settings the preset
    /// writes. If those diverge, a tile advertises a colour the app will not paint — which is the
    /// exact failure the hand-authored swatches this gallery replaced had been shipping for months.
    /// </remarks>
    [Fact]
    public void EveryPresetTileRendersTheColoursSelectingItWouldProduce()
    {
        foreach (var preset in SeedPresetCatalog.All.Where(p => p.Seed is not null))
        {
            // What the tile shows. The "live" arguments are deliberately hostile — a seed and a
            // mode nothing in the catalog uses — so a preset that silently fell through to them
            // instead of using its own values fails here rather than coincidentally matching.
            var tile = SeedPresetTileViewModel.PaletteFor(
                preset, liveSeed: "#123456", liveVariant: "Rainbow", liveIsLight: true, liveContrast: -1.0);

            // What selecting it writes, put through the same generator the theme service uses.
            Assert.True(Color.TryParse(preset.Seed!, out var seed), $"{preset.Id}: unparseable seed");
            var applied = DynamicColorGenerator.Generate(
                seed,
                preset.SchemeVariant!,
                isDark: !preset.IsLight!.Value,
                contrast: preset.Contrast!.Value);

            tile.Should().BeEquivalentTo(applied, $"{preset.Id}'s tile must show what clicking it paints");
        }
    }

    /// <summary>Dynamic is the one preset that renders the user's own settings, not its own.</summary>
    [Fact]
    public void TheDynamicTileFollowsTheLiveSeedRatherThanPinningOne()
    {
        var dynamic = SeedPresetCatalog.All.Single(p => p.Id == SeedPresetCatalog.DynamicId);

        var atTeal = SeedPresetTileViewModel.PaletteFor(dynamic, "#00A0A0", "TonalSpot", false, 0.0);
        var atRose = SeedPresetTileViewModel.PaletteFor(dynamic, "#C04070", "TonalSpot", false, 0.0);

        atTeal.Primary.Should().NotBe(atRose.Primary,
            "Dynamic means 'my own colour' - a tile that ignores the live seed is showing a lie");

        // And the mode has to follow too, or the tile is a light card in a dark app.
        var dark = SeedPresetTileViewModel.PaletteFor(dynamic, "#00A0A0", "TonalSpot", liveIsLight: false, 0.0);
        var light = SeedPresetTileViewModel.PaletteFor(dynamic, "#00A0A0", "TonalSpot", liveIsLight: true, 0.0);
        dark.Surface.Should().NotBe(light.Surface);
    }

    /// <summary>
    /// A tile handed a seed nothing can parse renders the fallback rather than black or an exception.
    /// </summary>
    /// <remarks>
    /// REACHABLE TODAY, NOT HYPOTHETICAL. A profile written before RemEx-07jij's validation fix can
    /// still carry something like "#FF0O00" (a capital O for a zero). The shell handles that with
    /// ThemeService's fallback seed; a gallery that instead rendered eight black cards, or threw
    /// inside a DataTemplate, would be the more visible half of the same bad value.
    /// </remarks>
    [Fact]
    public void AnUnparseableLiveSeedFallsBackInsteadOfRenderingBlack()
    {
        var dynamic = SeedPresetCatalog.All.Single(p => p.Id == SeedPresetCatalog.DynamicId);

        var palette = SeedPresetTileViewModel.PaletteFor(dynamic, "#FF0O00", "TonalSpot", false, 0.0);

        var expected = DynamicColorGenerator.Generate(
            Color.FromRgb(0x6C, 0x4C, 0xFF), "TonalSpot", isDark: true, contrast: 0.0);
        palette.Primary.Should().Be(expected.Primary);
    }

    /// <summary>The four retired themes are still selectable under the ids profiles already carry.</summary>
    /// <remarks>
    /// LOSING FOUR NAMED THEMES MUST NOT READ AS LOSING FOUR THEMES. <c>ThemeId</c> is persisted
    /// verbatim, so an installed profile naming CyberNOC has to keep resolving to the CyberNOC
    /// preset — with its geometry — rather than falling through to the default and quietly
    /// reshaping every card on the next launch.
    /// </remarks>
    [Theory]
    [InlineData("BaseDarkGlass", "#6C4CFF", 16.0, 24.0, false)]
    [InlineData("CyberNOC", "#00F3FF", 2.0, 4.0, false)]
    [InlineData("SolarFlare", "#FFB800", 24.0, 48.0, true)]
    [InlineData("Monolith", "#0A84FF", 8.0, 12.0, false)]
    public void TheFourHomagesKeepTheirRetiredThemesSeedAndGeometry(
        string id, string seed, double corner, double remoteCorner, bool isLight)
    {
        SeedPresetCatalog.TryGet(id, out var preset).Should().BeTrue($"{id} is a persisted ThemeId");

        preset.Seed.Should().Be(seed);
        preset.CornerRadius.Should().Be(corner);
        preset.RemoteCardCornerRadius.Should().Be(remoteCorner);
        preset.IsLight.Should().Be(isLight);

        // The structural theme file has to be the preset's own, not a shared one: the four .axaml
        // files still carry per-theme geometry the seed cannot express.
        preset.BaseTheme.ToString().Should().Be(id);
    }

    /// <summary>
    /// Lower-cased and mixed-case ids resolve, because <c>Enum.TryParse(ignoreCase: true)</c> did.
    /// </summary>
    [Fact]
    public void IdResolutionIsCaseInsensitive_BecauseTheEnumParseItReplacedWas()
    {
        SeedPresetCatalog.TryGet("cybernoc", out var lower).Should().BeTrue();
        lower.Id.Should().Be("CyberNOC");

        SeedPresetCatalog.TryGet("SOLARFLARE", out var upper).Should().BeTrue();
        upper.Id.Should().Be("SolarFlare");
    }

    /// <summary>An id from nowhere resolves to the default rather than throwing or returning null.</summary>
    [Fact]
    public void AnUnknownIdReportsTheMissAndStillYieldsARenderablePreset()
    {
        SeedPresetCatalog.TryGet("NoSuchTheme", out var preset).Should().BeFalse();
        preset.Id.Should().Be(SeedPresetCatalog.DefaultId);

        SeedPresetCatalog.TryGet(null, out var forNull).Should().BeFalse();
        forNull.Id.Should().Be(SeedPresetCatalog.DefaultId);

        SeedPresetCatalog.TryGet("   ", out var forBlank).Should().BeFalse();
        forBlank.Id.Should().Be(SeedPresetCatalog.DefaultId);
    }

    /// <summary>Ids are the persistence key, so two presets sharing one is a silent data loss.</summary>
    [Fact]
    public void EveryPresetIdIsUnique()
    {
        var ids = SeedPresetCatalog.All.Select(p => p.Id).ToArray();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));
    }

    /// <summary>
    /// Only Dynamic may leave an input unchosen. Any other preset with a null is a preset that
    /// silently inherits half the outgoing theme.
    /// </summary>
    [Fact]
    public void OnlyDynamicDeclinesToChooseItsInputs()
    {
        foreach (var preset in SeedPresetCatalog.All.Where(p => p.Id != SeedPresetCatalog.DynamicId))
        {
            preset.Seed.Should().NotBeNull($"{preset.Id} must pin a seed");
            preset.SchemeVariant.Should().NotBeNull($"{preset.Id} must pin a scheme variant");
            preset.IsLight.Should().NotBeNull($"{preset.Id} must pin its light/dark");
            preset.Contrast.Should().NotBeNull($"{preset.Id} must pin its contrast");
        }
    }

    /// <summary>Every seed parses and every variant is one the generator actually recognises.</summary>
    /// <remarks>
    /// <c>DynamicColorGenerator.StyleFor</c> falls through to TonalSpot for anything it does not
    /// know, so a typo'd variant is not an error — it is a preset that silently renders as a
    /// different one. This is the only place that can catch it.
    /// </remarks>
    [Fact]
    public void EverySeedParsesAndEveryVariantIsRecognised()
    {
        var known = new HashSet<string>(SchemeVariants.All, StringComparer.Ordinal);

        foreach (var preset in SeedPresetCatalog.All)
        {
            if (preset.Seed is { } seed)
                Color.TryParse(seed, out _).Should().BeTrue($"{preset.Id}: unparseable seed {seed}");

            if (preset.SchemeVariant is { } variant)
                known.Should().Contain(variant, $"{preset.Id}: {variant} would silently render as TonalSpot");

            if (preset.Contrast is { } contrast)
                contrast.Should().BeInRange(-1.0, 1.0, $"{preset.Id}: contrast is clamped to [-1, 1]");

            preset.GlassOpacity.Should().BeInRange(0.0, 1.0);
            preset.GlowStrength.Should().BeGreaterOrEqualTo(0.0);
        }
    }

    /// <summary>The gallery ships more than the four it replaced, and offers both modes.</summary>
    /// <remarks>
    /// Not decoration: the bead's whole argument for the catalog is that presets are cheap now. A
    /// catalog that shipped exactly the four homages would have paid the refactor's cost for none of
    /// its benefit, and "add a handful that only make sense now" would have quietly not happened.
    /// </remarks>
    [Fact]
    public void TheGalleryOffersMoreThanTheFourItReplaced_InBothModes()
    {
        SeedPresetCatalog.All.Should().HaveCountGreaterThan(5);
        SeedPresetCatalog.All.Where(p => p.IsLight == true).Should().HaveCountGreaterThan(1,
            "SolarFlare was the only light theme, and it is amber-on-cream rather than a neutral light mode");
        SeedPresetCatalog.All.Where(p => p.IsLight == false).Should().HaveCountGreaterThan(1);
    }

    /// <summary>Every preset's display name exists in all nine locale files.</summary>
    /// <remarks>
    /// A MISSING KEY IS NOT AN ERROR IN THIS APP, it renders as the key itself — so a preset added
    /// without its name would ship a tile labelled "Custom_PresetVoltage" in every language and
    /// nothing would fail. The nine-file parity check catches a key missing from SOME files; this
    /// catches one missing from ALL of them, which parity alone reads as consistent.
    /// </remarks>
    [Fact]
    public void EveryPresetNameKeyExistsInEveryLocale()
    {
        var files = Directory.GetFiles(
            Path.Combine(RepoRoot(), "remex.desktop", "Localization"), "Strings*.resx");

        files.Should().HaveCountGreaterOrEqualTo(9, "the app ships nine locales");

        foreach (var file in files)
        {
            var keys = new HashSet<string>(
                XDocument.Load(file).Root!.Elements("data")
                    .Select(d => d.Attribute("name")?.Value ?? string.Empty),
                StringComparer.Ordinal);

            foreach (var preset in SeedPresetCatalog.All)
            {
                keys.Should().Contain(preset.NameKey,
                    $"{Path.GetFileName(file)} is missing {preset.NameKey} for preset {preset.Id}");
            }
        }
    }

    /// <summary>
    /// The gallery is bound to the catalog, and no preset tile is hand-authored in the view any more.
    /// </summary>
    /// <remarks>
    /// THERE IS NO HEADLESS RENDER HERE, so this reads the axaml as source. It is the only way to
    /// catch the regression that matters: someone adding a ninth preset by pasting a Border with
    /// literal colours next to the ItemsControl, which is precisely how the five tiles this replaced
    /// came to be showing #050505 for a theme whose surface had not been #050505 for a release.
    /// </remarks>
    [Fact]
    public void ThePersonalizationPanelBindsTheGalleryRatherThanListingPresets()
    {
        var axaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

        axaml.Should().Contain("ItemsSource=\"{Binding ThemePresets}\"",
            "the gallery has to come from the catalog");
        axaml.Should().Contain("SelectThemeCommand");

        // Exactly three tile templates, not three templates plus leftovers. The preset gallery, the
        // scheme-variant strip row (RemEx-lrxyo) and the saved-palettes row (RemEx-ddynd) are all
        // catalog/collection-driven galleries sharing the .tile role from RemEx-z7pnx; a fourth
        // would be a hand-authored duplicate of one of them.
        Regex.Matches(axaml, @"Classes=""tile""").Should().HaveCount(3,
            "the preset gallery, the scheme-variant strip row and the saved-palettes row are the only three tile templates");

        // And no colour literal survives INSIDE either gallery. Scoped to the ItemsControls on
        // purpose: the accent quick-pick row further down the panel is literals by design — those
        // buttons set a seed rather than describe one — so a whole-file scan would fail for the
        // wrong reason and get deleted rather than fixed.
        var gallery = Regex.Match(axaml, @"<ItemsControl ItemsSource=""\{Binding ThemePresets\}"".*?</ItemsControl>",
            RegexOptions.Singleline);
        gallery.Success.Should().BeTrue("the gallery block has to be findable for this scan to mean anything");

        Regex.Match(gallery.Value, @"#[0-9A-Fa-f]{6}").Success.Should().BeFalse(
            "a colour literal in the gallery is a tile describing a palette instead of rendering it");

        axaml.Should().Contain("ItemsSource=\"{Binding SchemeVariantStrips}\"",
            "the variant row has to come from the same live-generated strips the seed change repaints");
        axaml.Should().Contain("SelectSchemeVariantCommand");

        var variantStrip = Regex.Match(axaml, @"<ItemsControl ItemsSource=""\{Binding SchemeVariantStrips\}"".*?</ItemsControl>",
            RegexOptions.Singleline);
        variantStrip.Success.Should().BeTrue("the variant strip block has to be findable for this scan to mean anything");

        Regex.Match(variantStrip.Value, @"#[0-9A-Fa-f]{6}").Success.Should().BeFalse(
            "a colour literal in the variant strip row is a swatch describing a palette instead of rendering it");

        // WHICH two blocks own the tiles, not just how many exist (review LOW, RemEx-lrxyo). A bare
        // count of 2 stays green if someone deletes the gallery's tile and hand-authors one
        // somewhere else — the exact substitution the original "exactly one" assertion was written
        // to catch. Both scoped blocks are already in hand, so pinning one tile inside each costs
        // two lines and restores the precision the count lost when it went from 1 to 2.
        Regex.Matches(gallery.Value, @"Classes=""tile""").Should().HaveCount(1,
            "the preset gallery owns exactly one of the two tile templates");
        Regex.Matches(variantStrip.Value, @"Classes=""tile""").Should().HaveCount(1,
            "the variant strip row owns exactly one of the two tile templates");
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
