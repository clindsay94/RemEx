using System.IO;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-jt6w5.2: the JOIN. Typography is applied from inside <c>ThemeService.ApplyCustomizationCore</c>,
/// with the generated palette's surface, so profile load and every settings change reach it with
/// no second wire — and so the halo is dark in Dark and light in Light by construction.
/// </summary>
public class ThemeServiceTypographyTests
{
    private static ThemeService NewTheme() => new() { PostToUiThread = action => action() };

    [Fact]
    public void ApplyCustomizationCore_AppliesTypography_FromTheSameSettings()
    {
        var theme = NewTheme();

        theme.ApplyCustomizationCore(new CustomizationSettings
        {
            ThemeMode = "Dark",
            Typography = new TypographySettings { HeadersScale = 1.5, SensorTitleBackdrop = true },
        });

        var applied = theme.Typography.LastApplied;
        applied.Should().NotBeNull();
        applied!.FontSizes["Typo.Headline6.FontSize"].Should().Be(30);
        applied.SensorTitleBackdrop.Should().BeTrue();
        theme.Typography.Overrides["Typo.Headline6.FontSize"].Should().Be(30.0);
    }

    [Fact]
    public void TheHalo_IsNearBlackInDark_AndNearWhiteInLight()
    {
        var theme = NewTheme();

        theme.ApplyCustomizationCore(new CustomizationSettings { ThemeMode = "Dark" });
        var dark = theme.Typography.LastApplied!.SectionShadows[TypographySection.Headers]!.Color;

        theme.ApplyCustomizationCore(new CustomizationSettings { ThemeMode = "Light" });
        var light = theme.Typography.LastApplied!.SectionShadows[TypographySection.Headers]!.Color;

        (dark.R + dark.G + dark.B).Should().BeLessThan(200, "Dark surfaces are near-black");
        (light.R + light.G + light.B).Should().BeGreaterThan(600, "Light surfaces are near-white");
    }

    [Fact]
    public void ApplyCustomization_ReachesTypography_ThroughThePostedPath()
    {
        var theme = NewTheme();

        theme.ApplyCustomization(new CustomizationSettings { Typography = new TypographySettings { ShadowEnabled = false } });

        theme.Typography.Overrides.ContainsKey(TypographyResolver.ShadowKey(TypographySection.Body)).Should().BeFalse();
    }

    /// <summary>
    /// Task 1's decision: the source-generated deserializer skips property initializers for absent
    /// JSON keys, so a loaded profile can hand <c>Typography</c> to this method as null even though
    /// the property is annotated non-nullable. ApplyCustomizationCore must Normalize before handing
    /// off to TypographyService, not throw or propagate the null.
    /// </summary>
    [Fact]
    public void ApplyCustomizationCore_WithNullTypography_AppliesDefaults_InsteadOfThrowing()
    {
        var theme = NewTheme();

        // Apply a customised scale first so the constructor's own defaults can't make this test
        // pass vacuously — if ApplyCustomizationCore skipped Typography.Apply on null, Headline6
        // would still read 30 from this call, not 20, and the assertion below would catch it.
        theme.ApplyCustomizationCore(new CustomizationSettings
        {
            ThemeMode = "Dark",
            Typography = new TypographySettings { HeadersScale = 1.5 },
        });
        theme.Typography.LastApplied!.FontSizes["Typo.Headline6.FontSize"].Should().Be(30);

        var act = () => theme.ApplyCustomizationCore(new CustomizationSettings
        {
            ThemeMode = "Dark",
            Typography = null!,
        });

        act.Should().NotThrow();
        var applied = theme.Typography.LastApplied;
        applied.Should().NotBeNull();
        applied!.FontSizes["Typo.Headline6.FontSize"].Should().Be(20, "a null Typography must fall back to defaults, not keep the previous customisation");
        applied.DefaultFontSize.Should().Be(14);
    }

    /// <summary>
    /// RemEx-n6csl: the own-key mechanism (ThemeService.ApplyCustomizationCore, guarded by
    /// <c>if (Application.Current is { } app)</c>) writes <c>PageSubtitleFontFamily</c> straight onto
    /// <c>Application.Resources</c>, the same mechanism as <c>PageTitleFontFamily</c>. This suite runs
    /// with no Avalonia <c>Application</c> (see <see cref="ThemeResourcesTests"/> - "No Avalonia
    /// Application in a unit test" is this project's deliberate norm), so that branch never executes
    /// here and <c>theme.Typography.LastApplied</c>/<c>Overrides</c> - the seam every other test in
    /// this file exercises - cannot see font-family resources at all: they live in
    /// <see cref="TypographyResolver"/>'s FontSizes/FontWeights/SectionShadows, not fonts. Pinned at
    /// SOURCE level instead, the same way <see cref="Views.TypographyStylesTests"/> pins the App.axaml
    /// side of this same fallback.
    /// </summary>
    [Fact]
    public void PageSubtitleFontFamilyResource_FallsBackToPageTitleFontFamily_AtSourceLevel()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        source.Should().Contain(
            @"app.Resources[""PageSubtitleFontFamily""] = SystemFontService.ResolveFontOrDefault(",
            "PageSubtitleFontFamily must be written through the same own-key/guard mechanism as PageTitleFontFamily");
        source.Should().Contain(
            "settings.PageSubtitleFontFamily ?? settings.PageTitleFontFamily",
            "a profile with no chosen subtitle font must fall back to the title font, not silently reset to the Orbitron default");
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisSourceFile = "")
        => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
