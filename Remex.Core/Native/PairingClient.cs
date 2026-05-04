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
    private ECDiffieHellman? _clientEcdh;
    private byte[]? _sessionKey;

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
        // Generate ephemeral P-256 ECDH keypair for client
        _clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var pubDer = _clientEcdh.PublicKey.ExportSubjectPublicKeyInfo();
        var clientPublicKeyBase64 = Convert.ToBase64String(pubDer);

        var req = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ProtocolVersion = 2,
            PairingRequest = new PairingRequest
            {
                ClientName = clientName,
                ClientVersion = clientVersion,
                ClientPublicKeyBase64 = clientPublicKeyBase64
            }
        };

        await MessageSerializer.SendAsync(_webSocket, req);
        _log?.Invoke("Sent PairingRequest with client ECDH P-256 public key to host.");

        var response = await ReceiveMessageAsync(ct);
        if (response?.Type != MessageTypes.PairingResponse || response.PairingResponse is null)
        {
            _log?.Invoke($"Expected PairingResponse, got {response?.Type}");
            return null;
        }

        return response.PairingResponse;
    }

    public async Task<bool> CompletePairingAsync(string pin, PairingResponse pairingResponse, CancellationToken ct)
    {
        if (_clientEcdh == null)
        {
            _log?.Invoke("No client keypair - must call StartPairingAsync first.");
            return false;
        }

        try
        {
            // Agree + KDF on receive
            using var hostPeer = ECDiffieHellman.Create();
            hostPeer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pairingResponse.HostPublicKeyBase64), out _);
            
            byte[] ss = _clientEcdh.DeriveRawSecretAgreement(hostPeer.PublicKey);

            // Derive session key via HKDF-SHA256(sharedSecret, salt=certSpkiHash, info="remex-pair-v1")
            var certSpkiHash = Convert.FromBase64String(pairingResponse.CertificateSpkiHashBase64);
            var info = Encoding.UTF8.GetBytes("remex-pair-v1");

            _sessionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ss, outputLength: 32, salt: certSpkiHash, info: info);
            CryptographicOperations.ZeroMemory(ss);

            // Verify host's PIN HMAC
            var expectedHostHmac = HMACSHA256.HashData(_sessionKey, Encoding.UTF8.GetBytes(pin));
            var expectedHostHmacBase64 = Convert.ToBase64String(expectedHostHmac);

            var receivedHostHmac = Convert.FromBase64String(pairingResponse.PinHmacBase64);
            if (!CryptographicOperations.FixedTimeEquals(expectedHostHmac, receivedHostHmac))
            {
                _log?.Invoke("PIN verification failed - computed HMAC does not match host's HMAC.");
                return false;
            }

            _log?.Invoke("PIN verified successfully.");

            // Compute client acknowledgment HMAC
            var ackMessage = "ack:" + pin;
            var clientAckHmac = HMACSHA256.HashData(_sessionKey, Encoding.UTF8.GetBytes(ackMessage));
            var clientAckHmacBase64 = Convert.ToBase64String(clientAckHmac);

            var comp = new RemexMessage
            {
                Type = MessageTypes.PairingComplete,
                ProtocolVersion = 2,
                PairingComplete = new PairingComplete
                {
                    ClientPinHmacBase64 = clientAckHmacBase64
                }
            };
            await MessageSerializer.SendAsync(_webSocket, comp);
            _log?.Invoke("Sent PairingComplete with client ack HMAC. Waiting for host confirmation...");

            var confirm = await ReceiveMessageAsync(ct);
            if (confirm != null && confirm.Type == MessageTypes.PairingComplete && confirm.CommandSuccess == true)
            {
                _log?.Invoke("Pairing successful.");
                return true;
            }

            _log?.Invoke($"Host rejected PairingComplete: {confirm?.ErrorText ?? confirm?.CommandMessage}");
            return false;
        }
        finally
        {
            // Clean up ephemeral key material
            _clientEcdh?.Dispose();
            _clientEcdh = null;
            if (_sessionKey != null)
            {
                CryptographicOperations.ZeroMemory(_sessionKey);
                _sessionKey = null;
            }
        }
    }

    private async Task<RemexMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        return await MessageSerializer.ReceiveAsync(_webSocket, ct);
    }
}
