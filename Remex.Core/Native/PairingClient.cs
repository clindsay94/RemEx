using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using NSec.Cryptography;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Native;

public class PairingClient
{
    private readonly ClientWebSocket _webSocket;
    private readonly Action<string>? _log;
    private Key? _clientPrivateKey;
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
        // Generate ephemeral X25519 keypair for client
        var keyAgreementAlgorithm = KeyAgreementAlgorithm.X25519;
        _clientPrivateKey = Key.Create(keyAgreementAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var clientPublicKeyBytes = _clientPrivateKey.Export(KeyBlobFormat.RawPublicKey);
        var clientPublicKeyBase64 = Convert.ToBase64String(clientPublicKeyBytes);

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
        _log?.Invoke("Sent PairingRequest with client X25519 public key to host.");

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
        if (_clientPrivateKey == null)
        {
            _log?.Invoke("No client keypair - must call StartPairingAsync first.");
            return false;
        }

        try
        {
            // Perform X25519 key agreement with host's public key
            var hostPublicKeyBytes = Convert.FromBase64String(pairingResponse.HostPublicKeyBase64);
            var hostPublicKey = PublicKey.Import(KeyAgreementAlgorithm.X25519, hostPublicKeyBytes, KeyBlobFormat.RawPublicKey);

            var keyAgreementAlgorithm = KeyAgreementAlgorithm.X25519;
            using var sharedSecret = keyAgreementAlgorithm.Agree(_clientPrivateKey, hostPublicKey);
            if (sharedSecret == null)
            {
                _log?.Invoke("Key agreement failed - shared secret is null.");
                return false;
            }

            // Derive session key via HKDF-SHA256(sharedSecret, salt=certSpkiHash, info="remex-pair-v1")
            var certSpkiHash = Convert.FromBase64String(pairingResponse.CertificateSpkiHashBase64);
            var kdf = KeyDerivationAlgorithm.HkdfSha256;
            var info = Encoding.UTF8.GetBytes("remex-pair-v1");

            _sessionKey = kdf.DeriveBytes(sharedSecret, certSpkiHash, info, 32);

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
            _clientPrivateKey?.Dispose();
            _clientPrivateKey = null;
            if (_sessionKey != null)
            {
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
                _sessionKey = null;
            }
        }
    }

    private async Task<RemexMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        return await MessageSerializer.ReceiveAsync(_webSocket, ct);
    }
}
