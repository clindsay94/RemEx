using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Windows.Media.Control;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Reads the Windows System Media Transport Controls session (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE SAME SESSION THE VOLUME FLYOUT SHOWS, which is what makes it the right one to report:
/// it is whatever <c>VK_MEDIA_PLAY_PAUSE</c> will act on, and that key is exactly what the phone's
/// button sends. Reading some other notion of "playing" would produce an icon that is accurate about
/// something the button does not control.
/// </para>
/// <para>
/// EVERY READ RE-ASKS FOR THE MANAGER RATHER THAN CACHING IT. <c>RequestAsync</c> is cheap after the
/// first call, and the cached alternative has to answer what happens when the SMTC service restarts —
/// a stale manager keeps returning the session that existed when it was captured, which is the stale
/// reading this whole feature exists to eliminate.
/// </para>
/// <para>
/// IT IS ALSO THE ARTWORK SOURCE (RemEx-vtorl), because the cover comes out of the same session
/// object the reading did. The two interfaces stay separate — see <see cref="IMediaArtworkSource"/>
/// — so the poll tick never pays for a thumbnail stream, but one class implements both because one
/// class owns the session.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowsMediaSessionReader(ILogger<WindowsMediaSessionReader> logger)
    : IMediaSessionReader, IMediaArtworkSource
{
    /// <summary>
    /// The anchor state for this reader's readings.
    /// </summary>
    /// <remarks>
    /// RUN ON WINDOWS TOO, EVEN THOUGH SMTC HANDS US AN ANCHOR. <c>LastUpdatedTime</c> is the OS's own
    /// anchor timestamp and spec 1.3 says to use it directly — which is what feeding it into the
    /// observed position below does. But several shipping players re-stamp it once a second while
    /// changing nothing else, and passing that straight onto the gated record would republish
    /// <c>media_state</c> every second on exactly those machines. The tracker is what turns a
    /// re-stamped timeline back into an unchanged value.
    /// </remarks>
    private readonly PlaybackAnchorTracker _anchors = new();

    public bool IsSupported => true;

    public async Task<MediaPlaybackState> ReadAsync(CancellationToken ct)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
            var session = manager.GetCurrentSession();

            if (session is null)
            {
                // NOTHING IS PLAYING, WHICH IS A READING. Distinct from Unknown below: the host asked
                // and Windows answered "no session", so the phone can say the next press will start
                // something rather than falling back to the neutral face.
                return new MediaPlaybackState { Status = MediaPlaybackStatus.None };
            }

            var status = session.GetPlaybackInfo().PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.None,

                // Opened and Changing are transitional. NEITHER IS A GUESS AT THE OUTCOME: a player
                // that has loaded a track but not started it, and one mid-swap, are both states where
                // drawing "playing" would be wrong half the time. Unknown holds the previous face for
                // the fraction of a second they last.
                _ => MediaPlaybackStatus.Unknown,
            };

            var (title, artist) = await ReadPropertiesAsync(session, ct);
            var sourceApp = Blank(session.SourceAppUserModelId);

            var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (durationMs, observedPositionMs) = ReadTimeline(session, status);
            var (anchorPositionMs, anchorUtcMs) = _anchors.Observe(
                status, observedPositionMs, nowUtcMs, TrackKey(title, artist, sourceApp));

            return new MediaPlaybackState
            {
                Status = status,
                Title = title,
                Artist = artist,
                SourceApp = sourceApp,
                DurationMs = durationMs,
                AnchorPositionMs = anchorPositionMs,
                AnchorUtcMs = anchorUtcMs,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // BROAD ON PURPOSE, AND THE ONE PLACE IT IS. WinRT surfaces a COM HRESULT as any of
            // several exception types, the set is not documented, and this call reaches whatever media
            // player the user happens to have open. A named list here would be a guess that fails
            // closed only for the cases someone thought of. The contract is "never throws", so this
            // catch is the contract rather than a safety net over it.
            logger.LogDebug(ex, "Could not read the Windows media session.");
            return new MediaPlaybackState { Status = MediaPlaybackStatus.Unknown };
        }
    }

    /// <summary>
    /// Title and artist, or nulls when the session will not say.
    /// </summary>
    /// <remarks>
    /// SEPARATELY GUARDED FROM THE STATUS READ ABOVE, because they fail independently and they are
    /// not equally important. <c>TryGetMediaPropertiesAsync</c> reaches further into the player than
    /// <c>GetPlaybackInfo</c> does and is the likelier of the two to refuse — and the status is the
    /// half the button actually needs. Losing the metadata must not cost the reading.
    /// </remarks>
    private async Task<(string? Title, string? Artist)> ReadPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session, CancellationToken ct)
    {
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(ct);
            return (Blank(properties.Title), Blank(properties.Artist));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Media session gave no track metadata.");
            return (null, null);
        }
    }

    /// <summary>
    /// Track length and where the session says it is, both in milliseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// POSITIONS ARE RELATIVE TO <c>StartTime</c>, NOT TO ZERO. SMTC's timeline is an interval, and
    /// for a chaptered audiobook or a clipped stream <c>StartTime</c> is not zero — subtracting it is
    /// the difference between a progress bar that starts full and one that starts empty.
    /// </para>
    /// <para>
    /// THE OBSERVATION IS ADVANCED BY <c>now - LastUpdatedTime</c> WHILE PLAYING, and that is spec
    /// 1.3's "use <c>LastUpdatedTime</c> directly". <c>Position</c> is a snapshot from whenever the
    /// player last bothered to update, which for most players is a seek or a track change, not this
    /// second; reporting it unadvanced would freeze the phone's progress bar for minutes at a time.
    /// </para>
    /// <para>
    /// A DEFAULT <c>LastUpdatedTime</c> IS NO OBSERVATION AT ALL. Sessions that never publish a
    /// timeline leave it at its zero value, and treating that as "the position was true in the year
    /// 1601" would advance the observation by four centuries. Null means the tracker keeps whatever
    /// it had, which is the honest answer.
    /// </para>
    /// <para>
    /// It also does not throw. <c>GetTimelineProperties</c> reaches into the same third-party player
    /// as everything else here, and a missing progress bar must not cost the play/pause icon.
    /// </para>
    /// </remarks>
    private (long? DurationMs, long? ObservedPositionMs) ReadTimeline(
        GlobalSystemMediaTransportControlsSession session, string status)
    {
        try
        {
            var timeline = session.GetTimelineProperties();

            var duration = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
            long? durationMs = duration > 0 ? duration : null;

            if (timeline.LastUpdatedTime == default)
            {
                return (durationMs, null);
            }

            var position = (timeline.Position - timeline.StartTime).TotalMilliseconds;

            if (string.Equals(status, MediaPlaybackStatus.Playing, StringComparison.Ordinal))
            {
                var elapsed = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds;
                if (elapsed > 0)
                {
                    position += elapsed;
                }
            }

            // Clamped at zero only. NOT clamped to the duration on purpose: a player that resumes
            // without updating its timeline leaves a stale LastUpdatedTime and the projection
            // overshoots until it next updates — the OS media flyout shows the same overshoot, and
            // pinning it to the duration would instead make every such track look finished.
            return (durationMs, position > 0 ? (long)position : 0);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Media session gave no timeline.");
            return (null, null);
        }
    }

    /// <summary>
    /// What identifies "still the same thing playing" for the anchor tracker.
    /// </summary>
    /// <remarks>
    /// TITLE, ARTIST AND APP RATHER THAN ANY ID, because SMTC does not expose one that survives a
    /// player restart, and this only has to be sensitive to CHANGE. Two consecutive tracks with the
    /// same title, artist and app is the same track in every case that matters.
    /// </remarks>
    private static string TrackKey(string? title, string? artist, string? sourceApp)
        => $"{title}|{artist}|{sourceApp}";

    /// <inheritdoc />
    /// <remarks>
    /// THE SESSION IS RE-REQUESTED RATHER THAN CAPTURED FROM THE READ, for the same reason
    /// <see cref="ReadAsync"/> re-requests the manager: this runs off the poll tick, possibly a
    /// second later, and a captured session that has since closed answers with the track that was
    /// playing when it was captured. Re-requesting and then CONFIRMING THE TITLE is what makes a
    /// late-arriving cover belong to the track it is stored against — without that check the store
    /// ends up mapping the new track's artwork id to the old track's image.
    /// </remarks>
    public Task<byte[]?> ResolveArtworkAsync(MediaPlaybackState state, CancellationToken ct)
        => MediaArtworkFallback.FirstNonEmptyAsync(
            [
                c => ReadSessionThumbnailAsync(state, c),
                c => WindowsAppIconResolver.ResolveAsync(state.SourceApp, c),
            ],
            ct);

    private async Task<byte[]?> ReadSessionThumbnailAsync(MediaPlaybackState state, CancellationToken ct)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
        var session = manager.GetCurrentSession();
        if (session is null)
        {
            return null;
        }

        var properties = await session.TryGetMediaPropertiesAsync().AsTask(ct);
        if (!string.Equals(Blank(properties.Title), state.Title, StringComparison.Ordinal))
        {
            // The session moved on between the reading and this resolve. Returning the new track's
            // art under the old track's id is worse than returning nothing.
            return null;
        }

        return await WindowsAppIconResolver.ReadImageStreamAsync(properties.Thumbnail, ct);
    }

    /// <summary>Empty and whitespace become null, so absent is one value on the wire rather than three.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
