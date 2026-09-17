using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Tests;

/// <summary>RemEx-jt6w5.1: defaults, clamping and the source-generated JSON contract of the Text section.</summary>
public class TypographySettingsTests
{
    [Fact]
    public void Defaults_MatchTheSpecTable()
    {
        var t = new TypographySettings();

        Assert.Equal(1.0, t.HeadersScale);
        Assert.Equal(1.0, t.SubtitlesScale);
        Assert.Equal(1.0, t.BodyScale);
        Assert.Equal(1.0, t.SmallScale);
        Assert.Equal(1.0, t.SensorScale);
        Assert.False(t.HeadersBold);
        Assert.False(t.SubtitlesBold);
        Assert.False(t.BodyBold);
        Assert.False(t.SmallBold);
        Assert.False(t.SensorBold);
        Assert.False(t.SensorTitleBackdrop);
        Assert.True(t.ShadowEnabled);
        Assert.Equal(40, t.ShadowStrength);
        Assert.Equal(new TypographySettings(), TypographySettings.Default);
    }

    [Fact]
    public void Normalize_ClampsEveryRangedField_AndKeepsInRangeValues()
    {
        var wild = new TypographySettings
        {
            HeadersScale = 9, SubtitlesScale = -3, BodyScale = 0.1, SmallScale = double.NaN, SensorScale = -1,
            ShadowStrength = 250, HeadersBold = true,
        };

        var n = TypographySettings.Normalize(wild);

        Assert.Equal(TypographySettings.MaxScale, n.HeadersScale);
        Assert.Equal(1.0, n.SubtitlesScale);   // negative is "no value", not the floor
        Assert.Equal(TypographySettings.MinScale, n.BodyScale);
        Assert.Equal(1.0, n.SmallScale);   // NaN is "no value", not "the floor"
        Assert.Equal(1.0, n.SensorScale);  // negative likewise
        Assert.Equal(TypographySettings.MaxShadowStrength, n.ShadowStrength);
        Assert.True(n.HeadersBold);

        var fine = new TypographySettings { HeadersScale = 1.25, ShadowStrength = 0, ShadowEnabled = false };
        Assert.Equal(fine, TypographySettings.Normalize(fine));
    }

    [Fact]
    public void Normalize_Null_IsDefaults()
        => Assert.Equal(TypographySettings.Default, TypographySettings.Normalize(null));

    [Fact]
    public void CustomizationSettings_CarriesTypography_ThroughSourceGeneratedJson()
    {
        var original = new CustomizationSettings
        {
            Typography = new TypographySettings
            {
                HeadersScale = 1.3, SensorBold = true, ShadowEnabled = false, ShadowStrength = 75, SensorTitleBackdrop = true,
            },
        };

        var json = JsonSerializer.Serialize(original, RemexJsonSerializerContext.Default.CustomizationSettings);
        Assert.Contains("\"typography\"", json);
        Assert.Contains("\"shadowStrength\":75", json);

        var back = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.CustomizationSettings);
        Assert.NotNull(back);
        Assert.Equal(original.Typography, back!.Typography);
    }

    [Fact]
    public void OlderProfile_WithoutTypography_LoadsDefaults()
    {
        const string json = "{\"baseTheme\":\"BaseDarkGlass\",\"uiScale\":1.1}";

        var back = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.CustomizationSettings);

        Assert.NotNull(back);
        // CustomizationSettings has only init-only properties and no explicit constructor, so the
        // source-generated deserializer (System.Text.Json's documented behavior for that shape)
        // leaves a key absent from the payload at its raw CLR default rather than its C# field
        // initializer - the same reason an absent SchemaVersion reads back as 0, not a "migrated"
        // value. Typography is therefore null here; Normalize is the read-site guard (see its XML
        // remarks) that every consumer - CustomizationViewModel.BuildCurrentSettings included - runs
        // before touching the record.
        Assert.Null(back!.Typography);
        Assert.Equal(TypographySettings.Default, TypographySettings.Normalize(back.Typography));
        Assert.Equal(1.1, back.UiScale);
    }

    [Fact]
    public void ExplicitNullTypography_NormalizesToDefaults()
    {
        const string json = "{\"typography\":null}";

        var back = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.CustomizationSettings)!;

        Assert.Equal(TypographySettings.Default, TypographySettings.Normalize(back.Typography));
    }

    /// <summary>
    /// RemEx-n6csl: a profile written before <c>PageSubtitleFontFamily</c> existed has no
    /// <c>pageSubtitleFont</c> key at all. Null is the "follows PageTitleFontFamily" signal
    /// (ThemeService reads it as <c>settings.PageSubtitleFontFamily ?? settings.PageTitleFontFamily</c>),
    /// so an upgraded profile must deserialize to null here, not to Orbitron or to the empty string.
    /// </summary>
    [Fact]
    public void OlderProfile_WithoutPageSubtitleFont_DeserializesToNull()
    {
        const string json = "{\"baseTheme\":\"BaseDarkGlass\",\"pageTitleFont\":\"avares://Remex.Desktop/Assets/Fonts#Orbitron\"}";

        var back = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.CustomizationSettings)!;

        Assert.Null(back.PageSubtitleFontFamily);
        Assert.Equal("avares://Remex.Desktop/Assets/Fonts#Orbitron", back.PageTitleFontFamily);
    }

    [Fact]
    public void PageSubtitleFontFamily_RoundTrips_ThroughSourceGeneratedJson()
    {
        var original = new CustomizationSettings { PageSubtitleFontFamily = "Comic Sans MS" };

        var json = JsonSerializer.Serialize(original, RemexJsonSerializerContext.Default.CustomizationSettings);
        Assert.Contains("\"pageSubtitleFont\":\"Comic Sans MS\"", json);

        var back = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.CustomizationSettings)!;
        Assert.Equal("Comic Sans MS", back.PageSubtitleFontFamily);
    }
}
