using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Agent.Services.Security;

/// <summary>
/// PAIR-1 proof-of-possession handshake for the secondary binary channels (<c>/ws/desktop</c>,
/// <c>/ws/files</c>) — VULN-2 (RemEx-s032.2).
///
/// Those channels historically authenticated on bare <c>clientId</c> presence
/// (<see cref="PairedClientRegistry.IsClientPaired"/>), which is a presence check, not authentication:
/// a <c>clientId</c> is an unguessable-but-not-secret identifier that rides in query strings. This
/// helper reuses the exact reconnect-secret handshake the <c>/ws</c> control channel already enforces
/// (see <c>PingPongHandler.TryAuthenticateReconnect</c>): the host issues a fresh 32-byte nonce, the
/// client answers with <c>HMAC-SHA256(reconnectSecret, nonce)</c>, and the socket is serviced only when
/// the proof verifies against the presence-checked <c>clientId</c>'s stored secret. Both sides already
/// hold that secret from pairing, so no new secret lifecycle is introduced. Loopback callers skip this
/// entirely (they start pre-paired; see the endpoint auth evaluators).
/// </summary>
internal static class ChannelReconnectAuth
{
    // A cooperative client answers immediately after connect; a stalled/hostile one must not hold the
    // accept open. Mirrors the 10 s the native /ws client already uses for its own request/response.
    private static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Issues a challenge on <paramref name="ws"/> and awaits the client's proof. Returns <c>true</c>
    /// only when the client returns a valid <c>HMAC-SHA256(reconnectSecret, nonce)</c> for
    /// <paramref name="clientId"/>'s stored reconnect secret within <see cref="ProofTimeout"/>. Never
    /// throws for a missing/late/invalid proof — returns <c>false</c> so the caller closes the socket.
    /// </summary>
    public static async Task<bool> ChallengeAndVerifyAsync(
        WebSocket ws,
        string clientId,
        PairedClientRegistry registry,
        ILogger logger,
        CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        try
        {
            var challenge = new RemexMessage
            {
                Type = MessageTypes.ReconnectChallenge,
                ReconnectChallenge = new ReconnectChallenge
                {
                    NonceBase64 = Convert.ToBase64String(nonce),
                },
            };
            await MessageSerializer.SendAsync(ws, challenge, ct);

            using var timeoutCts = new CancellationTokenSource(ProofTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            RemexMessage? reply;
            try
            {
                reply = await MessageSerializer.ReceiveAsync(ws, linked.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Secondary-channel proof-of-possession timed out for client {ClientId}.",
                    LogRedaction.RedactClientId(clientId));
                return false;
            }

            if (reply is null || reply.Type != MessageTypes.ReconnectProof || reply.ReconnectProof is null)
            {
                logger.LogWarning(
                    "Secondary-channel proof-of-possession failed for client {ClientId}: expected a reconnect_proof frame.",
                    LogRedaction.RedactClientId(clientId));
                return false;
            }

            var ok = Verify(clientId, nonce, reply.ReconnectProof, registry, logger);
            if (!ok)
            {
                logger.LogWarning(
                    "Secondary-channel proof-of-possession did NOT verify for client {ClientId}.",
                    LogRedaction.RedactClientId(clientId));
            }

            return ok;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    /// <summary>
    /// Verifies that <paramref name="proof"/> is <c>HMAC-SHA256(reconnectSecret, <paramref name="nonce"/>)</c>
    /// for the reconnect secret bound to <paramref name="clientId"/> — the presence-checked query-string
    /// identity, NOT any identity a caller might assert inside the proof body (that binds the socket to the
    /// authenticated identity and prevents proving as one client while dialing as another). Comparison is
    /// fixed-time; the secret is zeroed. Returns <c>false</c> for an unknown/secret-less client or a
    /// malformed/mismatched proof.
    /// </summary>
    public static bool Verify(
        string clientId,
        byte[] nonce,
        ReconnectProof proof,
        PairedClientRegistry registry,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(proof.ProofHmacBase64))
        {
            return false;
        }

        if (!registry.TryGetReconnectSecret(clientId, out var secret))
        {
            // A legacy secret-less paired entry lands here: it must re-pair to obtain a reconnect
            // secret before it can use the secondary channels (same rule PAIR-1 applies on /ws).
            logger.LogWarning(
                "Secondary-channel proof references client {ClientId} with no stored reconnect secret (re-pair required).",
                LogRedaction.RedactClientId(clientId));
            return false;
        }

        try
        {
            var expected = HMACSHA256.HashData(secret, nonce);

            byte[] provided;
            try
            {
                provided = Convert.FromBase64String(proof.ProofHmacBase64);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
