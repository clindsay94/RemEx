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
    private readonly object _lock = new();

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
            long totalCpuDiff;
            lock (_lock)
            {
                totalCpuDiff = currentTotalCpuTime - _lastTotalCpuTime;
                _lastTotalCpuTime = currentTotalCpuTime;
            }

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
                    // The comm field (process name) is enclosed in parentheses and may contain spaces.
                    // Locate the last ')' to reliably find where the fixed fields begin.
                    int lastParen = stat.LastIndexOf(')');
                    int firstParen = stat.IndexOf('(');
                    if (firstParen < 0 || lastParen < 0 || lastParen <= firstParen) continue;
                    name = stat.Substring(firstParen + 1, lastParen - firstParen - 1);
                    var remainder = stat.Substring(lastParen + 2); // skip ') '
                    var parts = remainder.Split(' ');
                    // After comm: state(0), ppid(1), pgrp(2), session(3), tty_nr(4), tpgid(5),
                    // flags(6), minflt(7), cminflt(8), majflt(9), cmajflt(10), utime(11), stime(12)
                    if (parts.Length < 13) continue;
                    long utime = long.Parse(parts[11]);
                    long stime = long.Parse(parts[12]);
                    processTotalCpuTime = utime + stime;
                }
                catch { continue; }

                try
                {
                    var statmParts = File.ReadAllText(Path.Combine(dir, "statm")).Split(' ');
                    long rssPages = long.Parse(statmParts[1]);
                    memory = rssPages * Environment.SystemPageSize;
                }
                catch { }

                try
                {
                    exePath = File.ResolveLinkTarget(Path.Combine(dir, "exe"), true)?.FullName ?? "";
                }
                catch { }

                double cpuUsage = 0;
                lock (_lock)
                {
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

            lock (_lock)
            {
                var toRemove = _cpuTrackers.Keys.Where(k => !activePids.Contains(k));
                foreach (var k in toRemove) _cpuTrackers.Remove(k);
            }

            return results;
        });
    }

    public ProcessKillResult KillProcess(int processId)
    {
        try
        {
            var p = Process.GetProcessById(processId);
            p.Kill();
            return new ProcessKillResult(true, "Process killed.");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Process {Pid} could not be found.", processId);
            return new ProcessKillResult(false, "Process could not be found.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Process {Pid} has already exited.", processId);
            return new ProcessKillResult(false, "Process has already exited.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill process {Pid}", processId);
            return new ProcessKillResult(false, $"Failed to kill process {processId}.");
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
