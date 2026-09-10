using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// The phone's palette at one instant, carried by <c>theme_sync</c> (RemEx-y06a0.1, RemEx-sudp8).
/// </summary>
/// <remarks>
/// <para>
/// CLIENT TO HOST ONLY. The phone sends this once after pairing/reconnect and again whenever its
/// own theme flow changes (debounced to one message per ~500ms on that side); the host never
/// replies. There is therefore no audience entry — <c>MessageAudience</c> describes host → client
/// traffic — and no JNI routing concern: a message this shape lands in the agent's own dispatcher
/// and never needs to reach back into Android at all.
/// </para>
/// <para>
/// <see cref="SeedHex"/>, <see cref="Style"/>, <see cref="Mode"/>, <see cref="Contrast"/>,
/// <see cref="DynamicColor"/> and <see cref="SentAtUnixMs"/> are the wire fields, verbatim from the
/// phone (RemEx-y06a0.1's <c>ThemeSync.buildEnvelope</c>) — <see cref="Style"/> and
/// <see cref="Mode"/> travel exactly as Android's <c>SettingsManager</c> stores them
/// (<c>tonal_spot</c>, <c>system</c>, …); this record does not translate them. <see cref="ClientId"/>
/// and <see cref="ReceivedUtc"/> are never on the wire — they are stamped onto a copy by
/// <c>PingPongHandler</c> once a message has been validated, via a <c>with</c> expression, so the
/// same record shape serves as both the deserialization target and the stored snapshot without a
/// second type to keep in sync.
/// </para>
/// <para>
/// NONE OF THE WIRE FIELDS ARE <c>required</c> (Opus review, RemEx-sudp8 fix round). A required
/// member missing from the JSON makes System.Text.Json throw, which <c>MessageSerializer.Deserialize</c>
/// turns into a null <c>RemexMessage</c> — and a null message makes <c>PingPongHandler</c>'s
/// receive loop treat it as a disconnect and drop the WHOLE session, for every message type, not
/// just this one. A malformed or truncated <c>theme_sync</c> must be rejectABLE, not
/// connection-ending, so every field defaults instead (empty string / 0 / false) and
/// <c>PingPongHandler.IsValidThemeSync</c> is what actually refuses an incomplete snapshot.
/// </para>
/// </remarks>
public sealed record PhoneThemeSnapshot
{
    /// <summary><c>#RRGGBB</c>. The resolved scheme primary when the phone is on dynamic colour.</summary>
    [JsonPropertyName("seed")]
    public string SeedHex { get; init; } = string.Empty;

    /// <summary>
    /// Android's <c>themeStyle</c> name verbatim (<c>tonal_spot</c>, <c>vibrant</c>, …). An
    /// unrecognised value is a PC-side mapping decision (<c>CustomizationViewModel.TryMapPhoneTheme</c>),
    /// not one made here.
    /// </summary>
    public string Style { get; init; } = string.Empty;

    /// <summary><c>light</c>, <c>dark</c>, or <c>system</c> — the phone's light/dark preference.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>
    /// The phone's <c>themeContrast</c>, in Android's own M3 range of -1.0..1.0 — negative means
    /// reduced contrast, which the PC's 0.0..1.0 <c>ThemeContrast</c> has no room for. Carried
    /// verbatim here; <c>CustomizationViewModel.TryMapPhoneTheme</c> is where it gets clamped into
    /// the PC's range, not this record.
    /// </summary>
    public double Contrast { get; init; }

    /// <summary>Whether the phone is on dynamic (wallpaper) colour.</summary>
    [JsonPropertyName("dynamic")]
    public bool DynamicColor { get; init; }

    /// <summary>The phone's wall clock at send time. For "last seen" display only; never for ordering.</summary>
    public long SentAtUnixMs { get; init; }

    /// <summary>
    /// The paired device this arrived from, stamped by the host after receipt. Never sent by the
    /// phone and absent from the wire payload.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>When the host accepted this snapshot, stamped by the host after receipt.</summary>
    public DateTimeOffset ReceivedUtc { get; init; }
}
