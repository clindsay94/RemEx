using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Native;

public class PairingClient
{
    private readonly ClientWebSocket _webSocket;
    private readonly ILogger<PairingClient> _logger;

    public PairingClient(ClientWebSocket webSocket, ILogger<PairingClient>? logger = null)
    {
        _webSocket = webSocket;
        _logger = logger ?? NullLogger<PairingClient>.Instance;
    }

    /// <summary>
    /// Starts the pairing handshake with the host and returns the PairingResponse.
    /// </summary>
    public async Task<PairingResponse?> StartPairingAsync(string clientName, string clientVersion, CancellationToken ct)
    {
        // 1. Send PairingRequest
        var req = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ProtocolVersion = 2,
            PairingRequest = new PairingRequest
            {
                ClientName = clientName,
                ClientVersion = clientVersion,
                ClientPublicKeyBase64 = "" // ECDH placeholder for future
            }
        };

        await MessageSerializer.SendAsync(_webSocket, req);
        _logger.LogInformation("Sent PairingRequest to host.");

        // 2. Wait for PairingResponse
        var response = await ReceiveMessageAsync(ct);
        if (response?.Type != MessageTypes.PairingResponse || response.PairingResponse is null)
        {
            _logger.LogWarning("Expected PairingResponse, got {Type}", response?.Type);
            return null;
        }

        return response.PairingResponse;
    }

    /// <summary>
    /// Completes the pairing by verifying the PIN and sending PairingComplete.
    /// </summary>
    public async Task<bool> CompletePairingAsync(string pin, string hostHmacBase64, string? correlationId, CancellationToken ct)
    {
        // 4. Verify PIN HMAC
        var pinBytes = Encoding.UTF8.GetBytes(pin);
        var expectedHmac = HMACSHA256.HashData(pinBytes, Encoding.UTF8.GetBytes("remex-pairing-v2"));
        var expectedBase64 = Convert.ToBase64String(expectedHmac);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedBase64),
                Encoding.UTF8.GetBytes(hostHmacBase64)))
        {
            _logger.LogWarning("User PIN verification failed.");
            return false;
        }

        // 5. Send PairingComplete
        var comp = new RemexMessage
        {
            Type = MessageTypes.PairingComplete,
            CorrelationId = correlationId,
            PairingComplete = new PairingComplete
            {
                ClientPinHmacBase64 = expectedBase64
            }
        };
        await MessageSerializer.SendAsync(_webSocket, comp);
        _logger.LogInformation("Sent PairingComplete. Waiting for host confirmation...");

        // 6. Wait for Host confirmation
        var confirm = await ReceiveMessageAsync(ct);
        if (confirm != null && confirm.Type == MessageTypes.PairingComplete && confirm.CommandSuccess == true)
        {
            _logger.LogInformation("Pairing successful.");
            return true;
        }

        _logger.LogWarning("Host rejected PairingComplete. Msg: {Error}", confirm?.ErrorText ?? confirm?.CommandMessage);
        return false;
    }

    private async Task<RemexMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        var msg = await MessageSerializer.ReceiveAsync(_webSocket, ct);
        return msg;
    }
}
