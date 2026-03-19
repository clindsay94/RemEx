using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Host.Services.ProcessMonitor;

public class LinuxProcessMonitorService : IProcessMonitorService
{
    private readonly ILogger<LinuxProcessMonitorService> _logger;
    private readonly Dictionary<int, ProcessCpuTracker> _cpuTrackers = new();
    private long _lastTotalCpuTime = 0;

    public LinuxProcessMonitorService(ILogger<LinuxProcessMonitorService> logger)
    {
        _logger = logger;
    }

    public Task<List<ProcessInfo>> GetProcessesAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<ProcessInfo>();
            var activePids = new HashSet<int>();

            long currentTotalCpuTime = GetTotalCpuTime();
            long totalCpuDiff = currentTotalCpuTime - _lastTotalCpuTime;
            _lastTotalCpuTime = currentTotalCpuTime;

            var processDirs = Directory.GetDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _)).ToList();

            foreach (var dir in processDirs)
            {
                if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
                activePids.Add(pid);

                string name = "";
                long memory = 0;
                long processTotalCpuTime = 0;
                string exePath = "";

                try
                {
                    string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var parts = stat.Split(' ');
                    name = parts[1].Trim('(', ')');
                    long utime = long.Parse(parts[13]);
                    long stime = long.Parse(parts[14]);
                    processTotalCpuTime = utime + stime;
                }
                catch { continue; }

                try
                {
                    var statmParts = File.ReadAllText(Path.Combine(dir, "statm")).Split(' ');
                    long rssPages = long.Parse(statmParts[1]);
                    // Assuming 4KB pages
                    memory = rssPages * 4096;
                }
                catch { }

                try
                {
                    exePath = File.ResolveLinkTarget(Path.Combine(dir, "exe"), true)?.FullName ?? "";
                }
                catch { }

                double cpuUsage = 0;
                if (!_cpuTrackers.TryGetValue(pid, out var tracker))
                {
                    tracker = new ProcessCpuTracker { LastCpuTime = processTotalCpuTime };
                    _cpuTrackers[pid] = tracker;
                }
                else
                {
                    long diff = processTotalCpuTime - tracker.LastCpuTime;
                    tracker.LastCpuTime = processTotalCpuTime;
                    if (totalCpuDiff > 0)
                    {
                        cpuUsage = (double)diff / totalCpuDiff * 100.0;
                    }
                }

                DateTime? installDate = null;
                if (!string.IsNullOrEmpty(exePath))
                {
                    try
                    {
                        var fi = new FileInfo(exePath);
                        installDate = fi.CreationTime;
                    }
                    catch { }
                }

                results.Add(new ProcessInfo
                {
                    Id = pid,
                    Name = name,
                    MemoryUsage = memory,
                    CpuUsage = cpuUsage,
                    FilePath = exePath,
                    InstallDate = installDate
                });
            }

            var toRemove = _cpuTrackers.Keys.Where(k => !activePids.Contains(k)).ToList();
            foreach (var k in toRemove) _cpuTrackers.Remove(k);

            return results;
        });
    }

    public bool KillProcess(int processId)
    {
        try
        {
            var p = Process.GetProcessById(processId);
            p.Kill();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill process {Pid}", processId);
            return false;
        }
    }

    private long GetTotalCpuTime()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/stat");
            if (lines.Length > 0 && lines[0].StartsWith("cpu "))
            {
                var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long total = 0;
                for (int i = 1; i < parts.Length; i++)
                {
                    if (long.TryParse(parts[i], out long val)) total += val;
                }
                return total;
            }
        }
        catch { }
        return 0;
    }

    private class ProcessCpuTracker
    {
        public long LastCpuTime { get; set; }
    }
}
