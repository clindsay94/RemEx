using System.Text;

namespace Remex.Core.Validation;

/// <summary>Why a clipboard payload was refused.</summary>
public enum ClipboardRejectReason
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>Nothing to send — the clipboard was empty.</summary>
    Empty,

    /// <summary>Larger than <see cref="ClipboardValidation.MaxPayloadBytes"/>.</summary>
    TooLarge
}

/// <summary>
/// Validates a clipboard payload before it crosses the wire (RemEx-qu3t).
/// </summary>
/// <remarks>
/// <para>
/// **THE ERROR NEVER CARRIES THE CONTENT, AND THAT IS THE POINT OF PUTTING THIS HERE.** A clipboard
/// holds whatever the user last copied — a password, a 2FA code, a private URL. A validation message
/// that quoted the payload, or a log line that echoed it, would leak exactly the thing this feature
/// is trusted with. So the result is an enum plus a length, never the text, and callers building a
/// user-facing message have nothing sensitive to accidentally interpolate.
/// </para>
/// <para>
/// NativeAOT-safe: no reflection, no dynamic code, no serialization. This is compiled into
/// <c>libRemexCore.so</c> for Android, and both sides of the transfer validate with the same rule
/// rather than each inventing one.
/// </para>
/// </remarks>
public static class ClipboardValidation
{
    /// <summary>
    /// Largest payload accepted, in BYTES.
    /// </summary>
    /// <remarks>
    /// 256 KB: comfortably more than any snippet, URL or code a person copies deliberately, and far
    /// below the size at which a stray copy of a large document would stall a phone on a slow link.
    /// </remarks>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>
    /// Checks a payload, reporting the reason and the measured size.
    /// </summary>
    /// <param name="text">The clipboard text.</param>
    /// <param name="byteCount">
    /// The UTF-8 size that was measured, so a caller can tell the user how far over the limit they
    /// are without ever handling the content itself.
    /// </param>
    /// <remarks>
    /// **SIZE IS MEASURED IN UTF-8 BYTES, NOT IN CHARACTERS**, and the difference is not academic:
    /// the wire carries bytes, and CJK text is three bytes per character, so a character-based cap
    /// of 256 K would admit a 768 KB payload — three times the intended limit, and only ever for
    /// users writing in Chinese, Japanese or Korean. A limit that is really three different limits
    /// depending on the user's language is the kind of bug that gets reported as "it works on my
    /// machine".
    /// </remarks>
    public static ClipboardRejectReason Validate(string? text, out int byteCount)
    {
        byteCount = 0;

        // AN EMPTY CLIPBOARD IS REFUSED RATHER THAN SENT. Sending it would silently CLEAR the other
        // machine's clipboard, destroying something the user had deliberately put there - and they
        // would have no way to know that is what the button did. Refusing lets the caller say
        // "there is nothing to send", which is both true and harmless.
        if (string.IsNullOrEmpty(text)) return ClipboardRejectReason.Empty;

        byteCount = Encoding.UTF8.GetByteCount(text);

        return byteCount > MaxPayloadBytes
            ? ClipboardRejectReason.TooLarge
            : ClipboardRejectReason.None;
    }

    /// <summary>
    /// Whether a payload may be sent.
    /// </summary>
    public static bool IsAcceptable(string? text) =>
        Validate(text, out _) == ClipboardRejectReason.None;
}
