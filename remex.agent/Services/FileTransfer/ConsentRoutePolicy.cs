using Remex.Core.Models;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>Where a file-consent question should be asked.</summary>
public enum ConsentRoute
{
    /// <summary>Ask on the phone that made the request.</summary>
    Phone,

    /// <summary>Ask on the PC. The compatibility path, not the preferred one.</summary>
    Desktop,

    /// <summary>Do not ask anyone — refuse.</summary>
    Deny
}

/// <summary>
/// Decides which surface is asked for file-transfer consent (RemEx-mneb).
/// </summary>
/// <remarks>
/// <para>
/// **THE CONSENT UX IS BACKWARDS TODAY.** When the phone requests full-browse or pushes a file, the
/// PC pops a dialog on the PC monitor for a decision the PHONE user is making — possibly from
/// another room, which means the prompt waits in front of nobody until it times out. The person
/// being asked should be the person who asked.
/// </para>
/// <para>
/// This is the routing half only. The decision itself, and the clean-deny timeout, already live in
/// <c>FileTrustService.RequestConsentAsync</c>.
/// </para>
/// </remarks>
public static class ConsentRoutePolicy
{
    /// <summary>
    /// Chooses the surface to prompt on.
    /// </summary>
    /// <param name="requestingClientConnected">
    /// Whether the client that asked still has a live session. A request whose asker has gone is
    /// not a question anyone can answer.
    /// </param>
    /// <param name="clientSupportsPhonePrompt">
    /// Whether that client advertises the phone-side consent capability.
    /// </param>
    /// <param name="consentKind">
    /// The <see cref="Remex.Core.Models.FileConsentKinds"/> value being asked about. Splits the
    /// route: see the remarks.
    /// </param>
    /// <remarks>
    /// <para>
    /// **THE SURFACE IS CHOSEN BY WHAT IS BEING GRANTED, NOT BY WHAT THE CLIENT CAN RENDER
    /// (RemEx-6bfyt).** Full browse is a durable grant over the WHOLE filesystem, so it is
    /// authorised at the machine whose filesystem is being handed out — the PC — even though the
    /// phone could display the prompt. Per-push consent stays on the phone.
    /// </para>
    /// <para>
    /// That split is the point, and it is not a revert of RemEx-mneb. The complaint that produced
    /// the phone route was a prompt waiting in front of nobody, and per-push is exactly the case
    /// where that happens: the phone user picks a file and the answer is wanted immediately, at the
    /// device they are holding. Full browse is the opposite — it is rare, it is durable, and the
    /// person who should weigh it is whoever is sitting at the PC. Routing both by client
    /// capability collapsed two different questions into one answer. (Connor's call, 2026-08-10;
    /// options considered were this split, prompting on both surfaces with first-answer-wins, and
    /// keeping everything on the phone.)
    /// </para>
    /// <para>
    /// **AN OLDER CLIENT FALLS BACK TO THE PC DIALOG RATHER THAN BEING DENIED**, and that is the
    /// backward-compatibility requirement, not a nicety. A phone built before this feature cannot
    /// render the prompt, so routing its request to a surface it does not have would make every
    /// transfer fail on an app the user has no reason to suspect — a silent break of a working
    /// setup, caused entirely by updating the PC. The PC dialog is worse UX and it is reachable,
    /// which beats better UX that nobody can answer. Unchanged by the split above: full browse was
    /// already going to the PC for those clients, and push still does.
    /// </para>
    /// </remarks>
    public static ConsentRoute Route(
        bool requestingClientConnected,
        bool clientSupportsPhonePrompt,
        string consentKind)
    {
        // NOBODY TO ASK MEANS DENY, and it must be a deny rather than a desktop prompt. The asker
        // is gone, so a PC dialog would sit there asking a question about a transfer that can no
        // longer happen - and if a user answered "allow", they would be granting durable trust to a
        // device that is not there, on the strength of a request they cannot see the origin of.
        if (!requestingClientConnected) return ConsentRoute.Deny;

        // BEFORE the capability check on purpose: a durable whole-filesystem grant goes to the PC
        // whether or not the phone could have rendered it.
        if (string.Equals(consentKind, FileConsentKinds.FullBrowse, StringComparison.Ordinal))
            return ConsentRoute.Desktop;

        return clientSupportsPhonePrompt ? ConsentRoute.Phone : ConsentRoute.Desktop;
    }

    /// <summary>
    /// Whether a consent response should be applied.
    /// </summary>
    /// <param name="responseConsentId">The id the client echoed back.</param>
    /// <param name="pendingConsentId">The id of the request still awaiting an answer, if any.</param>
    /// <remarks>
    /// <para>
    /// **A LATE ANSWER MUST NOT REVIVE A RESOLVED REQUEST.** If a prompt already timed out to a
    /// clean deny and the phone answers afterwards — the user picked up their phone a minute later
    /// and tapped Allow — applying it would grant a transfer the host already refused, and the two
    /// sides would disagree about what happened. `pendingConsentId` is null once resolved, so a
    /// late response is ignored by construction rather than by a race-prone timestamp check.
    /// </para>
    /// <para>
    /// Ids compare ORDINALLY. They are opaque tokens, and a case-insensitive match would let one
    /// request's answer be applied to another — which for a consent decision means granting access
    /// the user approved for something else.
    /// </para>
    /// </remarks>
    public static bool ShouldApplyResponse(string? responseConsentId, string? pendingConsentId)
    {
        if (string.IsNullOrWhiteSpace(responseConsentId)) return false;
        if (string.IsNullOrWhiteSpace(pendingConsentId)) return false;

        return string.Equals(responseConsentId, pendingConsentId, StringComparison.Ordinal);
    }
}
