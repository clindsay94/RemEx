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
}

public sealed record PairingComplete
{
    [JsonPropertyName("clientPinHmac")] public required string ClientPinHmacBase64 { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
}
