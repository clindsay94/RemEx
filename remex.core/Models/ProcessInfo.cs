using System;
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
}
