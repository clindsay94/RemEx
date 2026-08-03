using Remex.Core.Models;

namespace Remex.Core.Services.FileTransfer;

/// <summary>
/// Per-paired-device consent/trust store for the file-sharing feature (plan §2). Owned by the PC host
/// (<c>FileTrustService</c>, WP2), persisted to <c>file_transfer_trust.json</c> in ProgramData beside
/// <c>paired_clients.json</c>. Grants are auto-revoked when a device is unpaired.
///
/// <para>
/// The serving device holds the response and the prompt is ROUTED (RemEx-220r): a request not already
/// covered by a remembered grant is put to the requesting client when it can render one, to this
/// device's UI via <see cref="ConsentRequested"/> when it cannot, or refused immediately when that
/// client has gone. A local decision calls <see cref="ResolveConsent"/> and a remote one
/// <see cref="TryResolveRemoteConsent"/>; <see cref="RequestConsentAsync"/> completes with whichever
/// arrives first, or a clean deny on timeout. This mirrors the established <c>IPairingService</c>
/// event pattern.
/// </para>
///
/// <para>This interface lives in <c>Remex.Core</c>; the host implementation is NOT NativeAOT-constrained.</para>
/// </summary>
public interface IFileTrustService
{
    /// <summary>Returns the stored trust record for a paired client, or null when none exists.</summary>
    Task<FileTrustRecord?> GetTrustAsync(string clientId, CancellationToken ct);

    /// <summary>All stored trust records (for the settings / trust-management UI).</summary>
    Task<IReadOnlyList<FileTrustRecord>> GetAllAsync(CancellationToken ct);

    /// <summary>True when <paramref name="clientId"/> currently holds a full-device-browse grant.</summary>
    Task<bool> IsFullBrowseGrantedAsync(string clientId, CancellationToken ct);

    /// <summary>True when incoming pushes from <paramref name="clientId"/> should be auto-accepted.</summary>
    Task<bool> IsAutoAcceptIncomingAsync(string clientId, CancellationToken ct);

    /// <summary>Persists (or clears) the full-browse grant for a client.</summary>
    Task SetFullBrowseGrantedAsync(string clientId, bool granted, CancellationToken ct);

    /// <summary>Persists (or clears) the auto-accept-incoming preference for a client.</summary>
    Task SetAutoAcceptIncomingAsync(string clientId, bool autoAccept, CancellationToken ct);

    /// <summary>Removes all trust state for a client. Called when the device is unpaired.</summary>
    Task RevokeAsync(string clientId, CancellationToken ct);

    /// <summary>
    /// Raises a consent prompt and awaits the user's decision. If a remembered grant already covers the
    /// request, completes immediately as granted. On a 60-second timeout with no user response, completes
    /// as a clean deny (see plan §2). When the user chooses "remember", the grant is persisted before the
    /// returned decision resolves.
    /// </summary>
    /// <remarks>
    /// **MAY RETURN A DENY WITHOUT ASKING ANYONE.** Since RemEx-220r the question is routed: to the
    /// requesting client if it can render a prompt, to this device's UI if it cannot, and to an
    /// immediate refusal if that client no longer has a live session — because a prompt about a
    /// request whose asker has gone is one nobody can answer, and an "allow" would grant durable
    /// trust to a device that is not there. So a caller can no longer assume that calling this raises
    /// anything, or that a deny took the full timeout.
    /// </remarks>
    Task<FileConsentDecision> RequestConsentAsync(string clientId, FileConsentRequest request, CancellationToken ct);

    /// <summary>
    /// Supplies the user's decision for a pending consent prompt identified by
    /// <see cref="FileConsentRequest.ConsentId"/>. No-op if the id is unknown or already resolved.
    /// </summary>
    void ResolveConsent(string consentId, bool granted, bool remember);

    /// <summary>
    /// Applies a consent decision that arrived FROM a client, over the wire.
    /// </summary>
    /// <returns>True when it was applied; false when there is no such pending prompt, or it belongs to a different client.</returns>
    /// <remarks>
    /// Separate from <see cref="ResolveConsent"/> because the two have different trust. A decision typed
    /// on the serving device is the operator's to make; a decision arriving over the wire is only ever
    /// the answer to the prompt that particular client was sent, so it is matched against the pending
    /// prompt's owner as well as its id (RemEx-220r).
    /// </remarks>
    bool TryResolveRemoteConsent(string? clientId, string? consentId, bool granted, bool remember);

    /// <summary>
    /// Raised on the serving device when a consent prompt must be shown. The UI subscribes and, when the
    /// user responds, calls <see cref="ResolveConsent"/>.
    /// </summary>
    event Action<FileConsentPrompt>? ConsentRequested;
}

/// <summary>
/// Persisted per-client trust record. Schema of <c>file_transfer_trust.json</c> is keyed by client id;
/// this is one entry, with <see cref="ClientId"/> carried for enumeration.
/// </summary>
public sealed record FileTrustRecord
{
    public required string ClientId { get; init; }
    public bool FullBrowseGranted { get; init; }
    public bool AutoAcceptIncoming { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>Outcome of a consent prompt returned by <see cref="IFileTrustService.RequestConsentAsync"/>.</summary>
public readonly record struct FileConsentDecision(bool Granted, bool Remember);

/// <summary>
/// Event payload raised to the serving device's consent UI. Carries the originating client, the request
/// itself, and the absolute time the prompt auto-denies so the UI can show/enforce a countdown.
/// </summary>
public sealed record FileConsentPrompt
{
    public required string ClientId { get; init; }
    public required FileConsentRequest Request { get; init; }
    public required long ExpiresAtUnixMs { get; init; }
}
