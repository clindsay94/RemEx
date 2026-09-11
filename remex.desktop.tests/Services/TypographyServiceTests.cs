using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-jt6w5.2: the Avalonia half. No Application.Current in this assembly, so this proves what
/// lands in the override dictionary — the same seam <c>HardwareAccentInjectionTests</c> uses for
/// <c>ThemeService</c>. Whether the keys then reach a TextBlock is Task 3's XAML plus the eyes pass.
/// </summary>
public class TypographyServiceTests
{
    [Fact]
    public void AFreshService_AlreadyCarriesTheDefaults_ForEveryKey()
    {
        var service = new TypographyService();

        service.Overrides["Typo.Headline6.FontSize"].Should().Be(20.0);
        service.Overrides["Typo.Headline6.FontWeight"].Should().Be(FontWeight.Medium);
        service.Overrides["Typo.SensorMetricName.FontSize"].Should().Be(10.0);
        service.Overrides[TypographyResolver.SensorTitleBackdropKey].Should().Be(false);

        foreach (var member in TypographyResolver.Members)
        {
            service.Overrides.ContainsKey(member.FontSizeKey).Should().BeTrue(member.Key);
            service.Overrides.ContainsKey(member.FontWeightKey).Should().BeTrue(member.Key);
        }

        var effect = service.Overrides[TypographyResolver.ShadowKey(TypographySection.Headers)].Should().BeOfType<DropShadowEffect>().Subject;
        effect.BlurRadius.Should().BeApproximately(3.8, 1e-9);
        effect.Opacity.Should().BeApproximately(0.57, 1e-9);
        effect.OffsetX.Should().Be(0);
        effect.OffsetY.Should().Be(1);
        effect.Color.Should().Be(TypographyService.DefaultSurface);
    }

    [Fact]
    public void ShadowOff_RemovesEverySectionEffectKey_SoTheSetterGoesUnset()
    {
        var service = new TypographyService();

        service.Apply(new TypographySettings { ShadowEnabled = false }, TypographyService.DefaultSurface);

        foreach (var section in new[] { TypographySection.Headers, TypographySection.Body, TypographySection.Small, TypographySection.Sensor })
            service.Overrides.ContainsKey(TypographyResolver.ShadowKey(section)).Should().BeFalse(section.ToString());
        service.Overrides["Typo.Headline6.FontSize"].Should().Be(20.0, "sizes are untouched by the shadow switch");
    }

    [Fact]
    public void Apply_ReplacesTheWholeSet_AndRecordsWhatItApplied()
    {
        var service = new TypographyService();
        var surface = Color.FromRgb(0xFA, 0xFA, 0xFA);

        service.Apply(new TypographySettings { SensorBold = true, SensorScale = 1.5, SensorTitleBackdrop = true, ShadowStrength = 100 }, surface);

        service.Overrides["Typo.SensorTitle.FontSize"].Should().Be(18.0);
        service.Overrides["Typo.SensorTitle.FontWeight"].Should().Be(FontWeight.Bold);
        service.Overrides["Typo.SensorMetricName.FontWeight"].Should().Be(FontWeight.Bold);
        service.Overrides["Typo.Headline6.FontWeight"].Should().Be(FontWeight.Medium, "another section");
        service.Overrides[TypographyResolver.SensorTitleBackdropKey].Should().Be(true);
        var effect = (DropShadowEffect)service.Overrides[TypographyResolver.ShadowKey(TypographySection.Sensor)]!;
        effect.Color.Should().Be(surface);
        effect.BlurRadius.Should().Be(8);
        service.LastApplied!.SensorTitleBackdrop.Should().BeTrue();
        service.LastApplied.DefaultFontSize.Should().Be(14);
    }

    [Fact]
    public void OneEffectInstance_IsSharedAcrossSections()
    {
        var service = new TypographyService();

        var headers = service.Overrides[TypographyResolver.ShadowKey(TypographySection.Headers)];
        var body = service.Overrides[TypographyResolver.ShadowKey(TypographySection.Body)];

        headers.Should().BeSameAs(body, "one DropShadowEffect per apply, referenced by every shadowed section");
    }
}
