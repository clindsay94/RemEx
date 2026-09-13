using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Tests;

/// <summary>
/// Flyout toolbar drop 1 (RemEx-4kv0g.18.5): <see cref="CustomizationSettings.FlyoutOpacity"/>,
/// <see cref="CustomizationSettings.FlyoutHiddenSensorIds"/>,
/// <see cref="CustomizationSettings.FlyoutHiddenTileIds"/> and
/// <see cref="CustomizationSettings.FlyoutAppIds"/> are read this task but have no Personalize
/// control yet (drop 2, RemEx-4kv0g.18.6). This project pins the record's own defaults and the
/// source-gen round trip only — the absent-key repair for a profile that predates these fields is
/// <c>CustomizationMigration</c>, which lives in <c>remex.desktop</c> and is pinned in
/// <c>remex.desktop.tests</c>'s <c>CustomizationMigrationTests</c> instead.
/// </summary>
public class CustomizationSettingsFlyoutDefaultsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new CustomizationSettings();

        Assert.Equal(1.0, settings.FlyoutOpacity);
        Assert.Empty(settings.FlyoutHiddenSensorIds);
        Assert.Empty(settings.FlyoutHiddenTileIds);
        Assert.Empty(settings.FlyoutAppIds);
    }

    /// <summary>
    /// A profile JSON written before these four fields existed — the exact shape every profile on
    /// disk has right now — must still LOAD, not throw. It does NOT land on the record's own
    /// defaults here: this pins the raw deserialization behaviour this record already has for
    /// every field (see <see cref="CustomizationSettings.Typography"/>'s own remarks) — an absent
    /// key becomes the CLR default for its declared type (0.0, <c>null</c> for the lists), not the
    /// property initializer, because <c>RemexJson</c>'s source-generated context does not run
    /// initializers for keys the payload omits.
    /// <para>
    /// THE REAL DEFAULT (1.0, empty lists) IS A MIGRATION CONCERN, NOT A SERIALIZATION ONE —
    /// <c>Remex.Desktop.Services.CustomizationMigration</c> lives in <c>remex.desktop</c>, so it is
    /// not reachable from this project; <c>CustomizationMigrationTests.ASchemaSixProfileAdoptsTheFlyoutDefaults</c>
    /// and <c>ArmSevenDropsNoField</c> (remex.desktop.tests) pin that a profile deserialised this
    /// way is repaired to the real default the moment it reaches <c>Migrate</c>, which every real
    /// load path (<c>DashboardLayoutService.LoadAsync</c>) already runs before anything else sees it.
    /// </para>
    /// </summary>
    [Fact]
    public void ProfileJsonWithoutTheFourKeys_DeserializesToClrDefaultsNotRecordDefaults()
    {
        const string legacyJson = """
            {
                "baseTheme": "BaseDarkGlass",
                "cornerRadius": 16
            }
            """;

        var typeInfo = RemexJson.TypeInfo<CustomizationSettings>();
        var settings = RemexJson.Deserialize(legacyJson, typeInfo);

        Assert.NotNull(settings);
        Assert.Equal(0.0, settings!.FlyoutOpacity);
        Assert.Null(settings.FlyoutHiddenSensorIds);
        Assert.Null(settings.FlyoutHiddenTileIds);
        Assert.Null(settings.FlyoutAppIds);
    }

    /// <summary>
    /// Source-gen round-trip through the NativeAOT-safe context, camelCase names included, mirroring
    /// <c>CustomizationSettings_SourceGen_RoundTripsSavedPalettes</c> for the four new fields.
    /// </summary>
    [Fact]
    public void SourceGen_RoundTripsFlyoutFields()
    {
        var appId = Guid.NewGuid();
        var settings = new CustomizationSettings
        {
            FlyoutOpacity = 0.4,
            FlyoutHiddenSensorIds = new List<string> { "CPU Package Temp" },
            FlyoutHiddenTileIds = new List<string> { "sleep", "pair" },
            FlyoutAppIds = new List<Guid> { appId },
        };

        var typeInfo = RemexJson.TypeInfo<CustomizationSettings>();
        var json = RemexJson.Serialize(settings, typeInfo);
        var back = RemexJson.Deserialize(json, typeInfo);

        Assert.Contains("\"flyoutOpacity\"", json);
        Assert.Contains("\"flyoutHiddenSensorIds\"", json);
        Assert.Contains("\"flyoutHiddenTileIds\"", json);
        Assert.Contains("\"flyoutAppIds\"", json);

        Assert.NotNull(back);
        Assert.Equal(0.4, back!.FlyoutOpacity);
        Assert.Equal(settings.FlyoutHiddenSensorIds, back.FlyoutHiddenSensorIds);
        Assert.Equal(settings.FlyoutHiddenTileIds, back.FlyoutHiddenTileIds);
        Assert.Equal(settings.FlyoutAppIds, back.FlyoutAppIds);
    }
}
