using System;
using System.IO;
using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-rpvde: what an ABSENT key reads as when a saved profile is loaded.
/// </summary>
/// <remarks>
/// The records document defaults (an absent <c>schemeVariant</c> reads as TonalSpot, an absent
/// <c>customization</c> as a fresh <see cref="CustomizationSettings"/>, a partial <c>typography</c>
/// keeps its other defaults). Those hold ONLY for the reflection-based reader the profile is actually
/// loaded through (<see cref="DashboardLayoutService.JsonOptions"/>): it calls the parameterless
/// constructor and sets just the keys present. The source-generated metadata in
/// <see cref="RemexJsonSerializerContext"/> sets every init-only property, so there an absent key
/// reads as null / 0 / false. These tests pin the real reader to the documented defaults, and pin the
/// trap so nobody "optimises" the profile load onto the source-generated context.
/// </remarks>
public sealed class ProfileAbsentKeyDefaultsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remex-absent-keys-" + Guid.NewGuid().ToString("N"));

    public ProfileAbsentKeyDefaultsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DashboardProfile Load(string json)
    {
        var path = Path.Combine(_dir, "profile.json");
        File.WriteAllText(path, json);
        return DashboardLayoutService.ReadAndMigrate(path, out _)!;
    }

    [Fact]
    public void AnEmptyProfileReadsAsTheDocumentedDefaults()
    {
        var profile = Load("{}");
        var fresh = new DashboardProfile();

        Assert.NotNull(profile.Customization);
        Assert.Equal(fresh.GridSize, profile.GridSize);
        Assert.Equal(fresh.WolPort, profile.WolPort);
        Assert.Equal(fresh.CloseToTray, profile.CloseToTray);
        Assert.NotNull(profile.Cards);
        Assert.NotNull(profile.PinnedSensorIds);
    }

    [Fact]
    public void APartialCustomizationKeepsEveryUnwrittenDefault()
    {
        var raw = JsonSerializer.Deserialize<DashboardProfile>(
            "{\"customization\":{\"uiScale\":1.1,\"typography\":{\"headersScale\":1.2}}}",
            DashboardLayoutService.JsonOptions)!;
        var fresh = new CustomizationSettings();

        Assert.Equal(1.1, raw.Customization.UiScale);
        Assert.Equal("TonalSpot", raw.Customization.SchemeVariant);
        Assert.Equal(fresh.CornerRadius, raw.Customization.CornerRadius);
        Assert.Equal(fresh.BodyFontFamily, raw.Customization.BodyFontFamily);
        Assert.Equal(1.2, raw.Customization.Typography.HeadersScale);
        Assert.True(raw.Customization.Typography.ShadowEnabled, "an unwritten bool must not read as a deliberate off");
        Assert.Equal(new TypographySettings().ShadowStrength, raw.Customization.Typography.ShadowStrength);
    }

    [Fact]
    public void AnEmptySavedPaletteKeepsItsDefaults()
    {
        var raw = JsonSerializer.Deserialize<DashboardProfile>(
            "{\"customization\":{\"schemaVersion\":3,\"savedPalettes\":[{}]}}",
            DashboardLayoutService.JsonOptions)!;
        var fresh = new SavedPalette();

        var palette = Assert.Single(raw.Customization.SavedPalettes);
        Assert.Equal(fresh.Vibrancy, palette.Vibrancy);
        Assert.Equal(fresh.Strategy, palette.Strategy);
        Assert.Equal(fresh.Seed, palette.Seed);
    }

    [Fact]
    public void TheSourceGeneratedContextDropsTheDefaults_SoProfilesMustNotBeReadThroughIt()
    {
        // Pins the trap the remarks above describe. If a .NET update ever makes the source generator
        // honour initializers, this goes red and the warning in the records' docs can be retired.
        var viaSourceGen = JsonSerializer.Deserialize("{}", RemexJsonSerializerContext.Default.DashboardProfile)!;

        Assert.Null(viaSourceGen.Customization);
        Assert.Equal(0, viaSourceGen.GridSize);
    }
}
