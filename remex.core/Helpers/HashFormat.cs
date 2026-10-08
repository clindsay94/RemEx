using System.Security.Cryptography;

namespace Remex.Core.Helpers;

/// <summary>
/// How a SHA-256 is shown to a person and how a pasted one is read back (2026-10-08 redesign).
/// </summary>
/// <remarks>
/// <para>
/// THE WIRE STAYS BASE64; ONLY THE SCREEN CHANGES. Every <c>file_hash_response</c> and transfer verdict
/// already carries Base64. People compare hashes against <c>sha256sum</c>, <c>Get-FileHash</c> and download
/// pages, which all print hex, so that is what the screen shows: 64 lowercase hex characters.
/// </para>
/// <para>
/// A PASTED HASH IS ACCEPTED IN ANY FORM SOMEONE IS LIKELY TO HAVE. Hex in either case (Get-FileHash prints
/// upper case), with spaces, colons or dashes between groups, or the Base64 RemEx itself used to show.
/// Anything else is "not a SHA-256", never a silent mismatch.
/// </para>
/// <para>
/// The Kotlin twin is <c>com.clindsay94.remex.service.HashFormat</c>; both run the vectors in
/// <c>HashFormatTests</c> / <c>HashFormatTest</c>, so a hash reads the same on the PC and the phone.
/// </para>
/// </remarks>
public static class HashFormat
{
    /// <summary>Bytes in a SHA-256 digest.</summary>
    public const int Sha256Bytes = 32;

    /// <summary>
    /// The lowercase hex form of a Base64 SHA-256, or null when <paramref name="base64"/> is not one.
    /// </summary>
    public static string? ToHex(string? base64) =>
        TryDecodeBase64(base64, out var bytes) ? Convert.ToHexStringLower(bytes) : null;

    /// <summary>
    /// Reads a hash someone typed or pasted. True with the 32 digest bytes when it is 64 hex digits (any case,
    /// separators ignored) or a 44-character Base64 SHA-256; otherwise false.
    /// </summary>
    public static bool TryNormalize(string? input, out byte[] digest)
    {
        digest = [];
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        Span<char> hex = stackalloc char[Sha256Bytes * 2];
        var count = 0;
        var allHex = true;
        foreach (var c in trimmed)
        {
            if (c is ' ' or ':' or '-' or '\t')
                continue;
            if (!char.IsAsciiHexDigit(c))
            {
                allHex = false;
                break;
            }
            if (count == hex.Length)
            {
                allHex = false;
                break;
            }
            hex[count++] = c;
        }

        if (allHex && count == hex.Length)
        {
            digest = Convert.FromHexString(hex);
            return true;
        }

        return TryDecodeBase64(trimmed, out digest);
    }

    /// <summary>
    /// True when two hashes, each in any form <see cref="TryNormalize"/> accepts, name the same digest.
    /// False when they differ OR when either is not a SHA-256: call <see cref="TryNormalize"/> first to tell
    /// those apart on screen.
    /// </summary>
    public static bool Matches(string? a, string? b) =>
        TryNormalize(a, out var left)
        && TryNormalize(b, out var right)
        && CryptographicOperations.FixedTimeEquals(left, right);

    private static bool TryDecodeBase64(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64) || base64.Length != 44)
            return false;

        Span<byte> buffer = stackalloc byte[Sha256Bytes + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written != Sha256Bytes)
            return false;

        bytes = buffer[..Sha256Bytes].ToArray();
        return true;
    }
}
