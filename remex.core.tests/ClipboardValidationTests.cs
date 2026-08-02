using System.Text;
using Remex.Core.Validation;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins what may cross the wire as a clipboard payload (RemEx-qu3t).
/// </summary>
/// <remarks>
/// A clipboard holds whatever the user last copied - a password, a 2FA code, a private URL. So the
/// tests are about refusing safely and about never handling the content in a way that could leak it.
/// </remarks>
public class ClipboardValidationTests
{
    [Fact]
    public void AnOrdinarySnippetIsAccepted()
    {
        Assert.Equal(ClipboardRejectReason.None,
            ClipboardValidation.Validate("https://example.com/thing?id=42", out var bytes));
        Assert.Equal(31, bytes);
    }

    [Fact]
    public void AnEmptyClipboardIsRefusedRatherThanSent()
    {
        // SENDING IT WOULD SILENTLY CLEAR THE OTHER MACHINE'S CLIPBOARD, destroying something the
        // user deliberately put there - and nothing would tell them that is what the button did.
        // Refusing lets the caller say "there is nothing to send", which is true and harmless.
        Assert.Equal(ClipboardRejectReason.Empty, ClipboardValidation.Validate(null, out _));
        Assert.Equal(ClipboardRejectReason.Empty, ClipboardValidation.Validate("", out _));
    }

    [Fact]
    public void WhitespaceIsRealContentAndIsSent()
    {
        // Deliberately NOT treated as empty. A user who copied an indented block of code has copied
        // whitespace on purpose, and silently refusing it would look like the feature is broken.
        Assert.Equal(ClipboardRejectReason.None, ClipboardValidation.Validate("   \n\t", out var bytes));
        Assert.Equal(5, bytes);
    }

    [Fact]
    public void APayloadAtTheLimitIsAcceptedAndOneByteOverIsNot()
    {
        var atLimit = new string('a', ClipboardValidation.MaxPayloadBytes);
        var overLimit = new string('a', ClipboardValidation.MaxPayloadBytes + 1);

        Assert.Equal(ClipboardRejectReason.None, ClipboardValidation.Validate(atLimit, out _));
        Assert.Equal(ClipboardRejectReason.TooLarge, ClipboardValidation.Validate(overLimit, out _));
    }

    [Fact]
    public void TheLimitIsMeasuredInBytesNotCharacters()
    {
        // THE TEST THIS CLASS EXISTS FOR. The wire carries bytes, and CJK text is three UTF-8 bytes
        // per character - so a character-based cap would admit a 768 KB payload, three times the
        // intended limit, and ONLY for users writing in Chinese, Japanese or Korean. A limit that
        // is really three different limits depending on the reader's language is the kind of bug
        // reported as "it works on my machine".
        //
        // This string is well under the limit by character count and well over it by byte count.
        var cjk = new string('漢', (ClipboardValidation.MaxPayloadBytes / 3) + 1);

        Assert.True(cjk.Length < ClipboardValidation.MaxPayloadBytes, "fixture should be short by chars");
        Assert.True(Encoding.UTF8.GetByteCount(cjk) > ClipboardValidation.MaxPayloadBytes,
            "fixture should be long by bytes");

        Assert.Equal(ClipboardRejectReason.TooLarge, ClipboardValidation.Validate(cjk, out var bytes));
        Assert.True(bytes > ClipboardValidation.MaxPayloadBytes);
    }

    [Fact]
    public void AnEmojiIsCountedByItsRealSizeToo()
    {
        // Surrogate pairs are four UTF-8 bytes for two chars, so the same reasoning applies in the
        // opposite direction from CJK.
        Assert.Equal(ClipboardRejectReason.None, ClipboardValidation.Validate("🔥", out var bytes));
        Assert.Equal(4, bytes);
    }

    [Fact]
    public void TheReportedSizeIsUsableForTellingTheUserHowFarOverTheyAre()
    {
        // The caller needs a number to build a message with, and this is deliberately the ONLY
        // thing besides the reason that comes back - so there is nothing sensitive available to
        // interpolate into a string by accident.
        var overLimit = new string('a', ClipboardValidation.MaxPayloadBytes + 500);

        ClipboardValidation.Validate(overLimit, out var bytes);

        Assert.Equal(ClipboardValidation.MaxPayloadBytes + 500, bytes);
    }

    [Fact]
    public void ByteCountIsZeroWhenNothingWasMeasured()
    {
        // A caller that formats the count regardless must not render a stale or uninitialised
        // number next to "there is nothing to send".
        ClipboardValidation.Validate(null, out var bytes);

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void IsAcceptableAgreesWithValidate()
    {
        // Two entry points, one rule - so a caller that checks before sending and a receiver that
        // validates on arrival cannot disagree about a payload.
        Assert.True(ClipboardValidation.IsAcceptable("hello"));
        Assert.False(ClipboardValidation.IsAcceptable(""));
        Assert.False(ClipboardValidation.IsAcceptable(new string('a', ClipboardValidation.MaxPayloadBytes + 1)));
    }
}
