using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Persisted state of a single card within a saved dashboard layout.
/// </summary>
public record CardState
{
    /// <summary>Unique identifier for this card instance.</summary>
    public string CardId { get; init; } = string.Empty;

    /// <summary>Type discriminator: "Connection", "Actions", "Latency", or "Sensor".</summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>HWiNFO sensor name this card is bound to (null for non-sensor cards).</summary>
    public string? SensorId { get; init; }

    /// <summary>Horizontal position within the saved layout.</summary>
    [JsonPropertyName("positionX")]
    public double Left { get; init; }

    /// <summary>Vertical position within the saved layout.</summary>
    [JsonPropertyName("positionY")]
    public double Top { get; init; }

    /// <summary>Current card width in pixels.</summary>
    public double Width { get; init; } = 220;

    /// <summary>Current card height in pixels.</summary>
    public double Height { get; init; } = 160;

    /// <summary>Relative render order within the layout.</summary>
    [JsonPropertyName("zIndex")]
    public int Layer { get; init; }

    /// <summary>Compatibility alias for older canvas-oriented callers.</summary>
    [JsonIgnore]
    public double PositionX
    {
        get => Left;
        init => Left = value;
    }

    /// <summary>Compatibility alias for older canvas-oriented callers.</summary>
    [JsonIgnore]
    public double PositionY
    {
        get => Top;
        init => Top = value;
    }

    /// <summary>Compatibility alias for older canvas-oriented callers.</summary>
    [JsonIgnore]
    public int ZIndex
    {
        get => Layer;
        init => Layer = value;
    }
}

/// <summary>
/// A complete dashboard layout profile, serialised to/from JSON.
/// </summary>
public record DashboardProfile
{
    /// <summary>Human-readable profile name (e.g. "Gaming Mode", "Idle Monitoring").</summary>
    public string ProfileName { get; init; } = "Default";

    /// <summary>Whether cards snap to the nearest grid line on drop.</summary>
    public bool IsSnapToGridEnabled { get; init; }

    /// <summary>Grid cell size in pixels (used when snapping is enabled).</summary>
    public int GridSize { get; init; } = 50;

    /// <summary>Persisted WebSocket host address for the remote connection.</summary>
    public string HostAddress { get; init; } = "ws://localhost:5005/ws";

    /// <summary>Shared access key for WebSocket authentication. Empty = disabled.</summary>
    public string AccessKey { get; init; } = string.Empty;

    /// <summary>Persisted path to the Remex.Host.exe binary or its containing directory.</summary>
    public string HostPath { get; init; } = string.Empty;

    /// <summary>Persisted application language (e.g. "en", "es", "hi").</summary>
    public string Language { get; init; } = "en";

    /// <summary>All card positions and sizes.</summary>
    public List<CardState> Cards { get; init; } = new();

    /// <summary>Sensor names pinned to the Home overview.</summary>
    public List<string> PinnedSensorIds { get; init; } = new();

    /// <summary>Persisted Wake-on-LAN target MAC address (e.g. "AA:BB:CC:DD:EE:FF").</summary>
    public string WolMacAddress { get; init; } = string.Empty;

    /// <summary>Persisted Wake-on-LAN broadcast IP (default "255.255.255.255").</summary>
    public string WolBroadcastIp { get; init; } = "255.255.255.255";

    /// <summary>Persisted Wake-on-LAN UDP port (default 9).</summary>
    public int WolPort { get; init; } = 9;

    /// <summary>Whether the user has completed the first-run tutorial.</summary>
    public bool HasCompletedTutorial { get; init; }

    /// <summary>Host screen capture JPEG quality (10–100). Applies to the stream sent to mobile clients.</summary>
    public int StreamQuality { get; init; } = 75;

    /// <summary>Host screen capture target frames per second (5–120).</summary>
    public int StreamFps { get; init; } = 30;

    /// <summary>Visual aesthetic and theme overrides.</summary>
    public CustomizationSettings Customization { get; init; } = new();
}

/// <summary>
/// Persisted visual customization parameters.
/// </summary>
public record CustomizationSettings
{
    /// <summary>Identifier for the selected presentation style.</summary>
    [JsonPropertyName("baseTheme")]
    public string ThemeId { get; init; } = "BaseDarkGlass";

    /// <summary>Corner radius in pixels for cards and panels.</summary>
    public double CornerRadius { get; init; } = 16;

    /// <summary>Corner radius in pixels for remote control buttons.</summary>
    public double RemoteCardCornerRadius { get; init; } = 24;

    /// <summary>Opacity (0.0 to 1.0) of the canvas cards.</summary>
    public double GlassOpacity { get; init; } = 0.1;

    /// <summary>Opacity (0.1 to 1.0) of the entire app window when Glass mode is active.</summary>
    public double AppWindowOpacity { get; init; } = 0.92;

    /// <summary>Relative strength of the neon/glow effects.</summary>
    public double GlowStrength { get; init; } = 2;

    /// <summary>Vibrancy (Chroma) level for the seed color (Material 3).</summary>
    public double ThemeSeedChroma { get; init; } = 48.0;

    /// <summary>Contrast level for the dynamic color scheme (-1.0 to 1.0).</summary>
    public double ThemeContrast { get; init; } = 0.0;

    /// <summary>Primary brand/accent colour in Hex (e.g. "#6C4CFF").</summary>
    public string AccentColor { get; init; } = "#6C4CFF";

    /// <summary>Material 3 scheme variant for the Dynamic theme.</summary>
    public string SchemeVariant { get; init; } = "TonalSpot";

    /// <summary>Requested window or surface material treatment.</summary>
    [JsonPropertyName("canvasBackgroundType")]
    public string BackgroundMaterial { get; init; } = "Mica";

    /// <summary>Compatibility alias for older UI clients.</summary>
    [JsonIgnore]
    public string BaseTheme
    {
        get => ThemeId;
        init => ThemeId = value;
    }

    /// <summary>Compatibility alias for older UI clients.</summary>
    [JsonIgnore]
    public string CanvasBackgroundType
    {
        get => BackgroundMaterial;
        init => BackgroundMaterial = value;
    }
}
