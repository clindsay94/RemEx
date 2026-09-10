using Remex.Core.Models;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Reads what the PC is currently playing, once (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// ONE READ, NO STATE, NO SUBSCRIPTION — the polling and the change detection live in
/// <see cref="MediaSessionBackgroundService"/> so that every platform gets them identically. A
/// per-platform implementation that also owned its own event plumbing would be three chances to get
/// the "only publish on change" rule subtly different, and that rule is what stops a per-second poll
/// becoming a per-second broadcast to every connected phone.
/// </para>
/// <para>
/// POLLING RATHER THAN EVENTS, WHICH IS A DELIBERATE TRADE. Both platforms offer change
/// notifications — WinRT's <c>PlaybackInfoChanged</c>, MPRIS's <c>PropertiesChanged</c> — and both
/// need the subscription torn down and rebuilt when the ACTIVE SESSION changes, which is the case
/// that actually matters here (you pause Spotify, start a video, and the icon must follow). A missed
/// unsubscribe there is a stale reading that looks exactly like the bug this feature exists to fix.
/// A poll cannot go stale. It costs one cheap call a second against a local API, which is the same
/// bargain the telemetry sampler already makes.
/// </para>
/// </remarks>
internal interface IMediaSessionReader
{
    /// <summary>
    /// Whether this host can read a media session at all, for <c>HostCapabilities</c>.
    /// </summary>
    /// <remarks>
    /// FALSE MEANS "WILL NEVER REPORT", NOT "NOTHING IS PLAYING". The phone needs to tell those apart:
    /// the first keeps the neutral play triangle it has always drawn, the second is a real reading
    /// that the next press will start something. Conflating them is how a control starts lying about
    /// the machine.
    /// </remarks>
    bool IsSupported { get; }

    /// <summary>
    /// The current reading, or <see cref="MediaPlaybackStatus.Unknown"/> when the platform call fails.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS AND NEVER RETURNS NULL. A media session read is a call into a desktop API that can
    /// be mid-restart, refused, or simply absent, and the honest answer to all of those is "I cannot
    /// tell" — which the caller already knows how to render. Letting it throw would put a
    /// third-party media player's state machine in charge of the host's background loop.
    /// </remarks>
    Task<MediaPlaybackState> ReadAsync(CancellationToken ct);
}
