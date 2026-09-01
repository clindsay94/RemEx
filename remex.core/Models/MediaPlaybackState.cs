namespace Remex.Core.Models;

/// <summary>
/// What the PC is currently playing, pushed to clients as <c>media_state</c> (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// THIS EXISTS BECAUSE THE PHONE'S PLAY/PAUSE BUTTON WAS A PICTURE, NOT A READING. The media row
/// (RemEx-hulc) sends <c>VK_MEDIA_PLAY_PAUSE</c>, which is a TOGGLE, and drew a static play triangle
/// forever because nothing in the protocol carried playback state. So the one control whose whole job
/// is to tell you which way the toggle will go could not.
/// </para>
/// <para>
/// A RECORD, SO "CHANGED" IS A VALUE QUESTION. The host publishes a new instance only when this
/// differs from the last one by value, which is what stops a per-second poll turning into a
/// per-second broadcast of an identical envelope to every connected phone.
/// </para>
/// <para>
/// EVERY FIELD IS OPTIONAL EXCEPT THE STATUS, AND THE STATUS DEGRADES TO <see
/// cref="MediaPlaybackStatus.Unknown"/> RATHER THAN TO A GUESS. A phone that cannot tell playing from
/// paused must draw the neutral triangle it always drew — the failure mode this replaces is a button
/// that lies about the machine, and "I do not know" is not an improvement on it if it renders as
/// "paused".
/// </para>
/// </remarks>
public sealed record MediaPlaybackState
{
    /// <summary>
    /// One of the <see cref="MediaPlaybackStatus"/> tokens.
    /// </summary>
    /// <remarks>
    /// A STRING TOKEN RATHER THAN AN ENUM ON THE WIRE, the same choice and the same reason as
    /// <c>onPairingProgress</c>: a receiver that meets a token it does not know must be able to fall
    /// back to saying nothing, and an enum deserialises an unknown value to whichever member happens
    /// to be zero.
    /// </remarks>
    public string Status { get; init; } = MediaPlaybackStatus.Unknown;

    /// <summary>Track title, when the session publishes one.</summary>
    public string? Title { get; init; }

    /// <summary>Artist or author, when the session publishes one.</summary>
    public string? Artist { get; init; }

    /// <summary>
    /// The app the session belongs to — a Windows AUMID or an MPRIS bus suffix, not a display name.
    /// </summary>
    /// <remarks>
    /// CARRIED BUT NOT YET RENDERED, along with <see cref="Title"/> and <see cref="Artist"/>. The
    /// platform reads produce all three in the same call, so including them costs nothing here, and
    /// omitting them would make the eventual now-playing line a protocol change rather than a UI one.
    /// Nothing on the phone reads them today; do not treat their presence as a promise that something
    /// does.
    /// </remarks>
    public string? SourceApp { get; init; }
}

/// <summary>
/// The tokens <see cref="MediaPlaybackState.Status"/> may carry.
/// </summary>
/// <remarks>
/// <see cref="None"/> and <see cref="Stopped"/> are genuinely different and both are worth sending:
/// nothing is playing music at all, versus a player is open and stopped. They render the same today —
/// both mean "the next press will start something" — but collapsing them here would throw away the
/// distinction before anyone can decide whether it matters.
/// </remarks>
public static class MediaPlaybackStatus
{
    /// <summary>A session exists and is playing.</summary>
    public const string Playing = "playing";

    /// <summary>A session exists and is paused.</summary>
    public const string Paused = "paused";

    /// <summary>A session exists and is stopped.</summary>
    public const string Stopped = "stopped";

    /// <summary>No media session exists on the host at all.</summary>
    public const string None = "none";

    /// <summary>
    /// The host cannot tell. A platform read failed, or this host does not implement the feature.
    /// </summary>
    /// <remarks>
    /// NOT INTERCHANGEABLE WITH <see cref="None"/>. "Nothing is playing" is a fact about the PC;
    /// this is a fact about the reading. A client must not draw a paused or a playing face for it.
    /// </remarks>
    public const string Unknown = "unknown";
}
