using System.Text.Json.Serialization;

namespace Remex.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DesktopCodecKind>))]
public enum DesktopCodecKind
{
    /// <summary>Motion JPEG — compatibility baseline, always available.</summary>
    Mjpeg,
    /// <summary>H.264 — hardware-accelerated when the host supports it.</summary>
    H264,
}

[JsonConverter(typeof(JsonStringEnumConverter<DesktopH264Profile>))]
public enum DesktopH264Profile
{
    Baseline,
    Main,
    High,
}

[JsonConverter(typeof(JsonStringEnumConverter<DesktopEncoderBackend>))]
public enum DesktopEncoderBackend
{
    /// <summary>Software encoder (x264 / libavcodec).</summary>
    Software,
    /// <summary>NVIDIA NVENC hardware encoder.</summary>
    Nvenc,
    /// <summary>VA-API hardware encoder (Intel/AMD on Linux).</summary>
    Vaapi,
    /// <summary>Apple VideoToolbox hardware encoder.</summary>
    VideoToolbox,
    /// <summary>Android MediaCodec hardware encoder.</summary>
    MediaCodec,
}

/// <summary>
/// Codec capabilities and configuration for a remote desktop stream.
/// Carried in DesktopMeta so clients know what the current stream is encoded with.
/// </summary>
public record DesktopCodecInfo
{
    /// <summary>Active codec for this stream.</summary>
    [JsonPropertyName("codec")]
    public DesktopCodecKind Codec { get; init; } = DesktopCodecKind.Mjpeg;

    /// <summary>H.264 profile in use, null when codec is Mjpeg.</summary>
    [JsonPropertyName("profile")]
    public DesktopH264Profile? Profile { get; init; }

    /// <summary>Hardware encoder backend used, null when codec is Mjpeg or no hardware encoder is active.</summary>
    [JsonPropertyName("encoderBackend")]
    public DesktopEncoderBackend? EncoderBackend { get; init; }

    /// <summary>Target frame rate configured for this stream.</summary>
    [JsonPropertyName("targetFps")]
    public int TargetFps { get; init; }

    /// <summary>Target bitrate in kbps, null when codec is Mjpeg (quality-based, not bitrate-based).</summary>
    [JsonPropertyName("targetBitrateKbps")]
    public int? TargetBitrateKbps { get; init; }
}
