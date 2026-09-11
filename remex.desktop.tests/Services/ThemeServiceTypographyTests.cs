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

        var act = () => theme.ApplyCustomizationCore(new CustomizationSettings
        {
            ThemeMode = "Dark",
            Typography = null!,
        });

        act.Should().NotThrow();
        var applied = theme.Typography.LastApplied;
        applied.Should().NotBeNull();
        applied!.FontSizes["Typo.Headline6.FontSize"].Should().Be(TypographySettings.Default.HeadersScale * 20);
        applied.DefaultFontSize.Should().Be(14);
    }
}
