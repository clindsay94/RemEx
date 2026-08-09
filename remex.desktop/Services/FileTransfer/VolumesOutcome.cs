using Remex.Core.Models;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>What a <c>file_volumes_response</c> actually said (RemEx-jc4q).</summary>
public enum VolumesOutcome
{
    /// <summary>Full-device browse is granted; the volumes are usable.</summary>
    Granted,

    /// <summary>The peer's host refused without asking anybody, because the asker was not reachable.</summary>
    PeerUnreachable,

    /// <summary>Somebody was asked and said no.</summary>
    Refused,

    /// <summary>The request itself failed — malformed, or the host threw. Not a refusal.</summary>
    Failed,
}

/// <summary>
/// Classifies a <c>file_volumes_response</c> so the desktop can say something followable
/// (RemEx-jc4q).
/// </summary>
/// <remarks>
/// <para>
/// **THE DESKTOP IS A CLIENT AS WELL AS A HOST, AND IT HAD THE IDENTICAL SILENCE.**
/// <c>ListVolumesAsync</c> parsed volumes and <c>fullBrowseGranted</c> and dropped
/// <c>denyReason</c> on the floor, so a PC asking a peer to browse everything and being refused
/// without anybody being asked got "Full-device access was not granted." — the same flat no the
/// phone showed before RemEx-3qmd, with nothing to act on.
/// </para>
/// <para>
/// PORTED AS A SHAPE, NOT AS SHARED CODE. The Kotlin classifier is the reference and there is
/// nothing to reuse from it: different language, different resource system, different screen. What
/// transfers is the ORDER, and it is not arbitrary. <c>errorMessage</c> wins because the host sets
/// it when the request never got as far as asking anybody, and calling that a refusal would blame
/// the peer for a fault. <c>fullBrowseGranted</c> is next because a grant already held never denies
/// anything and carries no reason. Only then does the code matter — and an ABSENT reason genuinely
/// means a person decided, which is the contract RemEx-l580 established. Reading a missing reason as
/// "unreachable" would tell somebody to reconnect a device that is working fine.
/// </para>
/// </remarks>
public static class VolumesResponseClassifier
{
    /// <summary>Applies the precedence above.</summary>
    public static VolumesOutcome Classify(bool fullBrowseGranted, string? denyReason, string? errorMessage)
    {
        if (!string.IsNullOrWhiteSpace(errorMessage)) return VolumesOutcome.Failed;
        if (fullBrowseGranted) return VolumesOutcome.Granted;

        return Normalize(denyReason) switch
        {
            FileConsentDenyReasons.ClientUnreachable => VolumesOutcome.PeerUnreachable,
            _ => VolumesOutcome.Refused,
        };
    }

    /// <summary>
    /// Normalizes a deny reason off the wire.
    /// </summary>
    /// <remarks>
    /// Blank and whitespace-only become null, so a host that spells "no reason" as <c>""</c> rather
    /// than by omitting the field cannot be mistaken for one that sent a code. Case is NOT folded —
    /// these are fixed protocol tokens, not prose.
    /// </remarks>
    private static string? Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
