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

    /// <summary>Byte count, never the text.</summary>
    /// <remarks>
    /// **A RECORD'S GENERATED ToString() PRINTS EVERY PROPERTY**, so the default would render the
    /// user's clipboard into any log line that ever formats this — or formats a
    /// <see cref="Remex.Core.Messages.RemexMessage"/> holding it, which happens transitively. Nothing
    /// does today. This makes that a property of the type instead of something every future author
    /// has to remember, in a codebase where <c>LogWarning("... {Msg}", message)</c> is one keystroke.
    /// </remarks>
    public override string ToString() =>
        $"ClipboardPush {{ Bytes = {System.Text.Encoding.UTF8.GetByteCount(Text)} }}";
}

/// <summary>
/// Host → client: what is on the PC's clipboard, or why it is not being sent (RemEx-ci98m).
/// </summary>
/// <remarks>
/// <para>
/// **THE REASON IS CARRIED AND THE TEXT IS OPTIONAL, WHICH IS THE OPPOSITE SHAPE TO THE PUSH.** The
/// push direction can decide every refusal on the sending phone before anything crosses the wire; a
/// fetch cannot, because only the PC knows what is on its own clipboard. So a refusal has to travel,
/// and it travels as a token rather than a sentence — the host does not know the phone's language,
/// and the phone already owns the translations.
/// </para>
/// <para>
/// **AN EMPTY OR REFUSED ANSWER CARRIES NO TEXT AT ALL** rather than an empty string. A phone that
/// pasted an empty string into its clipboard would destroy whatever the user had copied there, which
/// is the same harm the push direction refuses an empty payload to avoid.
/// </para>
/// </remarks>
public sealed record ClipboardContent
{
    /// <summary>One of <c>none</c>, <c>empty</c>, <c>too_large</c>, <c>unavailable</c>.</summary>
    /// <remarks>
    /// The same vocabulary <see cref="Remex.Core.Validation.ClipboardValidation.ToNativeJson"/> uses
    /// for the push direction, deliberately: one set of tokens for one feature, so the phone maps
    /// them in one place instead of two that can drift.
    /// </remarks>
    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>The clipboard text. Null unless <see cref="Reason"/> is <c>none</c>.</summary>
    [JsonPropertyName("text")] public string? Text { get; init; }

    /// <summary>Reason and byte count, never the text. See <see cref="ClipboardPush.ToString"/>.</summary>
    public override string ToString() =>
        $"ClipboardContent {{ Reason = {Reason}, Bytes = {(Text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(Text))} }}";
}

/// <summary>
/// Host → client: what the PC actually did with a pushed clipboard (RemEx-s1ay7).
/// </summary>
/// <remarks>
/// <para>
/// **THIS EXISTS BECAUSE THE PHONE WAS TELLING PEOPLE A PUSH HAD WORKED WHEN THE PC HAD REFUSED
/// IT.** The push is sent fire-and-forget, so the phone learned only that the message reached the
/// socket, and every host-side refusal — an unpaired connection, a clipboard it could not reach, an
/// older host with no handler at all — rendered as "Sent to the PC's clipboard". That was observed,
/// not theorised: a real emulator push was refused by the pairing gate and reported as success.
/// </para>
/// <para>
/// Deciding refusals on the phone first was the right call and stays — an empty clipboard should not
/// need a network round trip to say so. It simply cannot cover the outcomes only the PC knows.
/// </para>
/// <para>
/// **THE SAME REASON VOCABULARY AS EVERY OTHER PART OF THIS FEATURE**, plus <c>refused</c> for the
/// case the phone cannot predict at all. One set of tokens means the phone maps them in one place
/// instead of two that drift, and an unrecognised token fails closed on arrival.
/// </para>
/// </remarks>
public sealed record ClipboardPushResult
{
    /// <summary>One of <c>none</c> (written), <c>empty</c>, <c>too_large</c>, <c>unavailable</c>, <c>refused</c>.</summary>
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
