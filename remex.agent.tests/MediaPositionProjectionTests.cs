using Remex.Agent.Services.Media;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that <see cref="MediaPositionProjection"/> computes the wire position from the anchor, and
/// never from the anchor state's own (always-null) <c>PositionMs</c> (RemEx-vtorl).
/// </summary>
public class MediaPositionProjectionTests
{
    private static MediaPlaybackState Anchor(
        string status, long? anchorPositionMs, long? anchorUtcMs, long? durationMs = null)
        => new()
        {
            Status = status,
            AnchorPositionMs = anchorPositionMs,
            AnchorUtcMs = anchorUtcMs,
            DurationMs = durationMs,
        };

    [Fact]
    public void PausedProjectsToExactlyTheAnchorPosition()
    {
        var anchor = Anchor(MediaPlaybackStatus.Paused, anchorPositionMs: 5_000, anchorUtcMs: 1_000_000);

        var projected = MediaPositionProjection.Project(anchor, nowUtcMs: 1_050_000);

        Assert.Equal(5_000, projected.PositionMs);
    }

    [Fact]
    public void PlayingProjectsForwardByElapsedWallClockTime()
    {
        var anchor = Anchor(MediaPlaybackStatus.Playing, anchorPositionMs: 5_000, anchorUtcMs: 1_000_000);

        var projected = MediaPositionProjection.Project(anchor, nowUtcMs: 1_003_000);

        Assert.Equal(8_000, projected.PositionMs);
    }

    [Fact]
    public void ANullAnchorProjectsToNull()
    {
        var anchor = Anchor(MediaPlaybackStatus.Playing, anchorPositionMs: null, anchorUtcMs: null);

        var projected = MediaPositionProjection.Project(anchor, nowUtcMs: 1_000_000);

        Assert.Null(projected.PositionMs);
    }

    [Fact]
    public void ProjectionIsClampedToDuration()
    {
        var anchor = Anchor(
            MediaPlaybackStatus.Playing, anchorPositionMs: 9_000, anchorUtcMs: 1_000_000, durationMs: 10_000);

        // Ten seconds of playback would put the raw projection at 19s, well past the 10s track.
        var projected = MediaPositionProjection.Project(anchor, nowUtcMs: 1_010_000);

        Assert.Equal(10_000, projected.PositionMs);
    }

    [Fact]
    public void TheReturnedInstanceIsNeverTheSameReferenceAndTheInputPositionStaysNull()
    {
        var anchor = Anchor(MediaPlaybackStatus.Playing, anchorPositionMs: 1_000, anchorUtcMs: 1_000_000);

        var projected = MediaPositionProjection.Project(anchor, nowUtcMs: 1_002_000);

        Assert.NotSame(anchor, projected);
        Assert.Null(anchor.PositionMs);
        Assert.NotNull(projected.PositionMs);
    }

    [Fact]
    public void ANullAnchorStillReturnsAFreshInstance()
    {
        // Even the "nothing to project" case must not hand back the same reference: the caller keeps
        // comparing the ORIGINAL anchor object for its own gate, never whatever this returns.
        var anchor = Anchor(MediaPlaybackStatus.Paused, anchorPositionMs: null, anchorUtcMs: null);

        var projected = MediaPositionProjection.Project(anchor, nowUtcMs: 1_000_000);

        Assert.NotSame(anchor, projected);
        Assert.Null(projected.PositionMs);
    }
}
