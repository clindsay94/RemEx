using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record DesktopConfig
{
    private int _quality = 50;
    private double _scale = 0.5;
    private int _targetFps = 10;

    [JsonPropertyName("quality")]
    public int Quality
    {
        get => _quality;
        init => _quality = Math.Clamp(value, 1, 100);
    }

    [JsonPropertyName("scale")]
    public double Scale
    {
        get => _scale;
        init => _scale = Math.Clamp(value, 0.1, 1.0);
    }

    [JsonPropertyName("targetFps")]
    public int TargetFps
    {
        get => _targetFps;
        init => _targetFps = Math.Clamp(value, 1, 360);
    }
}
