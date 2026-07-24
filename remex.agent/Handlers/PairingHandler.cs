using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Services.Security;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Handlers;

/// <summary>
/// Handles WebSocket pairing messages for the 2.0 security protocol.
/// Manages the PairingRequest → PairingResponse → PairingComplete handshake.
/// </summary>
public sealed class PairingHandler
{
    private readonly ILogger<PairingHandler> _logger;
    private readonly PairingService _pairingService;
    private readonly ICertificateService _certificateService;
    private readonly PairedClientRegistry _pairedClientRegistry;

    public PairingHandler(
        ILogger<PairingHandler> logger,
        PairingService pairingService,
        ICertificateService certificateService,
        PairedClientRegistry pairedClientRegistry)
    {
        _logger = logger;
        _pairingService = pairingService;
        _certificateService = certificateService;
        _pairedClientRegistry = pairedClientRegistry;
    }

    /// <summary>
    /// Handles an incoming pairing_request message.
    /// Starts a pairing session and responds with the host's public key and HMAC.
    /// </summary>
    public async Task<RemexMessage?> HandlePairingRequestAsync(RemexMessage message, CancellationToken ct)
    {
        if (message.PairingRequest is null)
        {
            _logger.LogWarning("Received pairing_request with null payload.");
            return MakeError("Missing pairing request payload.");
        }

        var pairingSessionStarted = false;
        try
        {
            _logger.LogInformation("Pairing request received from client: {Name} v{Version} (ID: {ClientId})",
                message.PairingRequest.ClientName,
                message.PairingRequest.ClientVersion,
                Remex.Agent.Services.Security.LogRedaction.RedactClientId(message.PairingRequest.ClientId));

            // Reuse any desktop-generated active PIN session so QR/manual pairing and the
            // subsequent pairing_request are talking about the same host-side cryptographic
            // state. Only brand-new sessions are cancelled on failure recovery below.
            var acquisition = await _pairingService.AcquirePairingSessionAsync(ct);
            var state = acquisition.State;
            pairingSessionStarted = acquisition.StartedNewSession;

            // Derive session key from client's public key. If this throws (e.g. malformed
            // client public key, HKDF failure) the session in PairingService is still live;
            // the catch below must call CancelPairing only when this request created the
            // session. Reused desktop-generated sessions must remain intact for the real
            // operator-visible pairing flow.
            var pinHmacBase64 = await _pairingService.DeriveSessionKeyAsync(
                message.PairingRequest.ClientPublicKeyBase64, ct);

            var response = new RemexMessage
            {
                Type = MessageTypes.PairingResponse,
                CorrelationId = message.CorrelationId,
                PairingResponse = new PairingResponse
                {
                    HostPublicKeyBase64 = state.HostPublicKeyBase64,
                    HostId = HostBootstrapper.HostId,
                    HostName = Environment.MachineName,
                    CertificateSpkiHashBase64 = _certificateService.GetSpkiSha256Base64(),
                    PinHmacBase64 = pinHmacBase64,
                    // Advertise that this host can relay the active PIN over /ws (pairing_pin_request).
                    // New apps only auto-fetch when they see this flag; older apps ignore it.
                    SupportsPinAutoFetch = true,
                },
            };

            // Never log the PIN value: the retained in-memory log buffer is a disclosure surface
            // (VULN-1, RemEx-s032.1). The PIN is shown on the host screen for the user to read.
            _logger.LogInformation("Pairing response sent. PIN is displayed on the host screen (not logged).");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle pairing request.");
            if (pairingSessionStarted)
            {
                try
                {
                    _pairingService.CancelPairing();
                }
                catch (Exception cancelEx)
                {
                    _logger.LogWarning(cancelEx, "Failed to cancel pairing during error recovery.");
                }
            }
            // Generic message to the peer (PAIR-7 / RemEx-xk9): a malformed client ECDH public key
            // makes DeriveSessionKeyAsync throw, and interpolating ex.Message would leak crypto
            // internals to an unauthenticated caller. Full detail is logged host-side above.
            return MakeError("Pairing request failed: invalid pairing key or request.");
        }
    }

    /// <summary>
    /// Handles an incoming pairing_complete message.
    /// Verifies the client's HMAC against the expected value.
    /// </summary>
    public async Task<RemexMessage?> HandlePairingCompleteAsync(RemexMessage message, CancellationToken ct)
    {
        if (message.PairingComplete is null)
        {
            _logger.LogWarning("Received pairing_complete with null payload.");
            return MakeError("Missing pairing complete payload.");
        }

        try
        {
            var verification = await _pairingService.VerifyClientHmacAndCaptureSecretAsync(
                message.PairingComplete.ClientPinHmacBase64, ct);

            if (verification.Verified)
            {
                _logger.LogInformation("Pairing completed successfully.");

                try
                {
                    if (!string.IsNullOrWhiteSpace(message.PairingComplete.ClientId)
                        && verification.ReconnectSecret.Length > 0)
                    {
                        // Bind the client to its reconnect secret (the pairing session key) so future
                        // reconnects must prove possession of it (PAIR-1). The client derives the same
                        // key during pairing, so the secret never travels over the wire.
                        _pairedClientRegistry.RegisterClient(
                            message.PairingComplete.ClientId,
                            verification.ReconnectSecret);
                    }
                }
                finally
                {
                    // The registry copied the bytes it needed; zero our captured copy promptly.
                    CryptographicOperations.ZeroMemory(verification.ReconnectSecret);
                }

                return new RemexMessage
                {
                    Type = MessageTypes.PairingComplete,
                    CorrelationId = message.CorrelationId,
                    CommandSuccess = true,
                    CommandMessage = "Pairing verified.",
                };
            }
            else
            {
                _logger.LogWarning("Pairing verification failed.");
                return MakeError("PIN verification failed. Please try again.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle pairing complete.");
            // Same generic message as the wrong-PIN path (line above) so a crypto/parse error can't
            // be distinguished from an incorrect PIN by the peer (PAIR-7 / RemEx-xk9).
            return MakeError("PIN verification failed. Please try again.");
        }
    }

    /// <summary>
    /// Handles an incoming <c>pairing_pin_request</c>: relays the currently-active pairing PIN over
    /// the pairing <c>/ws</c> socket so the Android app can auto-fill it without a trust-all HTTP
    /// fetch (ASI compliance, RemEx-1t0b). This is now the <em>only</em> PIN auto-fetch surface — the
    /// former <c>GET /pairing-pin</c> HTTP endpoint was retired (RemEx-0xp0) — and it carries exactly
    /// the capability that endpoint did: gated by the
    /// <see cref="TransportTrust.IsTrustedForPinAutoFetch"/> decision (computed at the <c>/ws</c> map
    /// site and passed in as <paramref name="isTrustedForPinAutoFetch"/>), it only ever
    /// <em>reads</em> an already-active PIN via <see cref="PairingService.TryGetActivePinInfo"/> — it
    /// never creates, extends, or mutates a session (it does not touch
    /// <c>AcquirePairingSessionAsync</c>).
    /// </summary>
    /// <returns>
    /// A <c>pairing_pin_response</c> whose payload is the active PIN when trusted, or null when the
    /// transport is untrusted OR no session is active. The two pin-less cases are byte-identical by
    /// design (the retired HTTP endpoint returned 404 for both; this preserves that
    /// indistinguishability), so a caller on an untrusted transport cannot probe whether a pairing
    /// session exists.
    /// </returns>
    public async Task<RemexMessage> HandlePairingPinRequestAsync(
        RemexMessage message, bool isTrustedForPinAutoFetch, CancellationToken ct)
    {
        PairingPinInfo? pinInfo = null;

        if (isTrustedForPinAutoFetch)
        {
            // TryGetActivePinInfo is a non-blocking _lock.Wait(0) snapshot that can transiently miss
            // under contention (why the old HTTP path retried). By the time a client asks, the session
            // is already active (AcquirePairingSessionAsync ran during pairing_request), so only lock
            // contention — not session absence — should cause a miss. A short retry covers it.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (_pairingService.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs))
                {
                    pinInfo = new PairingPinInfo(pin, expiresAtUnixMs);
                    break;
                }

                if (attempt < 2)
                    await Task.Delay(100, ct);
            }

            if (pinInfo is null)
                _logger.LogInformation("pairing_pin_request from a trusted transport, but no active PIN was readable.");
        }
        else
        {
            _logger.LogInformation("pairing_pin_request denied: transport is not trusted for PIN auto-fetch.");
        }

        return new RemexMessage
        {
            Type = MessageTypes.PairingPinResponse,
            CorrelationId = message.CorrelationId,
            // Null for BOTH "untrusted" and "no active session" — indistinguishable by design.
            PairingPin = pinInfo,
        };
    }

    private static RemexMessage MakeError(string errorText) => new()
    {
        Type = MessageTypes.PairingError,
        ErrorText = errorText,
    };

    public void CancelActivePairing() => _pairingService.CancelPairing();
}
