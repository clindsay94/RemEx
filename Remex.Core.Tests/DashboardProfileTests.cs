using System.Collections.Generic;
using System.Text.Json;
using Remex.Core.Models;

namespace Remex.Core.Tests;

public class DashboardProfileTests
{
    // ═══════════════ Default Values ═══════════════

    [Fact]
    public void DashboardProfile_DefaultValues_AreCorrect()
    {
        var profile = new DashboardProfile();

        Assert.Equal("Default", profile.ProfileName);
        Assert.False(profile.IsSnapToGridEnabled);
        Assert.Equal(50, profile.GridSize);
        Assert.Equal("ws://localhost:5005/ws", profile.HostAddress);
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
