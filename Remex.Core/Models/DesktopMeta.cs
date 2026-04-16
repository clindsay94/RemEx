using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record DesktopMeta
{
    [JsonPropertyName("screenWidth")]
    public int ScreenWidth { get; init; }

    [JsonPropertyName("screenHeight")]
    public int ScreenHeight { get; init; }

    /// <summary>
    /// Unique identifier for the host process instance.
    /// Used to detect self-connections (infinite mirror prevention).
    /// </summary>
    [JsonPropertyName("hostInstanceId")]
    public string? HostInstanceId { get; init; }

    /// <summary>
    /// Current mouse cursor X position (for trackpad mode visibility).
    /// </summary>
    [JsonPropertyName("cursorX")]
    public int CursorX { get; init; }

    /// <summary>
    /// Current mouse cursor Y position (for trackpad mode visibility).
    /// </summary>
    [JsonPropertyName("cursorY")]
    public int CursorY { get; init; }
}
