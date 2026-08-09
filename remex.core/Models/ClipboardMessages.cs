using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Client → host: put this text on the PC's clipboard (RemEx-hgqs).
/// </summary>
/// <remarks>
/// <para>
/// **THE ONLY FIELD IS THE TEXT, AND THAT IS DELIBERATE.** There is no "source app", no preview, no
/// truncated sample for logging. A clipboard holds whatever the user last copied — a password, a 2FA
/// code, a private URL — so every extra field is another place that content can be echoed into a log
/// or an error message. <see cref="Remex.Core.Validation.ClipboardValidation"/> already returns an
/// enum and a byte count rather than the text for exactly this reason; a payload that carried a
/// preview would hand it straight back.
/// </para>
/// <para>
/// SIZE IS NOT CARRIED EITHER. The receiver measures the UTF-8 bytes itself rather than trusting a
/// declared length, because a declared length is a claim from the peer and the cap exists to bound
/// what the peer can make the host hold.
/// </para>
/// <para>
/// Additive and optional, so it needs no <c>ProtocolVersion</c> bump: a host that does not know this
/// type drops it in silence, which is the correct outcome for a clipboard write that cannot happen.
/// </para>
/// </remarks>
public sealed record ClipboardPush
{
    [JsonPropertyName("text")] public required string Text { get; init; }
}
