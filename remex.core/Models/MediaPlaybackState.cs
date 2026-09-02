using System.Text.Json.Serialization;

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
    /// <remarks>
    /// RENDERED AS THE PHONE'S NOW-PLAYING LINE (RemEx-nmvz6), which is why a blank must arrive as
    /// null rather than as an empty string: the client picks its primary line with
    /// <c>title ?: statusWord</c>, and <c>""</c> would win that fallback and draw an empty row.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>Artist or author, when the session publishes one.</summary>
    public string? Artist { get; init; }

    /// <summary>
    /// The app the session belongs to — a Windows AUMID or an MPRIS bus suffix, not a display name.
    /// </summary>
    /// <remarks>
    /// CARRIED BUT DELIBERATELY NOT RENDERED, unlike <see cref="Title"/> and <see cref="Artist"/>,
    /// which the now-playing line does draw. This one is an IDENTIFIER —
    /// <c>Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic</c>, not "Groove" — so putting it in
    /// front of the user would mean showing them a string and calling it their app's name. The phone
    /// does not even parse it. It stays on the wire because the platform reads produce all three in
    /// the same call and because turning it into a display name later should be a UI change rather
    /// than a protocol one.
    /// </remarks>
    public string? SourceApp { get; init; }

    /// <summary>
    /// Opaque handle for the current artwork — the first 16 lowercase hex characters of the SHA-256
    /// of the image bytes — or null when there is nothing to draw.
    /// </summary>
    /// <remarks>
    /// AN ID RATHER THAN THE IMAGE, BECAUSE THIS RECORD IS THE THING WHOSE EQUALITY DECIDES WHETHER
    /// TO BROADCAST. Embedding a megabyte of PNG here would put that megabyte through every value
    /// comparison and, worse, onto the wire again on every unrelated field change. The id is stable
    /// for identical bytes, so a phone that already has it draws from its own cache and never asks;
    /// one that does not asks once with <c>media_artwork_request</c>.
    /// </remarks>
    public string? ArtworkId { get; init; }

    /// <summary>Track length in milliseconds; null or 0 means live or unknown.</summary>
    /// <remarks>
    /// ZERO IS TREATED THE SAME AS NULL ON PURPOSE. Platform sessions publish 0 for streams that have
    /// no end and for tracks whose metadata has not arrived yet, and a progress bar drawn against a
    /// zero length is either a divide by zero or a bar pinned at one end lying about a live stream.
    /// </remarks>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Playback position in milliseconds AT THE MOMENT THIS ENVELOPE WAS SERIALIZED, or null when
    /// the session publishes no timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THIS FIELD IS NULL ON EVERY INSTANCE THE SAMPLER COMPARES, AND THAT IS THE WHOLE DESIGN.**
    /// A position that advances is a position that differs, so a state carrying a live position would
    /// be unequal to the previous one on every single poll — which is exactly the per-second
    /// broadcast to every connected phone that making this a record was meant to stop. The sampler
    /// holds <see cref="AnchorPositionMs"/>/<see cref="AnchorUtcMs"/>, which only change when
    /// playback actually jumps, and <c>MediaPositionProjection</c> fills this in at SEND time.
    /// </para>
    /// <para>
    /// So: read it on the client, never set it on the host outside the projection.
    /// </para>
    /// </remarks>
    public long? PositionMs { get; init; }

    /// <summary>
    /// The position the host last actually observed, in milliseconds. Host-side only — never on the
    /// wire.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonIgnoreAttribute"/> KEEPS IT OFF THE WIRE, BUT IT IS STILL AN ORDINARY RECORD
    /// PROPERTY, so it participates in value equality. That is deliberate: re-anchoring is a real
    /// change in what the host believes about playback — a seek, a track change, a player that
    /// drifted past tolerance — and it must republish. A field excluded from equality would let a
    /// seek pass unnoticed until something else changed.
    /// </remarks>
    [JsonIgnore]
    public long? AnchorPositionMs { get; init; }

    /// <summary>
    /// When <see cref="AnchorPositionMs"/> was observed, as Unix milliseconds on the host's wall
    /// clock (<c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c>). Host-side only.
    /// </summary>
    /// <remarks>
    /// WALL CLOCK RATHER THAN A MONOTONIC TICK COUNT because the projection subtracts it from a
    /// second reading taken elsewhere in the process, and both readings have to come from the same
    /// clock. Off the wire for the same reason as <see cref="AnchorPositionMs"/>, and in equality for
    /// the same reason too.
    /// </remarks>
    [JsonIgnore]
    public long? AnchorUtcMs { get; init; }
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
