using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Tests;

public class DashboardProfileTests
{
    [Fact]
    public void DashboardProfile_WolDefaultValues_AreCorrect()
    {
        var profile = new DashboardProfile();
        Assert.Equal(string.Empty, profile.WolMacAddress);
        Assert.Equal("255.255.255.255", profile.WolBroadcastIp);
        Assert.Equal(9, profile.WolPort);
    }

    // ═══════════════ Default Values ═══════════════

    [Fact]
    public void DashboardProfile_DefaultValues_AreCorrect()
    {
        var profile = new DashboardProfile();

        Assert.Equal("Default", profile.ProfileName);
        Assert.False(profile.IsSnapToGridEnabled);
        Assert.Equal(50, profile.GridSize);
        Assert.Equal("wss://localhost:5005/ws", profile.HostAddress);
        Assert.Empty(profile.Cards);
        Assert.Empty(profile.PinnedSensorIds);
    }

    [Fact]
    public void CardState_DefaultValues_AreCorrect()
    {
        var card = new CardState();

        Assert.Equal(string.Empty, card.CardId);
        Assert.Equal(string.Empty, card.CardType);
        Assert.Null(card.SensorId);
        Assert.Equal(0, card.PositionX);
        Assert.Equal(0, card.PositionY);
        Assert.Equal(220, card.Width);
        Assert.Equal(160, card.Height);
        Assert.Equal(0, card.ZIndex);
    }

    // ═══════════════ Serialization Round-Trip ═══════════════

    [Fact]
    public void DashboardProfile_Serialization_RoundTripsCorrectly()
    {
        var original = new DashboardProfile
        {
            ProfileName = "Gaming Mode",
            IsSnapToGridEnabled = true,
            GridSize = 75,
            HostAddress = "ws://192.168.1.100:5005/ws",
            Cards = new List<CardState>
            {
                new CardState
                {
                    CardId = "card-1",
                    CardType = "Sensor",
                    SensorId = "CPU Package Temp",
                    PositionX = 100.5,
                    PositionY = 200.25,
                    Width = 300,
                    Height = 180,
                    ZIndex = 5,
                },
                new CardState
                {
                    CardId = "card-2",
                    CardType = "Connection",
                    PositionX = 0,
                    PositionY = 0,
                    Width = 240,
                    Height = 180,
                    ZIndex = 1,
                },
            },
            PinnedSensorIds = new List<string> { "CPU Package Temp", "GPU Temp" },
        };

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<DashboardProfile>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(original.ProfileName, deserialized.ProfileName);
        Assert.Equal(original.IsSnapToGridEnabled, deserialized.IsSnapToGridEnabled);
        Assert.Equal(original.GridSize, deserialized.GridSize);
        Assert.Equal(original.HostAddress, deserialized.HostAddress);
        Assert.Equal(2, deserialized.Cards.Count);
        Assert.Equal("card-1", deserialized.Cards[0].CardId);
        Assert.Equal("Sensor", deserialized.Cards[0].CardType);
        Assert.Equal("CPU Package Temp", deserialized.Cards[0].SensorId);
        Assert.Equal(100.5, deserialized.Cards[0].PositionX);
        Assert.Equal(200.25, deserialized.Cards[0].PositionY);
        Assert.Equal(300, deserialized.Cards[0].Width);
        Assert.Equal(180, deserialized.Cards[0].Height);
        Assert.Equal(5, deserialized.Cards[0].ZIndex);
        Assert.Equal(2, deserialized.PinnedSensorIds.Count);
        Assert.Contains("GPU Temp", deserialized.PinnedSensorIds);
    }

    // ═══════════════ Per-Card Customization (RemEx-rg28) ═══════════════

    [Fact]
    public void CardState_CustomizationDefaults_AreCorrect()
    {
        var card = new CardState();

        Assert.Null(card.CustomTitle);
        Assert.True(card.ShowValueOverlay);
        Assert.Null(card.CardTheme);
        Assert.Equal(GraphType.Auto, card.DisplayMode);
    }

    /// <summary>
    /// Round-trips the new per-card customization through the SOURCE-GENERATED context (not the
    /// reflection serializer), which is what actually ships in the NativeAOT `libRemexCore.so`.
    /// A missing [JsonSerializable(typeof(SensorCardTheme))] registration would fail here.
    /// </summary>
    [Fact]
    public void DashboardProfile_SourceGen_RoundTripsCardCustomization()
    {
        var original = new DashboardProfile
        {
            Cards = new List<CardState>
            {
                new CardState
                {
                    CardId = "c1",
                    CardType = "Sensor",
                    SensorId = "GPU Temp",
                    DisplayMode = GraphType.Line,
                    CustomTitle = "My GPU",
                    ShowValueOverlay = false,
                    CardTheme = SensorCardTheme.Presets[1], // "Magenta & Cyan"
                },
            },
        };

        var typeInfo = RemexJson.TypeInfo<DashboardProfile>();
        var json = RemexJson.Serialize(original, typeInfo);
        var back = RemexJson.Deserialize(json, typeInfo);

        Assert.NotNull(back);
        var card = Assert.Single(back!.Cards);
        Assert.Equal(GraphType.Line, card.DisplayMode);
        Assert.Equal("My GPU", card.CustomTitle);
        Assert.False(card.ShowValueOverlay);
        Assert.NotNull(card.CardTheme);
        Assert.Equal("Magenta & Cyan", card.CardTheme!.Name);
        Assert.Equal("#00E5FF", card.CardTheme.AccentColor);
    }

    /// <summary>
    /// The personalization sheet's new fields (RemEx-ddynd) round-trip through the source-generated
    /// context, camelCase names included — the same NativeAOT-safe path the desktop's profile file
    /// actually uses.
    /// </summary>
    [Fact]
    public void CustomizationSettings_SourceGen_RoundTripsSavedPalettes()
    {
        var settings = new CustomizationSettings
        {
            ColorSource = ColorSources.Wallpaper,
            WallpaperSeedIndex = 2,
            WallpaperSource = WallpaperSources.Image,
            WallpaperImagePath = @"C:\Users\x\wallpaper-abc.png",
            WallpaperBlur = 0.35,
            SavedPalettes = new[]
            {
                new SavedPalette { Name = "Dusk", ColorSource = ColorSources.Custom, Seed = "#123456", Vibrancy = 40, Contrast = -0.2, Strategy = "Expressive" },
            },
        };

        var typeInfo = RemexJson.TypeInfo<CustomizationSettings>();
        var json = RemexJson.Serialize(settings, typeInfo);
        var back = RemexJson.Deserialize(json, typeInfo);

        Assert.Contains("\"colorSource\"", json);
        Assert.Contains("\"savedPalettes\"", json);
        Assert.Contains("\"wallpaperBlur\"", json);

        Assert.NotNull(back);
        Assert.Equal(settings.ColorSource, back!.ColorSource);
        Assert.Equal(settings.WallpaperSeedIndex, back.WallpaperSeedIndex);
        Assert.Equal(settings.WallpaperSource, back.WallpaperSource);
        Assert.Equal(settings.WallpaperImagePath, back.WallpaperImagePath);
        Assert.Equal(settings.WallpaperBlur, back.WallpaperBlur);
        var savedPalette = Assert.Single(back.SavedPalettes);
        Assert.Equal(settings.SavedPalettes[0], savedPalette);
    }

    // ═══════════════ Snap-to-Grid Math ═══════════════

    [Theory]
    [InlineData(115, 50, 100)]   // 115/50 = 2.3, rounds to 2 → 100
    [InlineData(125, 50, 100)]   // 125/50 = 2.5, banker's rounding (to-even) → 2 → 100
    [InlineData(100, 50, 100)]   // Exact multiple → no change
    [InlineData(0, 50, 0)]       // Zero → zero
    [InlineData(24, 50, 0)]      // 24/50 = 0.48, rounds to 0 → 0
    [InlineData(26, 50, 50)]     // 26/50 = 0.52, rounds to 1 → 50
    [InlineData(37, 25, 25)]     // 37/25 = 1.48, rounds to 1 → 25
    [InlineData(38, 25, 50)]     // 38/25 = 1.52, rounds to 2 → 50
    public void SnapToGrid_SnapsCorrectly(double input, int gridSize, double expected)
    {
        var result = System.Math.Round(input / gridSize) * gridSize;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SnapToGrid_ExactMultiple_NoChange()
    {
        double input = 200;
        int gridSize = 50;
        var snapped = System.Math.Round(input / gridSize) * gridSize;
        Assert.Equal(200, snapped);
    }
}
