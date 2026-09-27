using System.Security.Cryptography;
using System.Text;

namespace Remex.Core.Security;

/// <summary>
/// A stable, opaque key for "this particular PC", derived from its pinned SPKI (RemEx-pp0rt.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>A BYTE-FOR-BYTE PORT OF THE PHONE'S <c>security/HostIdentity.kt</c> (<c>keyFor</c>).</b> A
/// routine is bound to a PC by this key (<c>Routine.hostIdentity</c>); the phone computes it from the
/// pin it stored at pairing, and the host recomputes it from its own SPKI to reject a routine meant
/// for another PC (<c>wrong_pc</c>). If the two derivations ever disagree, every routine the phone
/// syncs is refused as belonging elsewhere, so the shared vectors in
/// <c>Fixtures/Routines/host-identity-vectors.json</c> are asserted on both sides.
/// </para>
/// <para>
/// Derivation: trim, strip one leading <c>sha256/</c>, trim again (case is NOT folded - base64 is
/// case-sensitive); SHA-256 of the UTF-8 bytes; the first 8 bytes as 16 lower-case hex characters. A
/// missing or blank pin has no identity (null), exactly as on the phone.
/// </para>
/// </remarks>
public static class HostIdentity
{
    /// <summary>Hex characters kept from the digest (64 bits).</summary>
    public const int KeyLength = 16;

    private const string PinPrefix = "sha256/";

    /// <summary>Derives the identity key for a host from its SPKI pin, or null for a blank pin.</summary>
    public static string? KeyFor(string? spkiPin)
    {
        var normalized = NormalizePin(spkiPin);
        if (normalized is null)
        {
            return null;
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalized), digest);
        return Convert.ToHexStringLower(digest[..(KeyLength / 2)]);
    }

    /// <summary>True when both pins derive and denote the same machine.</summary>
    public static bool IsSameHost(string? pinA, string? pinB)
    {
        var a = KeyFor(pinA);
        var b = KeyFor(pinB);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kotlin: <c>spkiPin?.trim()?.removePrefix("sha256/")?.trim()</c>. Kotlin's <c>trim()</c> removes
    /// <c>Char.isWhitespace</c> characters, which for every pin this code can see (base64 plus the
    /// scheme prefix) is the same set <see cref="string.Trim()"/> removes.
    /// </summary>
    private static string? NormalizePin(string? spkiPin)
    {
        if (spkiPin is null)
        {
            return null;
        }

        var trimmed = spkiPin.Trim();
        if (trimmed.StartsWith(PinPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[PinPrefix.Length..];
        }

        trimmed = trimmed.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
