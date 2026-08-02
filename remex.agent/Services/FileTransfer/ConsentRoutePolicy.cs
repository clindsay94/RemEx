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
    /// <remarks>
    /// **AN OLDER CLIENT FALLS BACK TO THE PC DIALOG RATHER THAN BEING DENIED**, and that is the
    /// backward-compatibility requirement, not a nicety. A phone built before this feature cannot
    /// render the prompt, so routing its request to a surface it does not have would make every
    /// transfer fail on an app the user has no reason to suspect — a silent break of a working
    /// setup, caused entirely by updating the PC. The PC dialog is worse UX and it is reachable,
    /// which beats better UX that nobody can answer.
    /// </remarks>
    public static ConsentRoute Route(bool requestingClientConnected, bool clientSupportsPhonePrompt)
    {
        // NOBODY TO ASK MEANS DENY, and it must be a deny rather than a desktop prompt. The asker
        // is gone, so a PC dialog would sit there asking a question about a transfer that can no
        // longer happen - and if a user answered "allow", they would be granting durable trust to a
        // device that is not there, on the strength of a request they cannot see the origin of.
        if (!requestingClientConnected) return ConsentRoute.Deny;

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
