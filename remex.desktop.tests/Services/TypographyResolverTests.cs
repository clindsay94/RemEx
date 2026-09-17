using System;
using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>RemEx-jt6w5.2: the pure half — member table → resource values, no Avalonia application needed.</summary>
public class TypographyResolverTests
{
    private static readonly Color Dark = Color.FromRgb(0x12, 0x12, 0x12);
    private static readonly Color Light = Color.FromRgb(0xFA, 0xFA, 0xFA);

    [Fact]
    public void TheTable_HasEverySectionsReferenceMember_AndUniqueKeys()
    {
        TypographyResolver.Members.Select(m => m.Key).Should().OnlyHaveUniqueItems();
        foreach (var (section, key) in TypographyResolver.ReferenceMember)
        {
            var member = TypographyResolver.Member(key);
            member.Section.Should().Be(section, $"{key} is the reference member of {section}");
        }
        TypographyResolver.Members.Should().Contain(m => m.Key == "SensorTitle" && m.BaseSize == 12 && m.BaseWeight == FontWeight.Bold);
        TypographyResolver.Members.Should().Contain(m => m.Key == "SensorMetricName" && m.BaseSize == 10 && m.BaseWeight == FontWeight.Regular);
    }

    [Fact]
    public void Scale_MultipliesEachMembersOwnBase_AndOnlyItsSection()
    {
        var settings = new TypographySettings { HeadersScale = 1.25 };

        var r = TypographyResolver.Resolve(settings, Dark);

        r.FontSizes["Typo.Headline5.FontSize"].Should().Be(30);
        r.FontSizes["Typo.Headline6.FontSize"].Should().Be(25);
        r.FontSizes["Typo.Subtitle1.FontSize"].Should().Be(20);
        r.FontSizes["Typo.PageTitle.FontSize"].Should().Be(37.5);
        r.FontSizes["Typo.CardTitle.FontSize"].Should().Be(22.5);
        r.FontSizes["Typo.Body2.FontSize"].Should().Be(14, "Body is a different section");
        r.FontSizes["Typo.Caption.FontSize"].Should().Be(12);
        r.FontSizes["Typo.SensorTitle.FontSize"].Should().Be(12);
        r.DefaultFontSize.Should().Be(14, "untagged text follows Body, not Headers");
    }

    [Fact]
    public void BodyScale_ReachesUntaggedText_ThroughTheDefaultFontSize()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { BodyScale = 1.5 }, Dark);

        r.DefaultFontSize.Should().Be(21);
        r.FontSizes["Typo.Body2.FontSize"].Should().Be(21);
        r.FontSizes["Typo.PageSubtitle.FontSize"].Should().Be(12, "PageSubtitle moved to its own Subtitles section (RemEx-n6csl) and no longer follows Body");
    }

    [Fact]
    public void SubtitlesScale_ReachesPageSubtitle_AndOnlyPageSubtitle()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { SubtitlesScale = 1.5 }, Dark);

        r.FontSizes["Typo.PageSubtitle.FontSize"].Should().Be(18);
        r.FontSizes["Typo.Body2.FontSize"].Should().Be(14, "Body is a different section from Subtitles");
    }

    [Fact]
    public void SubtitlesBold_FlipsOnlyPageSubtitlesWeight()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { SubtitlesBold = true }, Dark);

        r.FontWeights["Typo.PageSubtitle.FontWeight"].Should().Be(FontWeight.Bold);
        r.FontWeights["Typo.Body2.FontWeight"].Should().Be(FontWeight.Regular, "Bold on Subtitles must not reach Body");
    }

    [Fact]
    public void BoldOff_IsEachMembersOwnWeight_NotRegular()
    {
        var r = TypographyResolver.Resolve(TypographySettings.Default, Dark);

        r.FontWeights["Typo.Headline6.FontWeight"].Should().Be(FontWeight.Medium);
        r.FontWeights["Typo.PageTitle.FontWeight"].Should().Be(FontWeight.Black);
        r.FontWeights["Typo.CardTitle.FontWeight"].Should().Be(FontWeight.Black);
        r.FontWeights["Typo.PageSubtitle.FontWeight"].Should().Be(FontWeight.Medium);
        r.FontWeights["Typo.SensorTitle.FontWeight"].Should().Be(FontWeight.Bold);
        r.FontWeights["Typo.Body2.FontWeight"].Should().Be(FontWeight.Regular);
        r.UntaggedBold.Should().BeFalse();
        r.FontWeights[TypographyResolver.UntaggedBoldFontWeightKey].Should().Be(FontWeight.Normal,
            "always present -- Normal when off, never absent, never a sentinel");
    }

    [Fact]
    public void BoldOn_BoldsEveryMemberOfTheSection_AndNothingElse()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { HeadersBold = true, BodyBold = true }, Dark);

        foreach (var m in TypographyResolver.Members.Where(m => m.Section is TypographySection.Headers or TypographySection.Body))
            r.FontWeights[m.FontWeightKey].Should().Be((FontWeight)Math.Max((int)FontWeight.Bold, (int)m.BaseWeight), m.Key);
        foreach (var m in TypographyResolver.Members.Where(m => m.Section is TypographySection.Small or TypographySection.Sensor))
            r.FontWeights[m.FontWeightKey].Should().Be(m.BaseWeight, m.Key);
        r.UntaggedBold.Should().BeTrue();
        r.FontWeights[TypographyResolver.UntaggedBoldFontWeightKey].Should().Be(FontWeight.Bold,
            "always present -- Bold when on");
    }

    [Fact]
    public void BoldOn_NeverThinsABaseWeightHeavierThanBold_PageTitleAndCardTitleStayBlack()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { HeadersBold = true }, Dark);

        r.FontWeights["Typo.PageTitle.FontWeight"].Should().Be(FontWeight.Black);
        r.FontWeights["Typo.CardTitle.FontWeight"].Should().Be(FontWeight.Black);
    }

    [Fact]
    public void BoldOn_RaisesALighterBaseWeightToBold_Headline6MediumToBold()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { HeadersBold = true }, Dark);

        r.FontWeights["Typo.Headline6.FontWeight"].Should().Be(FontWeight.Bold);
    }

    [Fact]
    public void BoldOn_RaisesRegularBaseWeightToBold_CaptionRegularToBold()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { SmallBold = true }, Dark);

        r.FontWeights["Typo.Caption.FontWeight"].Should().Be(FontWeight.Bold);
    }

    [Theory]
    [InlineData(0, 1.0, 0.35)]
    [InlineData(40, 3.8, 0.57)]
    [InlineData(100, 8.0, 0.90)]
    public void Strength_MapsLinearly_ToBlurAndOpacity(int strength, double blur, double opacity)
    {
        TypographyResolver.ShadowBlurRadius(strength).Should().BeApproximately(blur, 1e-9);
        TypographyResolver.ShadowOpacity(strength).Should().BeApproximately(opacity, 1e-9);

        var r = TypographyResolver.Resolve(new TypographySettings { ShadowStrength = strength }, Dark);
        var shadow = r.SectionShadows[TypographySection.Headers];
        shadow.Should().NotBeNull();
        shadow!.BlurRadius.Should().BeApproximately(blur, 1e-9);
        shadow.Opacity.Should().BeApproximately(opacity, 1e-9);
    }

    [Fact]
    public void HaloColour_IsTheSurface_OpaqueInBothModes()
    {
        TypographyResolver.Resolve(TypographySettings.Default, Dark).SectionShadows[TypographySection.Body]!.Color.Should().Be(Dark);
        TypographyResolver.Resolve(TypographySettings.Default, Light).SectionShadows[TypographySection.Body]!.Color.Should().Be(Light);
        TypographyResolver.ShadowColor(Color.FromArgb(0x40, 0x10, 0x20, 0x30)).A.Should().Be(255,
            "alpha comes from the effect's Opacity, never from the surface");
    }

    [Fact]
    public void ShadowSwitch_GovernsEverySection()
    {
        var on = TypographyResolver.Resolve(TypographySettings.Default, Dark);
        foreach (var section in TypographyResolver.ShadowedSections)
            on.SectionShadows[section].Should().NotBeNull(section.ToString());

        var off = TypographyResolver.Resolve(new TypographySettings { ShadowEnabled = false }, Dark);
        off.SectionShadows.Values.Should().OnlyContain(s => s == null);
    }

    [Fact]
    public void ShadowedSections_IsTheSpecDefault_AllFive()
    {
        // Task 6 (RemEx-jt6w5.6) may shrink this to Headers + Sensor if the measured cost says so;
        // change this assertion in the same commit as the table, with the measurement's path in the message.
        // Subtitles joined the set in RemEx-n6csl: page-subtitle wore Body's halo before it had its
        // own row, so its look is preserved.
        TypographyResolver.ShadowedSections.Should().BeEquivalentTo(new[]
        {
            TypographySection.Headers, TypographySection.Subtitles, TypographySection.Body, TypographySection.Small, TypographySection.Sensor,
        });
    }

    [Fact]
    public void ShadowKey_ForSubtitles_IsTypoSubtitlesEffect()
    {
        TypographyResolver.ShadowKey(TypographySection.Subtitles).Should().Be("Typo.Subtitles.Effect");
    }

    [Theory]
    [InlineData(TypographySection.Headers, 1.0, 20, 20)]
    [InlineData(TypographySection.Headers, 1.2, 20, 24)]
    [InlineData(TypographySection.Subtitles, 1.5, 12, 18)]
    [InlineData(TypographySection.Body, 1.05, 14, 15)]
    [InlineData(TypographySection.Small, 0.8, 12, 10)]
    [InlineData(TypographySection.Sensor, 1.5, 12, 18)]
    [InlineData(TypographySection.Sensor, 9.0, 12, 19)]   // clamped to 1.60 first
    public void ReferenceAndEffectivePoints_AreWholeNumbers_FromTheReferenceMember(TypographySection section, double scale, int reference, int effective)
    {
        TypographyResolver.ReferencePoints(section).Should().Be(reference);
        TypographyResolver.EffectivePoints(section, scale).Should().Be(effective);
    }

    [Fact]
    public void Resolve_NormalizesFirst_SoOutOfRangeSettingsNeverReachTheResources()
    {
        var r = TypographyResolver.Resolve(new TypographySettings { SmallScale = 40, ShadowStrength = -5 }, Dark);

        r.FontSizes["Typo.Caption.FontSize"].Should().Be(12 * TypographySettings.MaxScale);
        r.SectionShadows[TypographySection.Small]!.BlurRadius.Should().Be(1.0);
    }
}
