using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record DesktopConfig
{
    /// <summary>
    /// Maximum target frame rate. The UI's "Unlimited" option maps to this value. It sits at or above
    /// the highest consumer display refresh rate, and the H.264 encoder is throughput-bound well below
    /// it at real resolutions, so it is effectively uncapped — while keeping the host frame pacer safe
    /// (1000/360 ≈ 2.78 ms per tick, so a static screen can never busy-spin). Hosts that predate this
    /// bump clamp it straight back to 120 on deserialization, so raising it is backward-compatible.
    /// </summary>
    public const int MaxTargetFps = 360;

    /// <summary>
    /// Highest frame rate the UI presents as a concrete number. Above this, the FPS control shows
    /// "Unlimited" instead — the band up to <see cref="MaxTargetFps"/> is treated as effectively
    /// uncapped (the encoder is throughput-bound below it, so the exact value there is immaterial).
    /// Mirrors the Android client's <c>DESKTOP_FPS_PACED_MAX</c>.
    /// </summary>
    public const int PacedMaxFps = 240;

    private int _quality = 50;
    private double _scale = 0.5;
    private int _targetFps = 120;
    private DesktopCodecKind _codec = DesktopCodecKind.Mjpeg;

    [JsonPropertyName("codec")]
    public DesktopCodecKind Codec
    {
        get => _codec;
        init => _codec = value;
    }

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
        init => _targetFps = Math.Clamp(value, 1, MaxTargetFps);
    }

    private bool _drawCursor = true;

    [JsonPropertyName("drawCursor")]
    public bool DrawCursor
    {
        get => _drawCursor;
        init => _drawCursor = value;
    }

    [JsonPropertyName("desktopProtocolVersion")]
    public int? DesktopProtocolVersion { get; init; }

    [JsonPropertyName("clientCapabilities")]
    public DesktopClientCapabilities? ClientCapabilities { get; init; }

    [JsonPropertyName("captureMode")]
    public DesktopCaptureMode? CaptureMode { get; init; }

    [JsonPropertyName("displayId")]
    public string? DisplayId { get; init; }

    [JsonPropertyName("displayListVersion")]
    public int? DisplayListVersion { get; init; }
}
