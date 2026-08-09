using System;
using System.Text;

namespace Remex.Desktop.Services.Security;

/// <summary>
/// Renders this host's SPKI fingerprint in the exact form the Android client shows, so the two can
/// be compared by eye without converting anything (RemEx-n8xk).
/// </summary>
/// <remarks>
/// <para>
/// THE POINT IS THAT BOTH ENDS SPELL IT THE SAME WAY. RemEx-vnps gave the phone a dialog that shows
/// the pinned fingerprint next to the one answering at that address now, and asks the user to judge
/// whether the change is legitimate. Until this class there was nowhere on the PC that showed its
/// own fingerprint at all, so the honest answer to "check it against your PC" was to dig the
/// certificate out of the Windows certificate store by hand. A value the user cannot look up is not
/// a check they can perform.
/// </para>
/// <para>
/// This is a deliberate, line-for-line mirror of Android's
/// <c>com.clindsay94.remex.security.SpkiFingerprint.forDisplay</c>: same 16 characters, same
/// four-character groups, same base64 spelling, same <c>(none)</c> marker. Two formatters that
/// disagree by a single space would put the user in front of two strings that look different for a
/// certificate that never changed — which is the alarm this feature exists to let them dismiss.
/// <c>SpkiFingerprintDisplayTests</c> reads the Kotlin constants and fails if the two drift apart.
/// </para>
/// <para>
/// DISPLAY ONLY, AND THERE IS NO COMPARISON METHOD HERE ON PURPOSE. The Kotlin object also exposes
/// <c>isSameCertificate</c>, which compares the FULL value precisely because comparing truncated
/// forms would call two certificates identical whenever they share a prefix. Nothing on the PC needs
/// that comparison — the pinning decision lives in <see cref="CertificatePinPolicy"/> and works on
/// whole hashes — so offering a shortened string next to an equality helper would only invite the
/// mistake.
/// </para>
/// </remarks>
public static class SpkiFingerprintDisplay
{
    /// <summary>Characters shown, before grouping. Mirrors Kotlin <c>SpkiFingerprint.DisplayLength</c>.</summary>
    internal const int DisplayLength = 16;

    /// <summary>Characters per visual group. Mirrors Kotlin <c>SpkiFingerprint.GroupSize</c>.</summary>
    internal const int GroupSize = 4;

    /// <summary>
    /// Shown when the fingerprint is absent or unusable. Mirrors Kotlin
    /// <c>SpkiFingerprint.Unavailable</c>, and is deliberately NOT localized: it stands in for a
    /// value that is itself the same in every language, and the user may well be holding the phone
    /// next to the PC comparing the two screens.
    /// </summary>
    public const string Unavailable = "(none)";

    /// <summary>
    /// Renders a fingerprint as short, grouped text for on-screen comparison.
    /// </summary>
    /// <remarks>
    /// Grouped because an unbroken run of sixteen characters is not actually comparable: a person
    /// asked to check it against another sixteen glances at the first three and the last two and
    /// calls it a match. Four-character groups are the same reason card numbers are grouped.
    /// <para>
    /// Returns <see cref="Unavailable"/> rather than an empty string when there is nothing to show,
    /// so the row can never render blank and read as though the PC has no certificate.
    /// </para>
    /// </remarks>
    /// <param name="spkiPin">
    /// Base64 SHA-256 of the SubjectPublicKeyInfo, bare or <c>sha256/</c>-prefixed — both spellings
    /// occur in this codebase and must render identically.
    /// </param>
    public static string ForDisplay(string? spkiPin)
    {
        var normalized = Normalize(spkiPin);
        if (normalized is null)
            return Unavailable;

        var shown = normalized.Length <= DisplayLength
            ? normalized
            : normalized[..DisplayLength];

        var grouped = new StringBuilder(shown.Length + (shown.Length / GroupSize));
        for (var i = 0; i < shown.Length; i += GroupSize)
        {
            if (i > 0)
                grouped.Append(' ');
            grouped.Append(shown.AsSpan(i, Math.Min(GroupSize, shown.Length - i)));
        }

        return grouped.ToString();
    }

    /// <summary>
    /// Strips decoration without touching case, exactly as the Kotlin side does. Base64 is
    /// case-significant, so folding case here would render two different certificates alike.
    /// </summary>
    private static string? Normalize(string? spkiPin)
    {
        if (spkiPin is null)
            return null;

        var trimmed = spkiPin.Trim();
        if (trimmed.StartsWith("sha256/", StringComparison.Ordinal))
            trimmed = trimmed["sha256/".Length..].Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }
}
