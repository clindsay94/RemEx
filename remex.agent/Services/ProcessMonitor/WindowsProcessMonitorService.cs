using System.Diagnostics;
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

    /// <summary>Version/publisher per executable, so the scan reads each file once (RemEx-qz5z3).</summary>
    /// <remarks>Guarded by <see cref="_lock"/> along with everything else in a scan.</remarks>
    private readonly ExecutableMetadataCache _metadataCache = new();

    /// <summary>Reads the version resource. The expensive call the cache exists to avoid.</summary>
    internal static ExecutableMetadata LoadExecutableMetadata(string path)
    {
        var fvi = FileVersionInfo.GetVersionInfo(path);
        return new ExecutableMetadata(fvi.FileVersion ?? "", fvi.CompanyName ?? "");
    }

    /// <summary>
    /// Picks the cache key for one executable, or bypasses the cache when it has no usable stat.
    /// </summary>
    /// <remarks>
    /// **EXTRACTED SO THE KEY CHOICE IS TESTABLE, BECAUSE IT IS THE ONE LINE THE BEAD FORBIDS GETTING
    /// WRONG.** Keying on creation time instead of write time looks equivalent and is not: NTFS
    /// tunnelling preserves a creation time across an in-place replace, so an updated program would
    /// report its old version for the life of the agent. Inline, that swap changed no test.
    /// </remarks>
    internal static ExecutableMetadata ResolveMetadata(
        string path,
        FileInfo? stat,
        ExecutableMetadataCache cache,
        Func<string, ExecutableMetadata> load) =>
        stat is null ? load(path) : cache.GetOrAdd(path, stat.LastWriteTimeUtc, load);

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

            // Executables seen this scan, used to trim the metadata cache below. Ordinal because a
            // differently-cased path (MainModule preserves the casing a process was launched with,
            // so a service started from a registry ImagePath can differ from one started via
            // Explorer) costs one extra parse and never a wrong answer, where an ignore-case fold
            // costs a comparison on every lookup to save that.
            var livePaths = new HashSet<string>(StringComparer.Ordinal);
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
                        livePaths.Add(path);

                        // **ONE STAT SERVES BOTH THE INSTALL DATE AND THE CACHE KEY (RemEx-qz5z3).**
                        // Version and publisher are properties of the FILE, not of the process, and
                        // reading them opens and parses the executable's version resource - which used
                        // to happen once per process rather than once per file, so a dozen svchost
                        // instances meant a dozen identical parses, repeated on every poll. Keying on
                        // the write time as well as the path means a rewritten file is re-read on the
                        // next scan, which matters because this agent runs for weeks and nobody would
                        // think to restart it to correct a version string. Detection is by TIMESTAMP,
                        // not content: a replacement preserving the write time (robocopy's default)
                        // keeps the old entry while a process still holds the path. Narrow, because
                        // Windows locks a mapped image, so overwriting a running exe needs a swap.
                        //
                        // The stat is inside its own try because it was already: an executable whose
                        // creation time is unreadable (permissions, a reparse point) must still get a
                        // row. When it fails, the cache is bypassed entirely rather than keyed on a
                        // guessed timestamp - a wrong key is worse than no cache, because it would
                        // pin whatever it stored under a value the next scan may not reproduce.
                        FileInfo? fileInfo = null;
                        try
                        {
                            fileInfo = new FileInfo(path);
                            info = info with { InstallDate = fileInfo.CreationTime };
                        }
                        catch
                        {
                            // The executable exists but its creation time is unreadable - a permissions or
                            // reparse-point case. The install date is cosmetic; the row is not.
                            fileInfo = null;
                        }

                        // Method group, not a lambda: LoadExecutableMetadata captures nothing, so the
                        // compiler caches one delegate instead of allocating a closure per process
                        // per scan - including on the hits this change exists to produce.
                        var metadata = ResolveMetadata(path, fileInfo, _metadataCache, LoadExecutableMetadata);

                        info = info with
                        {
                            Version = metadata.Version,
                            Publisher = metadata.Publisher
                        };
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

            // Trimmed alongside the CPU trackers and for the same reason: this service lives as long
            // as the agent does, so a cache that only ever grows is a leak with a slow fuse. Bounding
            // it to executables currently running keeps it at a few hundred entries instead of every
            // binary — and every VERSION of every binary — launched since the machine booted.
            _metadataCache.RetainOnly(livePaths);

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
