using System.Text.Json.Serialization;

namespace Remex.Core.Messages;

/// <summary>
/// Lightweight JSON envelope for all Remex IPC messages.
/// </summary>
public sealed record RemexMessage
{
    /// <summary>
    /// Message type discriminator (e.g. "ping", "pong").
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// UTC ticks at the time the message was created, used for latency measurement.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }
}

/// <summary>
/// Well-known message type constants.
/// </summary>
public static class MessageTypes
{
    public const string Ping = "ping";
    public const string Pong = "pong";
}
