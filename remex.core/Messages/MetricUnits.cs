namespace Remex.Core.Messages;

/// <summary>
/// The single canonical source of truth for the unit a given <see cref="MetricKind"/> may display
/// (Dashboard 2.0). Shared across the host and every C# client so they can never disagree.
/// A card renders <c>Canonical(Kind)</c> — never a raw host unit string — so a mislabeled, blank,
/// or bogus "ms" unit can never reach a load or temperature card. Pure switch expression:
/// zero reflection, NativeAOT-trivial.
/// </summary>
public static class MetricUnits
{
    /// <summary>The ONLY unit a card of this kind may display. Empty string = value-only (no unit).</summary>
    public static string Canonical(MetricKind kind) => kind switch
    {
        MetricKind.CpuLoad or MetricKind.GpuLoad or MetricKind.RamLoad => "%",
        MetricKind.RamUsedGb or MetricKind.RamTotalGb                  => "GB",
        MetricKind.CpuTempC or MetricKind.GpuTempC or MetricKind.TempC => "°C",
        MetricKind.ClockMhz                                            => "MHz",
        MetricKind.PowerW                                              => "W",
        MetricKind.FanRpm                                              => "RPM",
        MetricKind.NetThroughputMbps
            or MetricKind.NetDownMbps
            or MetricKind.NetUpMbps                                    => "Mbps",
        MetricKind.VoltageV                                            => "V",
        MetricKind.DiskRateMBs                                         => "MB/s",
        _                                                             => "",   // Unknown → no unit; never garbage
    };
}
