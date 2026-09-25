namespace Remex.Agent.Services.RemoteDesktop;

/// <summary>
/// Decides whether an MJPEG frame is worth publishing to the send loop, so a static screen stops
/// re-sending an identical JPEG every tick (P1-13).
/// </summary>
/// <remarks>
/// <para>
/// "UNCHANGED" MEANS THE SAME BUFFER, AND ONLY WHEN THE BACKEND PROMISES IT. The caller only consults
/// this gate when <c>IScreenCaptureService.UnchangedFramesShareBuffer</c> is true: the Windows backend
/// then hands an unchanged screen back as the very memory it returned before (DXGI replays its cached
/// JPEG; the WGC tier reuses its last encode), and never mutates it. Comparing
/// <see cref="ReadOnlyMemory{T}"/> by <c>Equals</c> (same array, offset and length) is therefore both
/// exact and free — no hashing, no byte compare.
/// </para>
/// <para>
/// PER STREAM, NOT PER BACKEND. One capture instance serves every connected client, so each stream loop
/// owns its own gate. A backend-level "unchanged" flag would tell client B nothing changed right after
/// client A's capture picked up the change B never received.
/// </para>
/// <para>
/// NOT <c>IsLive</c>. This runs only on frames the handler already accepted as live; stale replays are
/// dropped before they get here (RemEx-ltd) and must keep advancing the failure counter.
/// </para>
/// <para>
/// FORCED REFRESH. An unchanged frame is still re-published every <see cref="ForcedRefreshIntervalMs"/>,
/// so a client that lost a frame (latest-frame overwrite, reconnect race) cannot sit on a stale picture
/// until the screen happens to change. 2 s mirrors the H.264 path's forced keyframe every 60 captured
/// frames at 30 fps; it has to be wall-clock here because a skipped frame is not counted as captured.
/// </para>
/// </remarks>
internal sealed class MjpegResendGate
{
    /// <summary>Longest an unchanged frame goes without being re-published.</summary>
    public const double ForcedRefreshIntervalMs = 2000.0;

    private ReadOnlyMemory<byte> _lastPublished;
    private long _lastStreamSerial = long.MinValue;
    private double _lastPublishMs = double.NegativeInfinity;

    /// <summary>
    /// Returns <c>true</c> (and records the frame as published) when <paramref name="frame"/> must be
    /// sent: it is a different buffer, the stream serial changed, the refresh interval elapsed, or
    /// <paramref name="force"/> is set. Returns <c>false</c> for a repeat of the last published frame.
    /// </summary>
    public bool ShouldPublish(ReadOnlyMemory<byte> frame, long streamSerial, double nowMs, bool force = false)
    {
        var repeat = !force &&
                     !frame.IsEmpty &&
                     streamSerial == _lastStreamSerial &&
                     frame.Equals(_lastPublished) &&
                     nowMs - _lastPublishMs < ForcedRefreshIntervalMs;
        if (repeat)
        {
            return false;
        }

        _lastPublished = frame;
        _lastStreamSerial = streamSerial;
        _lastPublishMs = nowMs;
        return true;
    }

    /// <summary>
    /// Forgets the last published frame, so the next MJPEG frame is sent whatever it is. Called when a
    /// non-MJPEG frame is published (the H.264 self-healing window), since the client's picture no
    /// longer comes from the last MJPEG frame.
    /// </summary>
    public void Reset()
    {
        _lastPublished = default;
        _lastStreamSerial = long.MinValue;
        _lastPublishMs = double.NegativeInfinity;
    }
}
