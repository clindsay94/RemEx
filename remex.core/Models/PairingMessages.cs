using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record PairingRequest
{
    [JsonPropertyName("clientPublicKey")] public required string ClientPublicKeyBase64 { get; init; }
    [JsonPropertyName("clientName")] public required string ClientName { get; init; }
    [JsonPropertyName("clientVersion")] public required string ClientVersion { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
}

public sealed record PairingResponse
{
    [JsonPropertyName("hostPublicKey")] public required string HostPublicKeyBase64 { get; init; }
    [JsonPropertyName("hostId")] public required string HostId { get; init; }
    [JsonPropertyName("hostName")] public required string HostName { get; init; }
    [JsonPropertyName("certificateSpkiHash")] public required string CertificateSpkiHashBase64 { get; init; }
    [JsonPropertyName("pinHmac")] public required string PinHmacBase64 { get; init; }

    /// <summary>
    /// True when this host can relay the active pairing PIN over the pairing <c>/ws</c> socket via
    /// <c>pairing_pin_request</c>/<c>pairing_pin_response</c>. New optional handshake field; it does
    /// not bump <c>protocolVersion</c> — an absent/false value means an older host, and the client
    /// simply falls back to manual PIN entry (or, for legacy apps, the retained HTTP auto-fetch).
    /// </summary>
    [JsonPropertyName("supportsPinAutoFetch")] public bool SupportsPinAutoFetch { get; init; }
}

public sealed record PairingComplete
{
    [JsonPropertyName("clientPinHmac")] public required string ClientPinHmacBase64 { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
}

/// <summary>
/// Host → client reconnect challenge (proof-of-possession). Sent when an already-paired client
/// reconnects. The client must answer with a <see cref="ReconnectProof"/> computed as
/// HMAC-SHA256(reconnectSecret, nonce). New optional handshake message in RemEx 2.0; it does not
/// bump <c>protocolVersion</c> — clients that do not understand it simply never authenticate via
/// the registry path and must re-pair.
/// </summary>
public sealed record ReconnectChallenge
{
    /// <summary>Base64-encoded random nonce the client must sign with its reconnect secret.</summary>
    [JsonPropertyName("nonce")] public required string NonceBase64 { get; init; }
}

/// <summary>
/// Client → host reconnect proof. Carries HMAC-SHA256(reconnectSecret, nonce) over the nonce from
/// the matching <see cref="ReconnectChallenge"/>, proving possession of the secret established at
/// pairing time without ever transmitting the secret itself.
/// </summary>
public sealed record ReconnectProof
{
    /// <summary>Base64-encoded HMAC-SHA256(reconnectSecret, nonce).</summary>
    [JsonPropertyName("proofHmac")] public required string ProofHmacBase64 { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
}
