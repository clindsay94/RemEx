using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Security;
using Remex.Host.Services.Security;

namespace Remex.Host.Handlers;

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

        try
        {
            _logger.LogInformation("Pairing request received from client: {Name} v{Version} (ID: {ClientId})",
                message.PairingRequest.ClientName,
                message.PairingRequest.ClientVersion,
                message.PairingRequest.ClientId ?? "Unknown");

            // Start pairing session (generates host keypair and PIN)
            var state = await _pairingService.StartPairingAsync(ct);

            // Derive session key from client's public key
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
                },
            };

            _logger.LogInformation("Pairing response sent. PIN displayed on host: {Pin}", state.Pin);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle pairing request.");
            return MakeError($"Pairing request failed: {ex.Message}");
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
            var verified = await _pairingService.VerifyClientHmacAsync(
                message.PairingComplete.ClientPinHmacBase64, ct);

            if (verified)
            {
                _logger.LogInformation("Pairing completed successfully.");
                
                if (!string.IsNullOrWhiteSpace(message.PairingComplete.ClientId))
                {
                    _pairedClientRegistry.RegisterClient(message.PairingComplete.ClientId);
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
            return MakeError($"Pairing verification failed: {ex.Message}");
        }
    }

    private static RemexMessage MakeError(string errorText) => new()
    {
        Type = MessageTypes.PairingError,
        ErrorText = errorText,
    };

    public void CancelActivePairing() => _pairingService.CancelPairing();
}
