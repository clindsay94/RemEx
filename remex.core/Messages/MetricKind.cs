using System.Text.Json.Serialization;

namespace Remex.Core.Messages;

/// <summary>
/// The semantic role of a telemetry sensor (Dashboard 2.0). Lets clients bind a card to a
/// metric by <em>meaning</em> rather than by a fragile host-supplied name/unit string, which
/// is what caused a timing sensor (unit "ms") to win the RAM card and render "1089.0ms".
/// </summary>
/// <remarks>
/// Wire tokens are the member names VERBATIM (the generic <see cref="JsonStringEnumConverter{TEnum}"/>
/// applies no naming policy): "Unknown", "CpuLoad", "GpuLoad", "RamLoad", "RamUsedGb", "RamTotalGb",
/// "CpuTempC", "GpuTempC", "TempC", "ClockMhz", "PowerW", "FanRpm", "NetThroughputMbps",
/// "NetDownMbps", "NetUpMbps", "VoltageV", "DiskRateMBs".
/// This is a shared wire contract: the Kotlin/Avalonia clients parse these exact tokens.
/// Append-only — never rename or renumber an existing member.
/// The generic converter (not the reflection-based non-generic form) is mandatory for the
/// NativeAOT libRemexCore.so link.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<MetricKind>))]
public enum MetricKind
{
    /// <summary>Unclassified / not stamped by the host. Never binds to a curated card.</summary>
    Unknown = 0,

    // ── Load (%) ──
    CpuLoad,
    GpuLoad,
    RamLoad,

    // ── Memory (GB) ──
    RamUsedGb,
    RamTotalGb,

    // ── Temperature (°C) ──
    CpuTempC,
    GpuTempC,
    TempC,

    // ── Clock (MHz) ──
    ClockMhz,

    // ── Power (W) ──
    PowerW,

    // ── Fan (RPM) ──
    FanRpm,

    // ── Network (Mbps) ──
    NetThroughputMbps,
    NetDownMbps,
    NetUpMbps,

    // ── Voltage (V) ──
    VoltageV,

    // ── Disk (MB/s) ──
    DiskRateMBs,
}
