using Remex.Core.Models;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Turns a stream of observed playback positions into an anchor that only moves when playback
/// actually jumps (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THIS CLASS EXISTS TO STOP A PER-SECOND BROADCAST, and that is the whole of it. Spec 1.3 states the
/// rule plainly: "Do not stamp <c>DateTime.UtcNow</c> on every read. That is the per-second
/// broadcast, rebuilt." <c>MediaSessionBackgroundService</c> publishes only when the reading differs
/// BY VALUE from the last one, so any field that changes every tick — a live position, or a
/// timestamp taken at read time — turns a cheap local poll into an identical envelope pushed down
/// every connected socket, once a second, forever. The gated record therefore carries an ANCHOR
/// (a position plus the wall-clock instant at which it was true) and the anchor must survive
/// ordinary playback untouched.
/// </para>
/// <para>
/// SO THE TEST OF THIS CLASS IS NOT "IS THE POSITION RIGHT", IT IS "DID THE VALUE STAY EQUAL". A
/// track playing normally for a minute must yield one anchor value across all sixty polls even
/// though every observation differs; a seek must yield a new one exactly once. Both platforms run it
/// — Windows too. SMTC's <c>LastUpdatedTime</c> is already an OS-maintained anchor, but several real
/// players re-stamp it every second while changing nothing else, so passing it straight through
/// reintroduces the broadcast on exactly the machines it is meant to be free on.
/// </para>
/// <para>
/// NOT THREAD-SAFE, ON PURPOSE. One instance belongs to one reader, and a reader is driven by the
/// single-threaded sampler loop. A lock here would advertise a concurrency this has never had.
/// </para>
/// </remarks>
/// <param name="toleranceMs">
/// How far an observation may drift from the prediction before it counts as a jump. 1.5 s per spec
/// 1.3: wide enough to absorb poll jitter, D-Bus round trips and players that round to the second,
/// narrow enough that a real seek is never mistaken for drift.
/// </param>
internal sealed class PlaybackAnchorTracker(long toleranceMs = 1500)
{
    private bool _seen;
    private string? _status;
    private string? _trackKey;
    private long? _anchorPositionMs;
    private long? _anchorUtcMs;

    /// <summary>
    /// Folds one observation in and returns the anchor to publish.
    /// </summary>
    /// <param name="status">The status token from <see cref="MediaPlaybackStatus"/> for this reading.</param>
    /// <param name="observedPositionMs">
    /// Where the player says it is, or null when it would not say. Null is a normal answer: MPRIS
    /// players omit <c>Position</c>, and an SMTC session with no timeline gives nothing to advance.
    /// </param>
    /// <param name="nowUtcMs">Host wall clock, Unix milliseconds, sampled once by the caller.</param>
    /// <param name="trackKey">
    /// What identifies "the same thing is still playing" — <c>Title|Artist|SourceApp</c>. A change
    /// here is a new track, and a new track always earns a new anchor even if the position happens to
    /// land where the old one predicted.
    /// </param>
    public (long? AnchorPositionMs, long? AnchorUtcMs) Observe(
        string status, long? observedPositionMs, long nowUtcMs, string? trackKey)
    {
        // A CHANGE OF CONTEXT IS ALWAYS A NEW ANCHOR, without consulting the tolerance. Pause, resume
        // and track change all break the relationship between the old anchor and the new reading —
        // the prediction is computed from a status that no longer holds — so comparing against it
        // would be comparing against a number that means nothing.
        var contextChanged = !_seen
            || !string.Equals(_status, status, StringComparison.Ordinal)
            || !string.Equals(_trackKey, trackKey, StringComparison.Ordinal);

        _seen = true;
        _status = status;
        _trackKey = trackKey;

        if (contextChanged)
        {
            _anchorPositionMs = observedPositionMs;
            _anchorUtcMs = observedPositionMs is null ? null : nowUtcMs;
            return (_anchorPositionMs, _anchorUtcMs);
        }

        // NOTHING OBSERVED KEEPS WHAT WE HAD rather than clearing it. A player that answers the
        // status call but not the position call is common (browsers, several MPRIS implementations),
        // and dropping the anchor would make the phone's progress bar appear and vanish once a second
        // — which is both a worse picture and, because the record changes, a broadcast.
        if (observedPositionMs is null)
        {
            return (_anchorPositionMs, _anchorUtcMs);
        }

        // A HARD ZERO WHILE PLAYING IS TREATED AS NO OBSERVATION, and this one is bought with real
        // pain: Spotify on Linux reports MPRIS Position as 0 forever while playing. Believed
        // literally, that is a 30-second divergence within half a minute, a re-anchor, and then the
        // same thing again on the next tick — the per-second broadcast with extra steps. A genuine
        // start-of-track zero still lands, because a new track changes the track key above and takes
        // the context branch.
        if (observedPositionMs == 0
            && string.Equals(status, MediaPlaybackStatus.Playing, StringComparison.Ordinal)
            && _anchorPositionMs is not null)
        {
            return (_anchorPositionMs, _anchorUtcMs);
        }

        if (_anchorPositionMs is null || _anchorUtcMs is null)
        {
            // The context has not changed but we never had an anchor — the player has started
            // answering the position call. Take it.
            _anchorPositionMs = observedPositionMs;
            _anchorUtcMs = nowUtcMs;
            return (_anchorPositionMs, _anchorUtcMs);
        }

        var elapsed = string.Equals(status, MediaPlaybackStatus.Playing, StringComparison.Ordinal)
            ? nowUtcMs - _anchorUtcMs.Value
            : 0;
        var predicted = _anchorPositionMs.Value + elapsed;

        if (Math.Abs(observedPositionMs.Value - predicted) > toleranceMs)
        {
            _anchorPositionMs = observedPositionMs;
            _anchorUtcMs = nowUtcMs;
        }

        return (_anchorPositionMs, _anchorUtcMs);
    }
}
