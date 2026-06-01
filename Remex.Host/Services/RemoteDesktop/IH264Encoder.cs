using System;

namespace Remex.Host.Services.RemoteDesktop;

/// <summary>
/// Interface for H.264 video frame encoders used in low-latency desktop streaming.
/// </summary>
public interface IH264Encoder : IDisposable
{
    /// <summary>
    /// Gets whether the H.264 encoder is supported and ready to be used on the current host.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The exact size, in bytes, of the raw BGRA frame this encoder expects per
    /// <see cref="EncodeFrame"/> call (width * height * 4). 0 before initialization.
    /// Callers must feed buffers of exactly this size or the encoder's rawvideo input desyncs.
    /// </summary>
    int ExpectedInputByteCount { get; }

    /// <summary>
    /// Initializes the encoder with target stream specifications.
    /// <paramref name="qp"/> is the constant quantization parameter (lower = higher quality/bitrate).
    /// </summary>
    bool Initialize(int width, int height, int fps, int qp);

    /// <summary>
    /// Encodes a raw 32-bit BGRA pixel frame to H.264 Annex B packets.
    /// </summary>
    /// <param name="rawPixelsBGRA">Raw 32-bit BGRA bytes of the screen frame.</param>
    /// <param name="forceKeyframe">If true, forces the encoder to produce a keyframe (I-frame) for this frame.</param>
    /// <returns>H.264 Annex B bytes (including start codes), or null if encoding failed.</returns>
    byte[]? EncodeFrame(byte[] rawPixelsBGRA, bool forceKeyframe);
}
