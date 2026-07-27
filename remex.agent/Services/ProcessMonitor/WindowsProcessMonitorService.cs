using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Services.ProcessMonitor;

public class WindowsProcessMonitorService : IProcessMonitorService
{
    private readonly ILogger<WindowsProcessMonitorService> _logger;
    private readonly Dictionary<int, ProcessCpuTracker> _cpuTrackers = new();
    private readonly object _lock = new();
    private DateTime _lastScanTime = DateTime.UtcNow;

    public WindowsProcessMonitorService(ILogger<WindowsProcessMonitorService> logger)
    {
        _logger = logger;
    }

    public Task<List<ProcessInfo>> GetProcessesAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
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
                    Name = p.ProcessName,
                    StartTimeUnixMs = TryReadStartUnixMs(p)
                };

                try
                {
                    info = info with { MemoryUsage = p.WorkingSet64 };
                }
                catch
                {
                    // Silent per-property degradation, not an error. WorkingSet64 throws for protected and
                    // system processes even from an elevated host, and for any process that exits between the
                    // enumeration and this read. The row is still worth listing with the memory figure absent -
                    // dropping it would hide a running process from the user entirely.
                }

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
                catch (System.ComponentModel.Win32Exception)
                {
                    // As above: TotalProcessorTime is unreadable for protected processes.
                }
                catch (InvalidOperationException)
                {
                    // And InvalidOperationException is the process having exited mid-enumeration.
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Unexpected error getting CPU time for {Pid}", p.Id);
                }

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
                        catch
                        {
                            // The executable exists but its creation time is unreadable - a permissions or
                            // reparse-point case. The install date is cosmetic; the row is not.
                        }
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // MainModule is the most commonly denied of these: it is refused for every process at a
                    // higher integrity level, which is most of the ones worth protecting.
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and read.
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Unexpected error getting module info for {Pid}", p.Id);
                } // Access denied is common here for system procs

                results.Add(info);
                p.Dispose();
            }

            // Cleanup old trackers
            var toRemove = _cpuTrackers.Keys.Where(k => !activePids.Contains(k)).ToList();
            foreach (var k in toRemove) _cpuTrackers.Remove(k);

            return results;
            }
        });
    }


    /// <summary>
    /// A process's start time as Unix milliseconds UTC, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// <c>Process.StartTime</c> throws for protected and system processes even from an elevated
    /// host, so this must degrade the value rather than the row — a process with no readable start
    /// time is still listed and still killable, just unverified on that axis. (RemEx-on4n.)
    /// </remarks>
    private static long? TryReadStartUnixMs(Process p)
    {
        try
        {
            return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public ProcessKillResult KillProcess(int processId, string? expectedName = null, long? expectedStartUnixMs = null)
    {
        try
        {
            var p = Process.GetProcessById(processId);

            // Check identity immediately before killing. This is the whole reason the check lives
            // on the host: any client-side version is separated from the kill by a network round
            // trip, during which the PID can change hands (RemEx-druh).
            //
            // Not literally atomic, and the comment should not claim it is: ProcessName is read
            // from the snapshot GetProcessById took, and Kill() re-resolves the PID when it opens
            // its handle. What this removes is the round trip and the user's thinking time — the
            // residual window is a handful of instructions rather than seconds.
            //
            // Both the process list and this check read the name from Process.ProcessName, so the
            // two agree by construction. That is NOT true on Linux, which has a second, truncated
            // spelling — see the note in LinuxProcessMonitorService.
            if (!ProcessKillGuard.IsExpectedProcess(expectedName, p.ProcessName)
                || !ProcessKillGuard.IsExpectedStartTime(expectedStartUnixMs, TryReadStartUnixMs(p)))
            {
                _logger.LogWarning(
                    "Refused to end process {Pid}: expected {Expected} but found {Actual}.",
                    processId, expectedName, p.ProcessName);
                return new ProcessKillResult(
                    false,
                    ProcessKillErrorCodes.Format(
                        ProcessKillErrorCodes.IdentityMismatch,
                        ProcessKillGuard.MismatchMessage(processId, expectedName, p.ProcessName),
                        p.ProcessName));
            }

            p.Kill(entireProcessTree: true);
            return new ProcessKillResult(true, "Process killed.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 5 or 1314)
        {
            _logger.LogWarning(ex, "Access denied killing process {Pid}; elevation is required.", processId);
            return new ProcessKillResult(
                false,
                ProcessKillErrorCodes.Format(
                    ProcessKillErrorCodes.AccessDenied,
                    "Access denied. Run the host as Administrator or retry with KillProcessElevated."),
                true);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Process {Pid} could not be found.", processId);
            return new ProcessKillResult(
                false,
                ProcessKillErrorCodes.Format(
                    ProcessKillErrorCodes.NotRunning, "Process could not be found."));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Process {Pid} has already exited.", processId);
            return new ProcessKillResult(
                false,
                ProcessKillErrorCodes.Format(
                    ProcessKillErrorCodes.NotRunning, "Process has already exited."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill process {Pid}", processId);
            return new ProcessKillResult(
                false,
                ProcessKillErrorCodes.Format(
                    ProcessKillErrorCodes.Failed, $"Failed to kill process {processId}."));
        }
    }

    private class ProcessCpuTracker
    {
        public double LastCpuTime { get; set; }
    }
}
