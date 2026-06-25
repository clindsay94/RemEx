using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record InputEvent
{
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("x")]
    public int? X { get; init; }

    [JsonPropertyName("y")]
    public int? Y { get; init; }

    [JsonPropertyName("button")]
    public int? Button { get; init; }

    [JsonPropertyName("keyCode")]
    public int? KeyCode { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("deltaX")]
    public int? DeltaX { get; init; }

    [JsonPropertyName("deltaY")]
    public int? DeltaY { get; init; }
}

public static class InputEventTypes
{
    public const string MouseMove = "mouseMove";
    public const string MouseDown = "mouseDown";
    public const string MouseUp = "mouseUp";
    public const string MouseClick = "mouseClick";
    public const string MouseScroll = "mouseScroll";
    public const string KeyDown = "keyDown";
    public const string KeyUp = "keyUp";
    public const string TypeText = "typeText";
}
