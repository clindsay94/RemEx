using System;
using System.Collections.Generic;
using System.Linq;
using Remex.Agent.Services.Media;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Covers the playback anchor, which exists to stop a live position becoming a per-second broadcast
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// EVERY TEST HERE COUNTS DISTINCT ANCHOR VALUES RATHER THAN CHECKING A POSITION, because that count
/// is the thing that matters. <c>MediaSessionBackgroundService</c> publishes only when the reading
/// differs by value from the last one, so an anchor that changes on an ordinary tick is an identical
/// <c>media_state</c> envelope pushed down every connected socket, once a second, for as long as the
/// user is listening to anything. Spec 1.3 calls that out as the single most important constraint in
/// the design.
/// </para>
/// <para>
/// AN IMPLEMENTATION THAT SIMPLY STAMPED <c>UtcNow</c> AND THE OBSERVED POSITION would satisfy any
/// test that asked "is the position roughly right" and would fail every test in this file. That is
/// the regression these guard.
/// </para>
/// </remarks>
public class PlaybackAnchorTrackerTests
{
    private const string TrackKey = "Hallogallo|Neu!|spotify";
    private const long Epoch = 1_700_000_000_000;

    [Fact]
    public void SteadyPlaybackWithPollJitterKeepsASingleAnchor()
    {
        // Sixty polls is a minute of ordinary listening. The observed position differs on every one of
        // them — that is what a playing track does — and the jitter is what a once-a-second poll over
        // D-Bus or WinRT actually looks like. One anchor value across all sixty is the whole contract.
        var tracker = new PlaybackAnchorTracker();
        var anchors = new HashSet<(long?, long?)>();

        for (var tick = 0; tick < 60; tick++)
        {
            var jitter = ((tick % 5) - 2) * 100; // -200..+200 ms, deterministic
            var observed = Math.Max(0, (tick * 1000L) + jitter);

            anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, observed, Epoch + (tick * 1000L), TrackKey));
        }

        Assert.Single(anchors);
    }

    [Fact]
    public void ASeekChangesTheAnchorExactlyOnce()
    {
        // The positive control for the test above: the tolerance must not be so forgiving that a real
        // seek is absorbed. A 30 s jump is a user dragging the scrubber, and the phone must follow it.
        var tracker = new PlaybackAnchorTracker();
        var anchors = new List<(long?, long?)>();

        for (var tick = 0; tick < 10; tick++)
        {
            anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, tick * 1000L, Epoch + (tick * 1000L), TrackKey));
        }

        for (var tick = 10; tick < 20; tick++)
        {
            var observed = (tick * 1000L) + 30_000L;
            anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, observed, Epoch + (tick * 1000L), TrackKey));
        }

        Assert.Equal(2, anchors.Distinct().Count());

        // And it settled again immediately: the ten polls after the seek all share the new anchor,
        // rather than the seek starting a re-anchor loop.
        Assert.Single(anchors.Skip(10).Distinct());
    }

    [Fact]
    public void ASeekBackwardsAlsoChangesTheAnchor()
    {
        // Symmetry matters here because the divergence is signed. An implementation comparing
        // observed > predicted + tolerance would pass the forward test and freeze the progress bar
        // for anyone who skips back.
        var tracker = new PlaybackAnchorTracker();

        var before = tracker.Observe(MediaPlaybackStatus.Playing, 120_000, Epoch, TrackKey);
        var after = tracker.Observe(MediaPlaybackStatus.Playing, 30_000, Epoch + 1000, TrackKey);

        Assert.NotEqual(before, after);
        Assert.Equal((30_000L, Epoch + 1000), after);
    }

    [Fact]
    public void PausingChangesTheAnchorOnceAndAPausedStreamKeepsIt()
    {
        // A paused track reports the same position forever. If the tracker kept predicting forward
        // through the pause, every paused poll would diverge by another second and re-anchor — the
        // broadcast, on a track nobody is even playing.
        var tracker = new PlaybackAnchorTracker();
        var anchors = new List<(long?, long?)>();

        for (var tick = 0; tick < 5; tick++)
        {
            anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, tick * 1000L, Epoch + (tick * 1000L), TrackKey));
        }

        for (var tick = 5; tick < 15; tick++)
        {
            anchors.Add(tracker.Observe(MediaPlaybackStatus.Paused, 4000L, Epoch + (tick * 1000L), TrackKey));
        }

        Assert.Equal(2, anchors.Distinct().Count());
        Assert.Single(anchors.Skip(5).Distinct());
        Assert.Equal((4000L, Epoch + 5000), anchors[^1]);
    }

    [Fact]
    public void ATrackChangeChangesTheAnchorExactlyOnce()
    {
        // A new track that happens to start where the old one was predicted to be would otherwise
        // keep the old anchor, and the phone would show the previous track's elapsed time.
        var tracker = new PlaybackAnchorTracker();
        var anchors = new List<(long?, long?)>();

        for (var tick = 0; tick < 5; tick++)
        {
            anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, tick * 1000L, Epoch + (tick * 1000L), TrackKey));
        }

        for (var tick = 5; tick < 10; tick++)
        {
            var observed = (tick - 5) * 1000L;
            anchors.Add(tracker.Observe(
                MediaPlaybackStatus.Playing, observed, Epoch + (tick * 1000L), "Negativland|Neu!|spotify"));
        }

        Assert.Equal(2, anchors.Distinct().Count());
        Assert.Equal((0L, Epoch + 5000), anchors[5]);
    }

    [Fact]
    public void ANullObservationKeepsThePreviousAnchor()
    {
        // Players that answer PlaybackStatus but not Position are common. Clearing the anchor would
        // make the progress bar appear and vanish once a second, and each flip is a publish.
        var tracker = new PlaybackAnchorTracker();

        var anchored = tracker.Observe(MediaPlaybackStatus.Playing, 5000, Epoch, TrackKey);
        var kept = tracker.Observe(MediaPlaybackStatus.Playing, null, Epoch + 1000, TrackKey);

        Assert.Equal(anchored, kept);
        Assert.Equal((5000L, Epoch), kept);
    }

    [Fact]
    public void NoObservationEverLeavesTheAnchorUnset()
    {
        // Nothing to anchor to is a legitimate answer, and it must be a STABLE one — (null, null)
        // every poll, not a timestamp that changes.
        var tracker = new PlaybackAnchorTracker();

        var first = tracker.Observe(MediaPlaybackStatus.Playing, null, Epoch, TrackKey);
        var second = tracker.Observe(MediaPlaybackStatus.Playing, null, Epoch + 1000, TrackKey);

        Assert.Equal((null, null), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AHardZeroWhilePlayingIsIgnoredOnceThereIsAnAnchor()
    {
        // Spotify on Linux reports MPRIS Position as 0 forever while playing. Believed literally,
        // every poll diverges from the prediction by another second and re-anchors.
        var tracker = new PlaybackAnchorTracker();
        var anchors = new HashSet<(long?, long?)>();

        anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, 12_000, Epoch, TrackKey));

        for (var tick = 1; tick < 20; tick++)
        {
            anchors.Add(tracker.Observe(MediaPlaybackStatus.Playing, 0, Epoch + (tick * 1000L), TrackKey));
        }

        Assert.Single(anchors);
        Assert.Equal((12_000L, Epoch), anchors.Single());
    }

    [Fact]
    public void AZeroOnANewTrackStillAnchorsAtTheStart()
    {
        // The other half of the rule above: a track that genuinely starts at zero must anchor there,
        // or every track would inherit the previous one's elapsed time. The track key is what tells
        // the two cases apart.
        var tracker = new PlaybackAnchorTracker();

        tracker.Observe(MediaPlaybackStatus.Playing, 12_000, Epoch, TrackKey);
        var started = tracker.Observe(MediaPlaybackStatus.Playing, 0, Epoch + 1000, "Isi|Neu!|spotify");

        Assert.Equal((0L, Epoch + 1000), started);
    }

    [Fact]
    public void DriftInsideTheToleranceIsAbsorbedAndDriftOutsideItIsNot()
    {
        // The boundary, stated once so the 1.5 s figure in spec 1.3 has a test that would notice it
        // being quietly widened or narrowed.
        var inside = new PlaybackAnchorTracker();
        var anchored = inside.Observe(MediaPlaybackStatus.Playing, 0, Epoch, TrackKey);
        Assert.Equal(anchored, inside.Observe(MediaPlaybackStatus.Playing, 2400, Epoch + 1000, TrackKey));

        var outside = new PlaybackAnchorTracker();
        outside.Observe(MediaPlaybackStatus.Playing, 0, Epoch, TrackKey);
        Assert.Equal(
            (2600L, Epoch + 1000),
            outside.Observe(MediaPlaybackStatus.Playing, 2600, Epoch + 1000, TrackKey));
    }
}
