using Remex.Core.Models;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Fills in <see cref="MediaPlaybackState.PositionMs"/> at send time from the anchor the reading
/// carries (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// RUNS ONCE PER SEND, NOT ONCE PER POLL. <see cref="MediaPlaybackState.PositionMs"/> is null on
/// every instance the sampler compares and publishes — see the field's own remarks — so the value a
/// phone actually reads has to be computed on the copy that goes out the socket, from the anchor the
/// sampler is holding, never on the record the gate compares.
/// </para>
/// <para>
/// A <c>with</c>-COPY EVERY TIME, EVEN WHEN THE ANCHOR IS NULL. The caller (<c>PingPongHandler</c>)
/// keeps tracking the anchor object it was handed for its own change comparison, so this must never
/// mutate or return that same instance — a fresh projection each send is what keeps "what the gate
/// compared" and "what went on the wire" from ever being the same reference by accident.
/// </para>
/// </remarks>
internal static class MediaPositionProjection
{
    /// <summary>
    /// Returns a copy of <paramref name="anchorState"/> with <see cref="MediaPlaybackState.PositionMs"/>
    /// projected forward to <paramref name="nowUtcMs"/>.
    /// </summary>
    public static MediaPlaybackState Project(MediaPlaybackState anchorState, long nowUtcMs)
    {
        long? projected = null;

        if (anchorState.AnchorPositionMs is { } anchorPositionMs)
        {
            // Paused (or stopped, or unknown): the position has not moved since the anchor was taken.
            // Playing: add the wall-clock time that has passed since then. Both readings come from the
            // same clock (DateTimeOffset.UtcNow), which is exactly what AnchorUtcMs' own remarks require.
            var elapsedMs = anchorState.Status == MediaPlaybackStatus.Playing
                ? nowUtcMs - (anchorState.AnchorUtcMs ?? nowUtcMs)
                : 0;

            projected = anchorPositionMs + elapsedMs;

            if (anchorState.DurationMs is { } durationMs && durationMs > 0)
            {
                projected = Math.Clamp(projected.Value, 0, durationMs);
            }
            else if (projected < 0)
            {
                projected = 0;
            }
        }

        return anchorState with { PositionMs = projected };
    }
}
