using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Represents a near-realtime snapshot of a running system process.
/// </summary>
public record ProcessInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("cpuUsage")]
    public double CpuUsage { get; init; }

    [JsonPropertyName("memoryUsage")]
    public long MemoryUsage { get; init; } // In bytes

    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("installDate")]
    public DateTime? InstallDate { get; init; }

    /// <summary>
    /// When this process started, as Unix milliseconds UTC. Null when the host could not read it.
    /// </summary>
    /// <remarks>
    /// Carried so a client can prove, at kill time, that the PID still holds the same INSTANCE it
    /// listed — not merely a process with the same name. Name alone cannot distinguish a relaunch:
    /// the dialog says "chrome (PID 1234)", the user confirms, the original exits, a new chrome is
    /// handed 1234, and the name check passes while the wrong window dies. (RemEx-on4n.)
    /// <para>
    /// A <c>long</c> rather than a <see cref="DateTime"/>, unlike <see cref="InstallDate"/> above,
    /// and deliberately so: the Android client parses this JSON by hand, so a <c>DateTime</c> would
    /// arrive as an ISO 8601 string it must parse, with precision that differs by platform. A Unix
    /// millisecond count is a plain JSON number on both sides and survives the round trip through
    /// the kill command's string dictionary exactly.
    /// </para>
    /// <para>
    /// Nullable because reading it genuinely fails for protected and system processes even from an
    /// elevated host. A null must never drop the row or block a kill — it means "unknown", and the
    /// guard treats unknown as unchecked.
    /// </para>
    /// </remarks>
    [JsonPropertyName("startTimeUnixMs")]
    public long? StartTimeUnixMs { get; init; }
}
