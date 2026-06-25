using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record DesktopWindowAction
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("windowId")]
    public string? WindowId { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("desktopNumber")]
    public int? DesktopNumber { get; init; }
}

public static class DesktopWindowActionTypes
{
    public const string Activate = "activate";
    public const string Raise = "raise";
    public const string Minimize = "minimize";
    public const string Close = "close";
    public const string Resize = "resize";
    public const string MoveToDesktop = "move_to_desktop";
}
