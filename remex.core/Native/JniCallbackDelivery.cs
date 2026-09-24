using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Remex.Core.Models;

namespace Remex.Core.Native;

/// <summary>
/// Decides when a dropped video frame earns a keyframe request (perf audit P0-13).
/// </summary>
/// <remarks>
/// <para>
/// The JNI frame queue sheds frames when the Java consumer falls behind. An H.264 P-frame that never
/// reaches the decoder breaks the reference chain, so every frame after it decodes against a picture
/// the decoder never saw: the stream stays smeared or frozen until the host's next natural IDR. A
/// drop therefore OWES a keyframe request.
/// </para>
/// <para>
/// **THE REQUEST IS CLAIMED ON THE NEXT SUCCESSFUL ENQUEUE, NOT AT THE DROP.** A drop means the
/// consumer is stalled right now; an IDR asked for at that instant tends to arrive while it is still
/// stalled and be shed like the frame it was meant to repair. Waiting until a frame fits again asks
/// for the IDR once there is room to deliver it.
/// </para>
/// <para>
/// **THROTTLED TO THE HOST'S OWN COOLDOWN.** <c>RemoteDesktopHandler</c> reinitializes the encoder at
/// most once per 5 s and swallows requests inside that window (docs/REGRESSION-GUARDS.md, "Keyframe
/// throttle cooldown"). Asking more often than that buys nothing but "Throttled keyframe reinits"
/// noise, so a request is claimed at most once per <see cref="DefaultMinIntervalMs"/>. A debt that
/// is throttled stays owed and is claimed by the first enqueue after the window reopens.
/// </para>
/// <para>
/// **ONE STREAM SESSION AT A TIME; NOTHING BEFORE ITS FIRST IDR.** The host honors one reinit per
/// cooldown window and each new session starts that window fresh (docs/REGRESSION-GUARDS.md,
/// "Keyframe throttle cooldown"). A debt left over from a session the user stopped, claimed by the
/// first frame of the NEXT session, would spend that session's allowance on a request that has
/// nothing to do with it — and a late decoder (late surface attach, image-size rebuild) would then
/// have its own legitimate request swallowed: the black-screen class RemEx-vj7b fixed. So
/// <see cref="Reset"/> runs on every stream start and stop, and a debt is never claimed until the
/// session has delivered its first IDR. Delivering an IDR also SETTLES any debt outright: an IDR
/// references nothing, so the chain a drop broke is whole again without asking the host for anything.
/// </para>
/// </remarks>
internal sealed class FrameDropKeyframeGate
{
    /// <summary>Matches <c>keyframeReinitCooldownMs</c> in <c>RemoteDesktopHandler</c>.</summary>
    public const long DefaultMinIntervalMs = 5000;

    private const int IdrNalType = 5;

    private readonly long _minIntervalMs;
    private readonly object _gate = new();
    private int _recoveryOwed;
    private int _primed;
    private bool _hasRequested;
    private long _lastRequestMs;

    public FrameDropKeyframeGate(long minIntervalMs = DefaultMinIntervalMs)
    {
        _minIntervalMs = minIntervalMs;
    }

    /// <summary>True while a drop has happened that no request has yet been claimed for.</summary>
    public bool RecoveryOwed => Volatile.Read(ref _recoveryOwed) != 0;

    /// <summary>True once the current session has delivered an IDR; cleared by <see cref="Reset"/>.</summary>
    public bool IsPrimed => Volatile.Read(ref _primed) != 0;

    /// <summary>
    /// True when the next delivered frame's keyframe-ness matters: the session is not primed yet, or
    /// a debt is owed that an IDR would settle. False on the steady-state hot path, so the caller can
    /// skip scanning the frame for an IDR entirely.
    /// </summary>
    public bool WantsKeyframeCheck => Volatile.Read(ref _recoveryOwed) != 0 || Volatile.Read(ref _primed) == 0;

    /// <summary>A video frame was shed. Cheap and lock-free: it runs on the network receive thread.</summary>
    public void RecordDrop() => Volatile.Write(ref _recoveryOwed, 1);

    /// <summary>
    /// Called after a video frame was enqueued. Returns true exactly when the caller must send one
    /// keyframe request now; the debt is cleared at the same moment, so concurrent callers cannot
    /// both claim it.
    /// </summary>
    /// <param name="nowMs">Monotonic clock, milliseconds.</param>
    /// <param name="deliveredKeyframe">
    /// The enqueued frame carries an IDR. It primes the session and settles any debt; it never
    /// produces a request.
    /// </param>
    public bool TryClaimRequest(long nowMs, bool deliveredKeyframe)
    {
        // Hot path: every delivered frame lands here, and almost none owe anything.
        if (!deliveredKeyframe && Volatile.Read(ref _recoveryOwed) == 0) return false;

        lock (_gate)
        {
            if (deliveredKeyframe)
            {
                _primed = 1;
                _recoveryOwed = 0;
                return false;
            }

            if (_recoveryOwed == 0) return false;

            // Held, not dropped: the session's first IDR will settle it.
            if (_primed == 0) return false;
            if (_hasRequested && nowMs - _lastRequestMs < _minIntervalMs) return false;

            _recoveryOwed = 0;
            _hasRequested = true;
            _lastRequestMs = nowMs;
            return true;
        }
    }

    /// <summary>
    /// Forgets everything about the previous stream session: its debt, its throttle window, and its
    /// IDR. Called on every stream start and stop.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _recoveryOwed = 0;
            _primed = 0;
            _hasRequested = false;
            _lastRequestMs = 0;
        }
    }

    /// <summary>
    /// True when a desktop video frame (the "RDXF" envelope or a bare Annex-B access unit) carries an
    /// H.264 IDR slice (NAL type 5). An enveloped non-H.264 frame and a bare JPEG are never an IDR.
    /// </summary>
    /// <remarks>
    /// Scans the payload rather than trusting <see cref="DesktopFrameFlags.KeyFrame"/>: the host sets
    /// that flag on the frame it ASKED the encoder to key, and the encoder is pipelined, so the flag
    /// can sit on a P-frame. Emulation prevention guarantees <c>00 00 01</c> never occurs inside a NAL,
    /// so every start-code hit is a real NAL boundary. Same scan as <c>FFmpegH264Encoder.IndexOfNalOfType</c>.
    /// </remarks>
    public static bool ContainsIdr(ReadOnlySpan<byte> frame)
    {
        if (DesktopFrameEnvelope.TryRead(frame, out var header, out var payload))
        {
            if (header.Codec != DesktopCodecKind.H264) return false;
            frame = payload;
        }
        else if (frame.Length >= 2 && frame[0] == 0xFF && frame[1] == 0xD8)
        {
            return false; // bare JPEG (legacy MJPEG stream): entropy-coded data can mimic a start code
        }

        ReadOnlySpan<byte> startCode = [0x00, 0x00, 0x01];
        int from = 0;
        while (from <= frame.Length - startCode.Length)
        {
            int hit = frame[from..].IndexOf(startCode);
            if (hit < 0) return false;

            int nalHeader = from + hit + startCode.Length;
            if (nalHeader < frame.Length && (frame[nalHeader] & 0x1F) == IdrNalType) return true;
            from = from + hit + 1;
        }

        return false;
    }
}

/// <summary>
/// One pending value per key, latest wins (perf audit P0-13).
/// </summary>
/// <remarks>
/// <para>
/// For callbacks that carry a whole snapshot (telemetry, the process list, the launcher list), an
/// older value still waiting to be delivered is worthless once a newer one exists. Rather than queue
/// one delivery per arrival — which grows without bound behind a stalled Java consumer — the producer
/// <see cref="Offer"/>s the value here and queues a drain only when none is pending for that key. The
/// drain <see cref="TryTake"/>s whatever is newest by the time it runs.
/// </para>
/// <para>
/// Race-free without a lock: an update is a compare-and-swap against the value that was pending, so
/// if the drain removed it in between, the swap fails and the producer adds a fresh entry and queues
/// a fresh drain instead of stranding its value in a slot nothing will read.
/// </para>
/// </remarks>
internal sealed class LatestWinsSlots<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _slots = new();

    /// <summary>
    /// Stores <paramref name="value"/> as the newest for <paramref name="key"/>. Returns true when the
    /// caller must queue a drain (nothing was pending); false when it was folded into a drain that is
    /// already queued.
    /// </summary>
    public bool Offer(TKey key, TValue value)
    {
        while (true)
        {
            if (_slots.TryAdd(key, value)) return true;
            if (_slots.TryGetValue(key, out var pending) && _slots.TryUpdate(key, value, pending)) return false;
        }
    }

    /// <summary>Removes and returns the newest pending value for <paramref name="key"/>.</summary>
    public bool TryTake(TKey key, [MaybeNullWhen(false)] out TValue value) => _slots.TryRemove(key, out value);
}
