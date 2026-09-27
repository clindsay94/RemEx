namespace Remex.Branding;

/// <summary>What a known peer is, for its glyph. Unknown devices draw as a phone.</summary>
public enum HandshakeDeviceKind { Phone, Tablet, Pc }

/// <summary>One paired peer as the splash draws it.</summary>
/// <param name="Name">Display name (the user's nickname when there is one).</param>
/// <param name="Kind">Glyph to draw.</param>
/// <param name="LinkedAt">
/// Splash time (seconds) this peer was first seen linked, or null while it has not been. On the PC
/// "linked" means an authenticated, non-loopback session, and a link is also the peer's answer.
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

/// <summary>A real reachability answer: when it was heard, and whether it came from the target.</summary>
public readonly record struct HandshakeAnswer(float At, bool Target);

/// <summary>The director's inputs: event times in seconds, null when the event has not happened.</summary>
/// <param name="Peers">How many peers are paired.</param>
/// <param name="TargetAt">When a target became known.</param>
/// <param name="ReadyAt">When the app became ready.</param>
/// <param name="LinkedAt">When the target linked.</param>
/// <param name="FailedAt">When connecting to the target failed.</param>
/// <param name="SkipAt">When the user skipped.</param>
/// <param name="ExitFromMark">True on the PC: the portal always opens from the mark.</param>
/// <param name="Answers">Real answers in any order (PC: every linked phone, at its link time).</param>
public readonly record struct HandshakeTimeline(
    int Peers,
    float? TargetAt,
    float? ReadyAt,
    float? LinkedAt,
    float? FailedAt,
    float? SkipAt,
    bool ExitFromMark,
    IReadOnlyList<HandshakeAnswer>? Answers = null);

/// <summary>Where the hand-off portal opens.</summary>
public enum HandshakeOrigin { Mark, Target }

/// <summary>Which console line (the PC set; "not answering" is Android-only).</summary>
public enum HandshakeLineKind { Paired, NonePaired, Listening, Linked, Opening }

/// <summary>One console line as data; the host localizes it.</summary>
/// <param name="Kind">Which line.</param>
/// <param name="At">When it appears (its due time, queued at least LINE_GAP after the previous line).</param>
/// <param name="Count">Paired: how many phones.</param>
/// <param name="Name">Linked: the phone's name.</param>
/// <param name="Port">Listening: the port, when known.</param>
public readonly record struct HandshakeLine(
    HandshakeLineKind Kind, float At, int Count = 0, string? Name = null, int? Port = null)
{
    /// <summary>Drawn in the accent colour while it is the newest line.</summary>
    public bool Hot => Kind is HandshakeLineKind.Linked or HandshakeLineKind.Opening;
}

/// <summary>
/// The pure rules for Live Handshake (RemEx-8g6n0): when the splash hands off, when each real event
/// is SHOWN (staging), where the portal opens, when the pulses fire and what the console says. The
/// Android director implements the same rules; both are tested against
/// <c>docs/specs/live-handshake-director-vectors.json</c>.
/// </summary>
/// <remarks>
/// STAGING (spec, revised after the first device test). On a fast LAN every real event lands inside
/// half a second and the first build flashed through. Events are never faked or reordered, but each
/// is shown no sooner than it can be read: answers in real order at
/// <c>max(effective, ANSWER_MIN, previous + ANSWER_GAP)</c>, the lock at
/// <c>max(linkedAt, shown(target) + LOCK_AFTER)</c>, and the hand-off holds the SHOWN lock for
/// LOCK_HOLD. Nothing staged past the hand-off is shown.
/// </remarks>
public static class LiveHandshakeDirector
{
    public const float Floor = 1.4f;
    public const float Grace = 1.2f;
    public const float Cap = 3.0f;
    public const float LockHold = 0.7f;
    public const float Exit = 0.72f;
    public const float FadeExit = 0.28f;
    public const float FirstPulse = 0.32f;
    public const float PulsePeriod = 1.0f;
    public const float AnswerMin = 0.62f;
    public const float AnswerGap = 0.18f;
    public const float LockAfter = 0.5f;
    public const float LineGap = 0.32f;

    /// <summary>The listening line is not shown before this (the mark is still igniting).</summary>
    public const float ListeningMin = 0.4f;

    /// <summary>Most answers the staging handles (larger lists are truncated, never a crash).</summary>
    public const int MaxAnswers = 32;

    private static bool Known(float? at, float now) => at is { } a && a <= now;

    private static float At(float? at) => Math.Max(0f, at ?? 0f);

    /// <summary>
    /// Stages the answers known by <paramref name="now"/>. <paramref name="shown"/> receives, per
    /// entry of <see cref="HandshakeTimeline.Answers"/> (and one extra slot at the end for a target
    /// that linked with no probe answer of its own), the time it is shown, or NaN when it is not
    /// known yet. Returns the number of slots written; <paramref name="targetSlot"/> is the target's
    /// slot, or -1.
    /// </summary>
    public static int StageAnswers(in HandshakeTimeline e, float now, Span<float> shown, out int targetSlot)
    {
        var answers = e.Answers;
        int n = Math.Min(answers?.Count ?? 0, Math.Min(MaxAnswers, shown.Length - 1));
        bool linked = Known(e.LinkedAt, now);
        float linkedAt = At(e.LinkedAt);
        Span<float> eff = stackalloc float[n + 1];
        targetSlot = -1;

        for (int i = 0; i < n; i++)
        {
            var a = answers![i];
            float t = float.NaN;
            if (a.At <= now) t = Math.Max(0f, a.At);
            if (a.Target && targetSlot < 0)
            {
                targetSlot = i;
                // A link also counts as the target answering: its effective answer is the earlier.
                if (linked) t = float.IsNaN(t) ? linkedAt : Math.Min(t, linkedAt);
            }
            eff[i] = t;
        }
        eff[n] = float.NaN;
        if (linked && targetSlot < 0)
        {
            targetSlot = n;
            eff[n] = linkedAt;
        }

        // Real order (stable), each shown no sooner than ANSWER_MIN and ANSWER_GAP after the last.
        Span<int> order = stackalloc int[n + 1];
        int m = 0;
        for (int i = 0; i <= n; i++)
        {
            if (float.IsNaN(eff[i])) continue;
            int j = m++;
            while (j > 0 && eff[order[j - 1]] > eff[i]) { order[j] = order[j - 1]; j--; }
            order[j] = i;
        }
        for (int i = 0; i <= n; i++) shown[i] = float.NaN;
        float prev = float.NegativeInfinity;
        for (int k = 0; k < m; k++)
        {
            float s = Math.Max(Math.Max(eff[order[k]], AnswerMin), prev + AnswerGap);
            shown[order[k]] = s;
            prev = s;
        }
        return n + 1;
    }

    /// <summary>When the lock is shown, given what is known by <paramref name="now"/>; null before the link.</summary>
    public static float? LockShown(in HandshakeTimeline e, float now)
    {
        if (!Known(e.LinkedAt, now) || !Known(e.TargetAt, now) || e.Peers == 0) return null;
        Span<float> shown = stackalloc float[MaxAnswers + 1];
        int count = StageAnswers(e, now, shown, out int slot);
        float target = slot >= 0 && slot < count && !float.IsNaN(shown[slot]) ? shown[slot] : Math.Max(At(e.LinkedAt), AnswerMin);
        return Math.Max(At(e.LinkedAt), target + LockAfter);
    }

    /// <summary>The lock time if it is shown at or before <paramref name="handoff"/>, else null.</summary>
    public static float? LockShownBy(in HandshakeTimeline e, float handoff) =>
        LockShown(e, handoff) is { } l && l <= handoff ? l : null;

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
            else if (LockShown(e, now) is { } lockShown) c = Math.Max(Math.Max(lockShown + LockHold, Floor), ready);
            else if (Known(e.FailedAt, now)) c = Math.Max(Math.Max(Floor, ready), At(e.FailedAt));
            else c = Math.Max(Floor, Math.Min(ready + Grace, Cap));
        }
        return Math.Min(c, Cap);
    }

    /// <summary>
    /// The hand-off time if it has happened by <paramref name="now"/>, else null. Exact rather than
    /// sampled: the first instant where <c>now &gt;= candidate(known)</c>. The candidate only changes
    /// when an event it reads becomes known (answers only move it through the target, whose answer is
    /// known by its link), so each interval between those event times is checked once. Later events
    /// cannot move an earlier answer, which is the "fixed once it starts" rule.
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

    /// <summary>Where the portal opens: the target only if its lock was SHOWN by the hand-off, and never on the PC.</summary>
    public static HandshakeOrigin Origin(in HandshakeTimeline e, float handoff) =>
        !e.ExitFromMark && LockShownBy(e, handoff) is not null ? HandshakeOrigin.Target : HandshakeOrigin.Mark;

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
    /// The PC console (spec, "The console"): the lines known so far, sorted by due
    /// time and queued so each appears at <c>max(due, previous + LINE_GAP)</c>. Writes into
    /// <paramref name="lines"/> (4 slots suffice) and returns the count. <paramref name="lockShown"/>
    /// and <paramref name="handoff"/> are the director's staged values (null while unknown).
    /// </summary>
    public static int PcConsole(
        int peers, float? listeningAt, int? listeningPort, string? linkedName, float? lockShown,
        float? handoff, Span<HandshakeLine> lines)
    {
        int n = 0;
        lines[n++] = peers > 0
            ? new HandshakeLine(HandshakeLineKind.Paired, FirstPulse, Count: peers)
            : new HandshakeLine(HandshakeLineKind.NonePaired, FirstPulse);
        if (listeningAt is { } la)
            lines[n++] = new HandshakeLine(HandshakeLineKind.Listening, Math.Max(la, ListeningMin), Port: listeningPort);
        if (lockShown is { } ls && linkedName is not null && (handoff is null || ls <= handoff))
            lines[n++] = new HandshakeLine(HandshakeLineKind.Linked, ls, Name: linkedName);
        if (handoff is { } h)
            lines[n++] = new HandshakeLine(HandshakeLineKind.Opening, h);

        // Stable sort by due, then queue.
        for (int i = 1; i < n; i++)
        {
            var x = lines[i];
            int j = i;
            while (j > 0 && lines[j - 1].At > x.At) { lines[j] = lines[j - 1]; j--; }
            lines[j] = x;
        }
        float prev = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            float at = Math.Max(lines[i].At, prev + LineGap);
            lines[i] = lines[i] with { At = at };
            prev = at;
        }
        return n;
    }
}
