using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Native;

public class PairingClient
{
    private readonly ClientWebSocket _webSocket;
    private readonly Action<string>? _log;

    // Constructor intentionally avoids any dependency on Microsoft.Extensions.Logging
    // because this type is invoked from the Android NativeAOT entrypoints
    // (AndroidNativeExports), which load before any DI container is built and
    // before Microsoft.Extensions.Logging.Abstractions can be resolved at runtime.
    public PairingClient(ClientWebSocket webSocket, Action<string>? log = null)
    {
        _webSocket = webSocket;
        _log = log;
    }

    public async Task<PairingResponse?> StartPairingAsync(string clientName, string clientVersion, CancellationToken ct)
    {
        var req = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ProtocolVersion = 2,
            PairingRequest = new PairingRequest
            {
                ClientName = clientName,
                ClientVersion = clientVersion,
                ClientPublicKeyBase64 = ""
            }
        };

        await MessageSerializer.SendAsync(_webSocket, req);
        _log?.Invoke("Sent PairingRequest to host.");

        var response = await ReceiveMessageAsync(ct);
        if (response?.Type != MessageTypes.PairingResponse || response.PairingResponse is null)
        {
            _log?.Invoke($"Expected PairingResponse, got {response?.Type}");
            return null;
        }

        return response.PairingResponse;
    }

    public async Task<bool> CompletePairingAsync(string pin, string hostHmacBase64, string? correlationId, CancellationToken ct)
    {
        var pinBytes = Encoding.UTF8.GetBytes(pin);
        var expectedHmac = HMACSHA256.HashData(pinBytes, Encoding.UTF8.GetBytes("remex-pairing-v2"));
        var expectedBase64 = Convert.ToBase64String(expectedHmac);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedBase64),
                Encoding.UTF8.GetBytes(hostHmacBase64)))
        {
            _log?.Invoke("User PIN verification failed.");
            return false;
        }

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
        _log?.Invoke("Sent PairingComplete. Waiting for host confirmation...");

        var confirm = await ReceiveMessageAsync(ct);
        if (confirm != null && confirm.Type == MessageTypes.PairingComplete && confirm.CommandSuccess == true)
        {
            _log?.Invoke("Pairing successful.");
            return true;
        }

        _log?.Invoke($"Host rejected PairingComplete: {confirm?.ErrorText ?? confirm?.CommandMessage}");
        return false;
    }

    private async Task<RemexMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        return await MessageSerializer.ReceiveAsync(_webSocket, ct);
    }
}
