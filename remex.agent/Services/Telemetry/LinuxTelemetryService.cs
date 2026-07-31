using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Agent.Services.Telemetry;

[SupportedOSPlatform("linux")]
public class LinuxTelemetryService : ITelemetryService
{
    private readonly ILogger<LinuxTelemetryService> _logger;

    private readonly string _statFile = "/proc/stat";
    private readonly string _meminfoFile = "/proc/meminfo";
    private readonly string _uptimeFile = "/proc/uptime";

    private double _lastTotalCpuTime;
    private double _lastIdleCpuTime;

    /// <summary>
    /// Maps raw hwmon chip names to user-friendly display names that match Windows sensor labelling.
    /// </summary>
    private static readonly Dictionary<string, string> ChipNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // AMD CPU temperature drivers
        ["k10temp"] = "CPU",
        ["zenpower"] = "CPU",
        // Intel CPU temperature driver
        ["coretemp"] = "CPU",
        // GPU temperature drivers
        ["amdgpu"] = "GPU",
        ["radeon"] = "GPU",
        ["nouveau"] = "GPU",
        ["nvidia"] = "GPU",
        // Motherboard / super-IO chip drivers
        ["nct6775"] = "Motherboard",
        ["nct6776"] = "Motherboard",
        ["nct6779"] = "Motherboard",
        ["nct6791"] = "Motherboard",
        ["nct6792"] = "Motherboard",
        ["nct6793"] = "Motherboard",
        ["nct6795"] = "Motherboard",
        ["nct6796"] = "Motherboard",
        ["nct6797"] = "Motherboard",
        ["nct6798"] = "Motherboard",
        ["it8688"] = "Motherboard",
        ["it8689"] = "Motherboard",
        ["it8620"] = "Motherboard",
        ["it8628"] = "Motherboard",
        ["it87"] = "Motherboard",
        ["w83627"] = "Motherboard",
        ["w83667"] = "Motherboard",
        ["f71882"] = "Motherboard",
        ["f71868"] = "Motherboard",
        // NVMe / SSD temperature
        ["nvme"] = "SSD",
        ["drivetemp"] = "Disk",
        // Wireless / network
        ["iwlwifi"] = "Wi-Fi Adapter",
        ["ath10k"] = "Wi-Fi Adapter",
        // Power supply
        ["acpi"] = "ACPI",
        // Battery
        ["BAT0"] = "Battery",
        ["BAT1"] = "Battery",
    };

    /// <summary>
    /// Maps raw sensor labels to cleaner display names.
    /// </summary>
    private static readonly Dictionary<string, string> LabelNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tctl"] = "Temperature",
        ["Tdie"] = "Die Temperature",
        ["Tccd1"] = "CCD1 Temperature",
        ["Tccd2"] = "CCD2 Temperature",
        ["Tctl/Tdie"] = "Temperature",
        ["edge"] = "Edge Temperature",
        ["junction"] = "Junction Temperature",
        ["mem"] = "Memory Temperature",
        ["Composite"] = "Temperature",
        ["Sensor 1"] = "Temperature",
        ["Sensor 2"] = "Temperature 2",
        ["SYSTIN"] = "System Temperature",
        ["CPUTIN"] = "CPU Temperature",
        ["AUXTIN0"] = "Auxiliary Temperature",
        ["AUXTIN1"] = "Auxiliary Temperature 2",
        ["AUXTIN2"] = "Auxiliary Temperature 3",
        ["AUXTIN3"] = "Auxiliary Temperature 4",
        ["PCH_CHIP_CPU_MAX_TEMP"] = "PCH Temperature",
        ["PCH_CHIP_TEMP"] = "PCH Temperature",
    };

    public LinuxTelemetryService(ILogger<LinuxTelemetryService> logger)
    {
        _logger = logger;
    }

    public async Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
    {
        var cpuResult = await GetCpuUsageAsync(ct);
        var ramResult = await GetRamUsageAsync(ct);
        var uptimeStr = await GetUptimeAsync(ct);

        var sensors = new List<SensorReading>
        {
            new() { Name = "Total CPU Usage", Value = cpuResult, Unit = "%", Category = "CPU", Source = "Linux", Kind = MetricKind.CpuLoad, Id = "linux:cpu:load" },
            new() { Name = "Physical Memory Used", Value = ramResult.used, Unit = "GB", Category = "Memory", Source = "Linux", Kind = MetricKind.RamUsedGb, Id = "linux:mem:used" },
            new() { Name = "Physical Memory Available", Value = ramResult.total - ramResult.used, Unit = "GB", Category = "Memory", Source = "Linux", Id = "linux:mem:avail" },
            new() { Name = "Physical Memory Load", Value = ramResult.total > 0 ? (ramResult.used / ramResult.total) * 100.0 : 0, Unit = "%", Category = "Memory", Source = "Linux", Kind = MetricKind.RamLoad, Id = "linux:mem:load" },
            // Total is a static machine fact (from /proc/meminfo MemTotal, the same source used for the
            // used/load readings above); stamp it so the curated RAM Total card binds instead of sitting
            // on "Collecting Data" (RemEx-km0i.3 follow-up).
            new() { Name = "Physical Memory Total", Value = ramResult.total, Unit = "GB", Category = "Memory", Source = "Linux", Kind = MetricKind.RamTotalGb, Id = "linux:mem:total" }
        };

        // Hardware monitoring via /sys/class/hwmon
        try
        {
            if (Directory.Exists("/sys/class/hwmon"))
            {
                foreach (var hwmonDir in Directory.GetDirectories("/sys/class/hwmon"))
                {
                    var rawChipName = string.Empty;
                    var namePath = Path.Combine(hwmonDir, "name");
                    if (File.Exists(namePath))
                        rawChipName = (await File.ReadAllTextAsync(namePath, ct)).Trim();

                    var friendlyChip = MapChipName(rawChipName);
                    var category = InferCategory(rawChipName);

                    // Temperatures
                    foreach (var tempInput in Directory.GetFiles(hwmonDir, "temp*_input"))
                    {
                        try
                        {
                            var inputVal = (await File.ReadAllTextAsync(tempInput, ct)).Trim();
                            if (double.TryParse(inputVal, out var milliCelsius))
                            {
                                var labelPath = tempInput.Replace("_input", "_label");
                                var rawLabel = File.Exists(labelPath)
                                    ? (await File.ReadAllTextAsync(labelPath, ct)).Trim()
                                    : Path.GetFileNameWithoutExtension(tempInput);

                                var friendlyLabel = MapLabel(rawLabel);

                                var tempC = Math.Round(milliCelsius / 1000.0, 1);

                                // Filter out obvious bogus readings (e.g. 115°C, 127°C, -66°C, -128°C) 
                                // which are common placeholder values for disconnected sensors on many Linux drivers.
                                if (tempC is > -50 and < 112)
                                {
                                    sensors.Add(new SensorReading
                                    {
                                        Name = $"{friendlyChip} {friendlyLabel}".Trim(),
                                        Value = tempC,
                                        Unit = "°C",
                                        Category = category.Length > 0 ? category : "Temperature",
                                        Source = "Linux",
                                        Kind = category == "CPU" ? MetricKind.CpuTempC
                                             : category == "GPU" ? MetricKind.GpuTempC
                                             : MetricKind.TempC,
                                        Id = $"linux:hwmon:{Path.GetFileName(hwmonDir)}:{Path.GetFileNameWithoutExtension(tempInput)}"
                                    });
                                }
                            }
                        }
                        catch { /* Skip unreadable sensor */ }
                    }

                    // Fans
                    foreach (var fanInput in Directory.GetFiles(hwmonDir, "fan*_input"))
                    {
                        try
                        {
                            var inputVal = (await File.ReadAllTextAsync(fanInput, ct)).Trim();
                            if (double.TryParse(inputVal, out var rpm) && rpm > 0)
                            {
                                var labelPath = fanInput.Replace("_input", "_label");
                                var rawLabel = File.Exists(labelPath)
                                    ? (await File.ReadAllTextAsync(labelPath, ct)).Trim()
                                    : Path.GetFileNameWithoutExtension(fanInput);

                                var friendlyLabel = MapLabel(rawLabel);
                                var idx = Path.GetFileNameWithoutExtension(fanInput).Replace("fan", "");
                                var fanName = !string.IsNullOrWhiteSpace(friendlyLabel) && friendlyLabel != rawLabel
                                    ? $"{friendlyChip} {friendlyLabel}"
                                    : $"{friendlyChip} Fan {idx}";

                                sensors.Add(new SensorReading
                                {
                                    Name = fanName.Trim(),
                                    Value = rpm,
                                    Unit = "RPM",
                                    Category = "Fan",
                                    Source = "Linux",
                                    Kind = MetricKind.FanRpm,
                                    Id = $"linux:hwmon:{Path.GetFileName(hwmonDir)}:{Path.GetFileNameWithoutExtension(fanInput)}"
                                });
                            }
                        }
                        catch { /* Skip unreadable sensor */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read hwmon sensors.");
        }

        return new TelemetryPayload
        {
            Sensors = sensors,
            UptimeText = uptimeStr,
        };
    }

    private static string MapChipName(string rawChipName)
    {
        if (string.IsNullOrWhiteSpace(rawChipName))
            return "System";

        // Direct match
        if (ChipNameMap.TryGetValue(rawChipName, out var mapped))
            return mapped;

        // Partial match for chips like "nct6798" matching "nct67" prefix entries or nvme0, nvme1 etc.
        foreach (var entry in ChipNameMap)
        {
            if (rawChipName.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        // Return cleaned version of raw name
        return rawChipName.Length > 0
            ? char.ToUpper(rawChipName[0]) + rawChipName[1..]
            : "System";
    }

    private static string MapLabel(string rawLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel))
            return "Temperature";

        if (LabelNameMap.TryGetValue(rawLabel, out var mapped))
            return mapped;

        // Clean up generic labels like "temp1" → "Sensor 1"
        if (rawLabel.StartsWith("temp", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(rawLabel.AsSpan(4), out var num))
            return $"Sensor {num}";

        // Clean up fan labels like "fan1" → "Fan 1"
        if (rawLabel.StartsWith("fan", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(rawLabel.AsSpan(3), out var fanNum))
            return $"Fan {fanNum}";

        // Return cleaned version
        var cleaned = rawLabel.Replace("_", " ");
        return cleaned.Length > 0 ? char.ToUpper(cleaned[0]) + cleaned[1..] : rawLabel;
    }

    private static string InferCategory(string rawChipName)
    {
        if (string.IsNullOrWhiteSpace(rawChipName)) return "Temperature";

        if (ChipNameMap.TryGetValue(rawChipName, out var mapped))
        {
            return mapped switch
            {
                "CPU" => "CPU",
                "GPU" => "GPU",
                "Motherboard" => "Motherboard",
                "SSD" or "Disk" => "Storage",
                "Wi-Fi Adapter" => "Network",
                "Battery" => "Battery",
                _ => "Temperature"
            };
        }

        return "Temperature";
    }

    private async Task<double> GetCpuUsageAsync(CancellationToken ct)
    {
        try
        {
            var line = (await File.ReadAllLinesAsync(_statFile, ct)).FirstOrDefault(l => l.StartsWith("cpu "));
            if (line == null) return 0;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return 0;

            var user = double.Parse(parts[1]);
            var nice = double.Parse(parts[2]);
            var system = double.Parse(parts[3]);
            var idle = double.Parse(parts[4]);

            var total = user + nice + system + idle;
            var idleDelta = idle - _lastIdleCpuTime;
            var totalDelta = total - _lastTotalCpuTime;

            var usage = totalDelta > 0 ? (1.0 - (idleDelta / totalDelta)) * 100.0 : 0;

            _lastIdleCpuTime = idle;
            _lastTotalCpuTime = total;

            return double.IsNaN(usage) || usage < 0 ? 0 : Math.Round(usage, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read CPU usage from {Path}.", _statFile);
            return 0;
        }
    }

    private async Task<(double total, double used)> GetRamUsageAsync(CancellationToken ct)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(_meminfoFile, ct);
            var memTotal = ParseMeminfoLine(lines.FirstOrDefault(l => l.StartsWith("MemTotal:")));
            var memFree = ParseMeminfoLine(lines.FirstOrDefault(l => l.StartsWith("MemFree:")));
            var buffers = ParseMeminfoLine(lines.FirstOrDefault(l => l.StartsWith("Buffers:")));
            var cached = ParseMeminfoLine(lines.FirstOrDefault(l => l.StartsWith("Cached:")));

            var totalGb = memTotal / 1024.0 / 1024.0;
            var usedGb = (memTotal - memFree - buffers - cached) / 1024.0 / 1024.0;

            return (Math.Round(totalGb, 2), Math.Round(Math.Max(0, usedGb), 2));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read RAM usage from {Path}.", _meminfoFile);
            return (0, 0);
        }
    }

    private async Task<string> GetUptimeAsync(CancellationToken ct)
    {
        try
        {
            var contents = await File.ReadAllTextAsync(_uptimeFile, ct);
            var uptimeSeconds = double.Parse(contents.Split(' ')[0]);
            var time = TimeSpan.FromSeconds(uptimeSeconds);
            return $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read uptime from {Path}.", _uptimeFile);
            return "N/A";
        }
    }

    private static double ParseMeminfoLine(string? line)
    {
        if (line == null) return 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && double.TryParse(parts[1], out var kb))
            return kb;
        return 0;
    }
}
