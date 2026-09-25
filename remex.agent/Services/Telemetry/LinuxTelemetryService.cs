using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Agent.Services.Telemetry;

[SupportedOSPlatform("linux")]
public class LinuxTelemetryService : ITelemetryService
{
    private readonly ILogger<LinuxTelemetryService> _logger;

    private readonly string _statFile;
    private readonly string _meminfoFile;
    private readonly string _uptimeFile;
    private readonly string _hwmonRoot;
    private readonly Func<long> _nowMs;

    // Perf audit P4-19: the hwmon topology (directory walk, chip name, sensor labels) was re-read on
    // every tick although it only changes on hotplug. It is scanned once and reused for this long;
    // only the *_input value files are read per tick.
    internal static readonly TimeSpan TopologyRefreshInterval = TimeSpan.FromSeconds(30);
    private HwmonSensor[]? _topology;
    private long _topologyScannedAtMs;

    /// <summary>Hwmon topology scans since construction; a test seam.</summary>
    internal int TopologyScanCount { get; private set; }

    private sealed record HwmonSensor(string InputPath, string Id, string Name, bool IsFan, string Category, MetricKind Kind);

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
        : this(logger, "/sys/class/hwmon", "/proc", null)
    {
    }

    // Test seam: a fake sysfs/procfs tree and clock.
    internal LinuxTelemetryService(ILogger<LinuxTelemetryService> logger, string hwmonRoot, string procRoot, Func<long>? nowMs)
    {
        _logger = logger;
        _hwmonRoot = hwmonRoot;
        _statFile = Path.Combine(procRoot, "stat");
        _meminfoFile = Path.Combine(procRoot, "meminfo");
        _uptimeFile = Path.Combine(procRoot, "uptime");
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
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

        // Hardware monitoring via /sys/class/hwmon. The topology comes from the P4-19 cache; the value
        // files are read here every tick.
        try
        {
            foreach (var sensor in await GetHwmonTopologyAsync(ct))
            {
                try
                {
                    var inputVal = (await File.ReadAllTextAsync(sensor.InputPath, ct)).Trim();
                    // INVARIANT, ALWAYS. sysfs and /proc emit C-locale numbers with a '.'
                    // decimal separator regardless of the user's locale, so parsing them
                    // under the ambient culture misreads every one of them on a de-DE or
                    // fr-FR box — "1234.56" becomes 123456, a silent 100× error with no
                    // exception to notice. InvariantGlobalization is set in no csproj and no
                    // Directory.Build.props, so the ambient culture really is the user's.
                    if (!double.TryParse(inputVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                        continue;

                    if (sensor.IsFan)
                    {
                        if (raw > 0)
                        {
                            sensors.Add(new SensorReading
                            {
                                Name = sensor.Name,
                                Value = raw,
                                Unit = "RPM",
                                Category = sensor.Category,
                                Source = "Linux",
                                Kind = sensor.Kind,
                                Id = sensor.Id
                            });
                        }
                        continue;
                    }

                    var tempC = Math.Round(raw / 1000.0, 1);

                    // Filter out obvious bogus readings (e.g. 115°C, 127°C, -66°C, -128°C)
                    // which are common placeholder values for disconnected sensors on many Linux drivers.
                    if (tempC is > -50 and < 112)
                    {
                        sensors.Add(new SensorReading
                        {
                            Name = sensor.Name,
                            Value = tempC,
                            Unit = "°C",
                            Category = sensor.Category,
                            Source = "Linux",
                            Kind = sensor.Kind,
                            Id = sensor.Id
                        });
                    }
                }
                catch { /* Skip unreadable sensor (vanished since the scan, or the driver refused the read) */ }
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

    private async Task<HwmonSensor[]> GetHwmonTopologyAsync(CancellationToken ct)
    {
        var now = _nowMs();
        if (_topology is { } cached && now - _topologyScannedAtMs < (long)TopologyRefreshInterval.TotalMilliseconds)
            return cached;

        // A scan that throws (e.g. a chip's name file unreadable) is not cached: the caller logs it
        // and skips hwmon for this tick, and the next tick scans again - the pre-cache behaviour.
        var (sensors, complete) = await ScanHwmonTopologyAsync(ct);
        TopologyScanCount++;
        // A sensor whose label could not be read is left out of this scan; not caching an incomplete
        // scan retries it next tick, as the per-tick read used to.
        _topology = complete ? sensors : null;
        _topologyScannedAtMs = now;
        return sensors;
    }

    private async Task<(HwmonSensor[] Sensors, bool Complete)> ScanHwmonTopologyAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_hwmonRoot))
            return ([], true);

        var result = new List<HwmonSensor>();
        bool complete = true;
        foreach (var hwmonDir in Directory.GetDirectories(_hwmonRoot))
        {
            var rawChipName = string.Empty;
            var namePath = Path.Combine(hwmonDir, "name");
            if (File.Exists(namePath))
                rawChipName = (await File.ReadAllTextAsync(namePath, ct)).Trim();

            var friendlyChip = MapChipName(rawChipName);
            var category = InferCategory(rawChipName);
            var chipId = Path.GetFileName(hwmonDir);

            // Temperatures
            foreach (var tempInput in Directory.GetFiles(hwmonDir, "temp*_input"))
            {
                try
                {
                    var labelPath = tempInput.Replace("_input", "_label");
                    var rawLabel = File.Exists(labelPath)
                        ? (await File.ReadAllTextAsync(labelPath, ct)).Trim()
                        : Path.GetFileNameWithoutExtension(tempInput);

                    result.Add(new HwmonSensor(
                        tempInput,
                        $"linux:hwmon:{chipId}:{Path.GetFileNameWithoutExtension(tempInput)}",
                        $"{friendlyChip} {MapLabel(rawLabel)}".Trim(),
                        IsFan: false,
                        category.Length > 0 ? category : "Temperature",
                        category == "CPU" ? MetricKind.CpuTempC
                            : category == "GPU" ? MetricKind.GpuTempC
                            : MetricKind.TempC));
                }
                catch { complete = false; }
            }

            // Fans
            foreach (var fanInput in Directory.GetFiles(hwmonDir, "fan*_input"))
            {
                try
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

                    result.Add(new HwmonSensor(
                        fanInput,
                        $"linux:hwmon:{chipId}:{Path.GetFileNameWithoutExtension(fanInput)}",
                        fanName.Trim(),
                        IsFan: true,
                        "Fan",
                        MetricKind.FanRpm));
                }
                catch { complete = false; }
            }
        }

        return (result.ToArray(), complete);
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
            int.TryParse(rawLabel.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            return $"Sensor {num}";

        // Clean up fan labels like "fan1" → "Fan 1"
        if (rawLabel.StartsWith("fan", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(rawLabel.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fanNum))
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
            // The aggregate "cpu " line comes first; stop there instead of reading every per-CPU,
            // intr and softirq line of the file each tick (P4-19).
            string? line = null;
            using (var reader = new StreamReader(_statFile))
            {
                while (await reader.ReadLineAsync(ct) is { } candidate)
                {
                    if (candidate.StartsWith("cpu ", StringComparison.Ordinal))
                    {
                        line = candidate;
                        break;
                    }
                }
            }
            if (line == null) return 0;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return 0;

            // /proc/stat is C-locale, so parse it as C — see the note at the temperature read above.
            var user = double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
            var nice = double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
            var system = double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture);
            var idle = double.Parse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture);

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
            // /proc/uptime is "SECONDS.FRACTION IDLE", always with a '.' whatever the user's locale.
            // Under de-DE the ambient culture reads that '.' as a group separator, so 1234.56 parses
            // as 123456 and the reported uptime is 100× too long — no exception, just a wrong number.
            var uptimeSeconds = double.Parse(contents.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture);
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
        if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var kb))
            return kb;
        return 0;
    }
}
