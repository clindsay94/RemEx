namespace Remex.Agent.Services.RemoteDesktop;

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

    /// <summary>
    /// Requests an on-demand keyframe (IDR with fresh SPS/PPS). A client whose decoder desynced
    /// (dropped frames / output-format change) calls this so it can recover without waiting up to a
    /// full GOP. The flag is consumed by <see cref="ConsumeKeyframeRequest"/>; the stream-control
    /// layer reinitializes the encoder when it is set, which emits a true IDR (codecs are configured
    /// with forced-IDR so the reinit's first frame is independently decodable).
    /// </summary>
    void RequestKeyframe();

    /// <summary>
    /// Atomically reads-and-clears a pending keyframe request set by <see cref="RequestKeyframe"/>.
    /// Returns true exactly once per request so the stream-control loop can force a keyframe path.
    /// </summary>
    bool ConsumeKeyframeRequest();

    /// <summary>
    /// Running count of raw frames accepted onto the encoder's input channel (i.e. not dropped by
    /// the capacity-3 drop-write submit queue). The <see cref="AdaptiveScaleController"/> (Phase 5)
    /// samples the delta of this counter across an evaluation window as "achieved encode FPS".
    /// </summary>
    long AcceptedInputFrameCount { get; }

    /// <summary>
    /// Running count of encoded access units dropped because the output channel overflowed (RemEx-3u6o).
    /// The <see cref="AdaptiveScaleController"/> (Phase 5) samples the delta of this counter to decide
    /// whether the current capture scale is outrunning what the encoder can sustain.
    /// </summary>
    long DroppedAccessUnitCount { get; }
}
