using Remex.Core.Models;

namespace Remex.Core.Tests;

public class DesktopConfigTests
{
    [Fact]
    public void Default_Values_Are_Reasonable()
    {
        var config = new DesktopConfig();
        Assert.Equal(50, config.Quality);
        Assert.Equal(0.5, config.Scale);
        Assert.Equal(10, config.TargetFps);
    }

    [Theory]
    [InlineData(0, 1)]    // below minimum clamped to 1
    [InlineData(-10, 1)]  // negative clamped to 1
    [InlineData(1, 1)]    // at minimum
    [InlineData(50, 50)]  // normal
    [InlineData(100, 100)] // at maximum
    [InlineData(200, 100)] // above maximum clamped to 100
    public void Quality_Is_Clamped(int input, int expected)
    {
        var config = new DesktopConfig { Quality = input };
        Assert.Equal(expected, config.Quality);
    }

    [Theory]
    [InlineData(0.0, 0.1)]   // below minimum
    [InlineData(-1.0, 0.1)]  // negative
    [InlineData(0.1, 0.1)]   // at minimum
    [InlineData(0.5, 0.5)]   // normal
    [InlineData(1.0, 1.0)]   // at maximum
    [InlineData(2.0, 1.0)]   // above maximum
    public void Scale_Is_Clamped(double input, double expected)
    {
        var config = new DesktopConfig { Scale = input };
        Assert.Equal(expected, config.Scale, precision: 5);
    }

    [Theory]
    [InlineData(0, 1)]      // below minimum
    [InlineData(-5, 1)]     // negative
    [InlineData(1, 1)]      // at minimum
    [InlineData(30, 30)]    // normal
    [InlineData(60, 60)]    // common value
    [InlineData(120, 120)]  // at maximum
    [InlineData(360, 120)]  // above maximum
    [InlineData(400, 120)]  // above maximum
    public void TargetFps_Is_Clamped(int input, int expected)
    {
        var config = new DesktopConfig { TargetFps = input };
        Assert.Equal(expected, config.TargetFps);
    }

    [Fact]
    public void Json_Roundtrip_Preserves_Values()
    {
        var original = new DesktopConfig
        {
            Quality = 75,
            Scale = 0.8,
            TargetFps = 30,
            DesktopProtocolVersion = 1,
            ClientCapabilities = new DesktopClientCapabilities
            {
                SupportsDisplaySelection = true,
                SupportsFrameEnvelope = true,
                SupportsTargetSwitch = true,
                SupportsCursorState = true,
                SupportsCursorShape = true,
            },
            CaptureMode = DesktopCaptureMode.Monitor,
            DisplayId = "DISPLAY1",
            DisplayListVersion = 42,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<DesktopConfig>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(75, deserialized.Quality);
        Assert.Equal(0.8, deserialized.Scale, precision: 5);
        Assert.Equal(30, deserialized.TargetFps);
        Assert.Equal(1, deserialized.DesktopProtocolVersion);
        Assert.True(deserialized.ClientCapabilities?.SupportsDisplaySelection);
        Assert.True(deserialized.ClientCapabilities?.SupportsFrameEnvelope);
        Assert.True(deserialized.ClientCapabilities?.SupportsTargetSwitch);
        Assert.True(deserialized.ClientCapabilities?.SupportsCursorState);
        Assert.True(deserialized.ClientCapabilities?.SupportsCursorShape);
        Assert.Equal(DesktopCaptureMode.Monitor, deserialized.CaptureMode);
        Assert.Equal("DISPLAY1", deserialized.DisplayId);
        Assert.Equal(42, deserialized.DisplayListVersion);
    }

    [Fact]
    public void Json_Deserialization_Clamps_OutOfRange()
    {
        var json = """{"quality":999,"scale":5.0,"targetFps":999}""";
        var config = System.Text.Json.JsonSerializer.Deserialize<DesktopConfig>(json);
        Assert.NotNull(config);
        Assert.Equal(100, config.Quality);
        Assert.Equal(1.0, config.Scale, precision: 5);
        Assert.Equal(120, config.TargetFps);
    }
}
