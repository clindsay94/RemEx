namespace Remex.Branding;

/// <summary>What a known peer is, for its glyph. Unknown devices draw as a phone.</summary>
public enum HandshakeDeviceKind { Phone, Tablet, Pc }

/// <summary>One paired peer as the splash draws it.</summary>
/// <param name="Name">Display name (the user's nickname when there is one).</param>
/// <param name="Kind">Glyph to draw.</param>
/// <param name="LinkedAt">
/// Splash time (seconds) this peer was first seen linked, or null while it has not been. On the PC
/// "linked" means an authenticated, non-loopback session.
/// </param>
public readonly record struct HandshakePeer(string Name, HandshakeDeviceKind Kind, float? LinkedAt);

/// <summary>
/// Everything the host has observed about the real startup, stamped in seconds on the splash's own
/// clock. Immutable: the host builds a new one per poll and the variant swaps it whole.
/// </summary>
/// <param name="Peers">The paired devices, in a stable order.</param>
/// <param name="TargetIndex">Index into <paramref name="Peers"/> of the target, or -1 for none.</param>
/// <param name="TargetAt">When the target became known (PC: when it linked; Android: 0).</param>
/// <param name="ReadyAt">When the app became ready (PC: host services published).</param>
/// <param name="ListeningAt">When the host's listener came up (PC only).</param>
/// <param name="ListeningPort">The listener's port when it can be resolved.</param>
/// <param name="FailedAt">When the connection to the target failed (Android only).</param>
public sealed record HandshakeSnapshot(
    IReadOnlyList<HandshakePeer> Peers,
    int TargetIndex,
    float? TargetAt,
    float? ReadyAt,
    float? ListeningAt,
    int? ListeningPort,
    float? FailedAt)
{
    /// <summary>Nothing observed yet.</summary>
    public static HandshakeSnapshot Empty { get; } =
        new(Array.Empty<HandshakePeer>(), -1, null, null, null, null, null);

    /// <summary>The target's link time, or null when there is no target or it has not linked.</summary>
    public float? TargetLinkedAt =>
        TargetIndex >= 0 && TargetIndex < Peers.Count ? Peers[TargetIndex].LinkedAt : null;
}

/// <summary>The director's inputs: event times in seconds, null when the event has not happened.</summary>
/// <param name="Peers">How many peers are paired.</param>
/// <param name="TargetAt">When a target became known.</param>
/// <param name="ReadyAt">When the app became ready.</param>
/// <param name="LinkedAt">When the target linked.</param>
/// <param name="FailedAt">When connecting to the target failed.</param>
/// <param name="SkipAt">When the user skipped.</param>
/// <param name="ExitFromMark">True on the PC: the portal always opens from the mark.</param>
public readonly record struct HandshakeTimeline(
    int Peers,
    float? TargetAt,
    float? ReadyAt,
    float? LinkedAt,
    float? FailedAt,
    float? SkipAt,
    bool ExitFromMark);

/// <summary>Where the hand-off portal opens.</summary>
public enum HandshakeOrigin { Mark, Target }

/// <summary>Which status line the splash shows.</summary>
public enum HandshakeStatusKind { Starting, Pinging, SomeLinked, LinkedTo, NotAnswering, NonePaired, Listening }

/// <summary>The status line as data; the host localizes it.</summary>
/// <param name="Kind">Which line.</param>
/// <param name="Count">Pinging: peers. SomeLinked: how many linked.</param>
/// <param name="Total">SomeLinked: how many paired.</param>
/// <param name="Name">LinkedTo / NotAnswering: the peer's name.</param>
/// <param name="Port">Listening: the port, when known.</param>
public readonly record struct HandshakeStatus(
    HandshakeStatusKind Kind, int Count = 0, int Total = 0, string? Name = null, int? Port = null)
{
    /// <summary>Drawn in the accent colour rather than muted.</summary>
    public bool Hot => Kind == HandshakeStatusKind.LinkedTo;
}

/// <summary>
/// The pure rules for Live Handshake (RemEx-8g6n0): when the splash hands off, where the portal
/// opens, when the pulses fire, and what the status line says. The Android director implements the
/// same rules; both are tested against <c>docs/specs/live-handshake-director-vectors.json</c>.
/// </summary>
public static class LiveHandshakeDirector
{
    public const float Floor = 0.9f;
    public const float Grace = 1.2f;
    public const float Cap = 2.6f;
    public const float LockHold = 0.5f;
    public const float Exit = 0.72f;
    public const float FadeExit = 0.28f;
    public const float FirstPulse = 0.32f;
    public const float PulsePeriod = 1.0f;

    private static bool Known(float? at, float now) => at is { } a && a <= now;

    private static float At(float? at) => Math.Max(0f, at ?? 0f);

    /// <summary>The spec's <c>candidate(known)</c>: only events at or before <paramref name="now"/> count.</summary>
    public static float Candidate(in HandshakeTimeline e, float now)
    {
        float c;
        if (Known(e.SkipAt, now)) c = At(e.SkipAt);
        else if (!Known(e.ReadyAt, now)) c = Cap;
        else
        {
            float ready = At(e.ReadyAt);
            if (e.Peers == 0 || !Known(e.TargetAt, now)) c = Math.Max(Floor, ready);
            else if (Known(e.LinkedAt, now)) c = Math.Max(Math.Max(At(e.LinkedAt) + LockHold, Floor), ready);
            else if (Known(e.FailedAt, now)) c = Math.Max(Math.Max(Floor, ready), At(e.FailedAt));
            else c = Math.Max(Floor, Math.Min(ready + Grace, Cap));
        }
        return Math.Min(c, Cap);
    }

    /// <summary>
    /// The hand-off time if it has happened by <paramref name="now"/>, else null. Exact rather than
    /// sampled: the first instant where <c>now &gt;= candidate(known)</c>. The candidate only changes
    /// when an event becomes known, so each interval between event times is checked once. Later
    /// events cannot move an earlier answer, which is the "fixed once it starts" rule.
    /// </summary>
    public static float? Handoff(in HandshakeTimeline e, float now)
    {
        Span<float> starts = stackalloc float[6];
        int n = 0;
        starts[n++] = 0f;
        AddIfKnown(starts, ref n, e.TargetAt, now);
        AddIfKnown(starts, ref n, e.ReadyAt, now);
        AddIfKnown(starts, ref n, e.LinkedAt, now);
        AddIfKnown(starts, ref n, e.FailedAt, now);
        AddIfKnown(starts, ref n, e.SkipAt, now);
        starts[..n].Sort();

        for (int i = 0; i < n; i++)
        {
            float start = starts[i];
            float next = i + 1 < n ? starts[i + 1] : float.PositiveInfinity;
            if (next <= start) continue; // duplicate time: the later copy carries the interval
            float h = Math.Max(start, Candidate(e, start));
            if (h < next) return h <= now ? h : null;
        }
        return null;
    }

    private static void AddIfKnown(Span<float> starts, ref int n, float? at, float now)
    {
        if (Known(at, now)) starts[n++] = At(at);
    }

    /// <summary>Where the portal opens, decided at <paramref name="handoff"/>.</summary>
    public static HandshakeOrigin Origin(in HandshakeTimeline e, float handoff) =>
        !e.ExitFromMark && e.Peers > 0 && Known(e.TargetAt, handoff) && Known(e.LinkedAt, handoff)
            ? HandshakeOrigin.Target
            : HandshakeOrigin.Mark;

    /// <summary>Start time of pulse <paramref name="k"/> (0-based).</summary>
    public static float PulseAt(int k) => FirstPulse + k * PulsePeriod;

    /// <summary>
    /// How many pulses have started by <paramref name="now"/>: they fire strictly before the hand-off
    /// (null = not yet decided), and never under reduced motion.
    /// </summary>
    public static int PulsesStarted(float now, float? handoff, bool reducedMotion)
    {
        if (reducedMotion) return 0;
        int k = 0;
        while (k < 64)
        {
            float tp = PulseAt(k);
            if (tp > now || (handoff is { } h && tp >= h)) break;
            k++;
        }
        return k;
    }

    /// <summary>
    /// The PC status line (the reference is the lab's <c>statusText</c>). <paramref name="linkedCount"/>
    /// counts peers that linked before the hand-off and by <paramref name="now"/>;
    /// <paramref name="lockedTarget"/> is the target's name once it has locked on, else null;
    /// <paramref name="listeningAt"/> is when the listener is shown coming up.
    /// </summary>
    public static HandshakeStatus Status(
        float now,
        int peers,
        int linkedCount,
        string? lockedTarget,
        string? notAnsweringTarget,
        float? listeningAt,
        int? listeningPort)
    {
        if (peers == 0)
            return new HandshakeStatus(now < FirstPulse ? HandshakeStatusKind.Starting : HandshakeStatusKind.NonePaired);
        if (lockedTarget is not null)
            return new HandshakeStatus(HandshakeStatusKind.LinkedTo, Name: lockedTarget);
        if (notAnsweringTarget is not null)
            return new HandshakeStatus(HandshakeStatusKind.NotAnswering, Name: notAnsweringTarget);
        if (Known(listeningAt, now) && linkedCount == 0)
            return new HandshakeStatus(HandshakeStatusKind.Listening, Port: listeningPort);
        if (now < FirstPulse) return new HandshakeStatus(HandshakeStatusKind.Starting);
        return linkedCount > 0
            ? new HandshakeStatus(HandshakeStatusKind.SomeLinked, linkedCount, peers)
            : new HandshakeStatus(HandshakeStatusKind.Pinging, peers, peers);
    }
}
