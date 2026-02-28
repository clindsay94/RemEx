using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Host.Services.Telemetry;

[SupportedOSPlatform("linux")]
public class LinuxTelemetryService : ITelemetryService
{
    private readonly ILogger<LinuxTelemetryService> _logger;

    private readonly string _statFile = "/proc/stat";
    private readonly string _meminfoFile = "/proc/meminfo";
    private readonly string _uptimeFile = "/proc/uptime";

    private double _lastTotalCpuTime;
    private double _lastIdleCpuTime;

    public LinuxTelemetryService(ILogger<LinuxTelemetryService> logger)
    {
        _logger = logger;
    }

    public async Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
    {
        var cpuResult = await GetCpuUsageAsync(ct);
        var ramResult = await GetRamUsageAsync(ct);
        var uptimeStr = await GetUptimeAsync(ct);
        
        // GPU, Network formatting via /sys/class omitted for brevity, sensors fallback assumed.

        var sensors = new System.Collections.Generic.List<SensorReading>
        {
            new() { Name = "Total CPU Usage", Value = cpuResult, Unit = "%", Category = "CPU" },
            new() { Name = "Physical Memory Used", Value = ramResult.used, Unit = "GB", Category = "Memory" },
            new() { Name = "Physical Memory Available", Value = ramResult.total - ramResult.used, Unit = "GB", Category = "Memory" },
            new() { Name = "Physical Memory Load", Value = (ramResult.used / ramResult.total) * 100.0, Unit = "%", Category = "Memory" }
        };

        return new TelemetryPayload
        {
            Sensors = sensors,
            UptimeText = uptimeStr,
        };
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

            var usage = (1.0 - (idleDelta / totalDelta)) * 100.0;

            _lastIdleCpuTime = idle;
            _lastTotalCpuTime = total;

            return double.IsNaN(usage) ? 0 : usage;
        }
        catch
        {
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

            return (totalGb, usedGb);
        }
        catch
        {
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
        catch
        {
            return "N/A";
        }
    }

    private double ParseMeminfoLine(string? line)
    {
        if (line == null) return 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && double.TryParse(parts[1], out var kb))
            return kb;
        return 0;
    }
}
