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

public class WindowsProcessMonitorService : IProcessMonitorService
{
    private readonly ILogger<WindowsProcessMonitorService> _logger;
    private readonly Dictionary<int, ProcessCpuTracker> _cpuTrackers = new();
    private DateTime _lastScanTime = DateTime.UtcNow;

    public WindowsProcessMonitorService(ILogger<WindowsProcessMonitorService> logger)
    {
        _logger = logger;
    }

    public Task<List<ProcessInfo>> GetProcessesAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<ProcessInfo>();
            var activePids = new HashSet<int>();
            var now = DateTime.UtcNow;
            var timeDiff = (now - _lastScanTime).TotalMilliseconds;
            _lastScanTime = now;
            var processes = Process.GetProcesses();

            foreach (var p in processes)
            {
                activePids.Add(p.Id);
                var info = new ProcessInfo
                {
                    Id = p.Id,
                    Name = p.ProcessName
                };

                try
                {
                    info = info with { MemoryUsage = p.WorkingSet64 };
                }
                catch { }

                try
                {
                    var cpuTime = p.TotalProcessorTime.TotalMilliseconds;
                    if (!_cpuTrackers.TryGetValue(p.Id, out var tracker))
                    {
                        tracker = new ProcessCpuTracker { LastCpuTime = cpuTime };
                        _cpuTrackers[p.Id] = tracker;
                        info = info with { CpuUsage = 0 };
                    }
                    else
                    {
                        var cpuDiff = cpuTime - tracker.LastCpuTime;
                        tracker.LastCpuTime = cpuTime;
                        double usage = 0;
                        if (timeDiff > 0)
                        {
                            usage = (cpuDiff / timeDiff) * 100.0 / Environment.ProcessorCount;
                        }
                        info = info with { CpuUsage = usage };
                    }
                }
                catch { }

                // Attempt to get file path and publisher/version
                try
                {
                    string path = p.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        info = info with { FilePath = path };
                        var fvi = FileVersionInfo.GetVersionInfo(path);
                        info = info with
                        {
                            Version = fvi.FileVersion ?? "",
                            Publisher = fvi.CompanyName ?? ""
                        };
                        try
                        {
                            var fi = new FileInfo(path);
                            info = info with { InstallDate = fi.CreationTime };
                        }
                        catch { }
                    }
                }
                catch { } // Access denied is common here for system procs

                results.Add(info);
                p.Dispose();
            }

            // Cleanup old trackers
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

    private class ProcessCpuTracker
    {
        public double LastCpuTime { get; set; }
    }
}
