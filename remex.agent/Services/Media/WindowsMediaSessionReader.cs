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
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowsMediaSessionReader(ILogger<WindowsMediaSessionReader> logger) : IMediaSessionReader
{
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

            return new MediaPlaybackState
            {
                Status = status,
                Title = title,
                Artist = artist,
                SourceApp = Blank(session.SourceAppUserModelId),
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

    /// <summary>Empty and whitespace become null, so absent is one value on the wire rather than three.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
