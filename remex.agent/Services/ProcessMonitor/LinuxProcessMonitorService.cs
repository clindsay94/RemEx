using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Services.ProcessMonitor;

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

            // Read once per poll: every process's start time is derived from it, and it does
            // not change while the machine is up.
            long bootTimeUnixSeconds = GetBootTimeUnixSeconds();

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
                long? startTimeUnixMs = null;

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
                    startTimeUnixMs = TryComputeStartUnixMs(parts, bootTimeUnixSeconds);
                }
                catch { continue; }

                try
                {
                    var statmParts = File.ReadAllText(Path.Combine(dir, "statm")).Split(' ');
                    long rssPages = long.Parse(statmParts[1]);
                    memory = rssPages * Environment.SystemPageSize;
                }
                catch
                {
                    // Silent per-property degradation. /proc/<pid>/statm disappears the moment the process
                    // exits, and the field layout is not guaranteed for kernel threads - the row is still
                    // listed with the memory figure absent rather than dropped.
                }

                try
                {
                    exePath = File.ResolveLinkTarget(Path.Combine(dir, "exe"), true)?.FullName ?? "";
                }
                catch
                {
                    // Resolving /proc/<pid>/exe needs permission the host may not have for another user's
                    // process; an empty path is the honest answer and the row survives.
                }

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
                    catch
                    {
                        // The executable resolved but its creation time did not. Cosmetic, unlike the row.
                    }
                }

                results.Add(new ProcessInfo
                {
                    Id = pid,
                    Name = name,
                    StartTimeUnixMs = startTimeUnixMs,
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

    /// <summary>
    /// Reads a process's name from <c>/proc/{pid}/stat</c> — the same source the process list sends
    /// to clients, and therefore the spelling a client echoes back.
    /// </summary>
    /// <remarks>
    /// This repeats the parse in <see cref="GetProcessesAsync"/> deliberately rather than reusing
    /// <c>Process.ProcessName</c>, because the two do not agree: the kernel truncates <c>comm</c> to
    /// <c>TASK_COMM_LEN - 1</c> = 15 characters and <c>ProcessName</c> is not truncated. The field is
    /// wrapped in parentheses and may itself contain spaces and parentheses, which is why the
    /// closing one is found from the END of the line rather than the first one after the opening.
    /// <para>
    /// Returns null rather than throwing when the file cannot be read — that means the process is
    /// gone, and the caller falls back to the name it already holds.
    /// </para>
    /// </remarks>
    private static string? TryReadCommName(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var firstParen = stat.IndexOf('(');
            var lastParen = stat.LastIndexOf(')');
            if (firstParen < 0 || lastParen <= firstParen)
                return null;

            return stat.Substring(firstParen + 1, lastParen - firstParen - 1);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Clock ticks per second, as <c>/proc/&lt;pid&gt;/stat</c> reports CPU and start times.
    /// </summary>
    /// <remarks>
    /// This is <c>USER_HZ</c>, which the kernel fixes at 100 for userspace regardless of the
    /// internal <c>CONFIG_HZ</c>. Reading it properly means <c>sysconf(_SC_CLK_TCK)</c> through a
    /// P/Invoke; the constant is used instead and named here so the assumption is visible rather
    /// than buried in a magic number. Verified against <c>getconf CLK_TCK</c> and, more usefully,
    /// against <see cref="Process.StartTime"/> itself — see the remarks on
    /// <see cref="TryComputeStartUnixMs"/>.
    /// </remarks>
    private const long UserHz = 100;

    /// <summary>Field index of <c>starttime</c> within the stat fields that follow <c>comm</c>.</summary>
    /// <remarks>
    /// <c>starttime</c> is field 22 of <c>/proc/&lt;pid&gt;/stat</c> counting from 1. The callers here
    /// split the remainder AFTER the closing parenthesis of <c>comm</c> (field 2), so field N lands
    /// at index N-3 — hence 19.
    /// </remarks>
    private const int StartTimeFieldIndex = 19;

    /// <summary>
    /// Boot time as Unix seconds, from <c>/proc/stat</c>'s <c>btime</c> line. Zero when unreadable.
    /// </summary>
    private static long GetBootTimeUnixSeconds()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                {
                    return seconds;
                }
            }
        }
        catch (IOException)
        {
            // The process vanished between the directory scan and this read, which is routine on a
            // busy machine rather than exceptional.
        }
        catch (UnauthorizedAccessException)
        {
            // Same, for a process this host is not permitted to inspect.
        }

        return 0;
    }

    /// <summary>
    /// A process's start time as Unix milliseconds UTC, computed from stat fields already in hand.
    /// </summary>
    /// <param name="statFieldsAfterComm">
    /// The stat line split on spaces, starting AFTER <c>comm</c>'s closing parenthesis.
    /// </param>
    /// <param name="bootTimeUnixSeconds">From <see cref="GetBootTimeUnixSeconds"/>; 0 means unknown.</param>
    /// <remarks>
    /// <c>starttime</c> is measured in clock ticks since boot, so wall-clock time is boot time plus
    /// that. Computing it here rather than materialising a <see cref="Process"/> per row matters
    /// twice over. It removes a second read of a file the caller has already read, for every process
    /// on every poll (RemEx-nchu) — but more importantly it means the process LISTING and the
    /// kill-time check derive the value the same way, so they cannot disagree. That is not a
    /// theoretical concern here: this class previously exposed a process's name from two different
    /// sources that disagreed above 15 characters, which refused every kill of a long-named process
    /// until review caught it (RemEx-druh).
    /// <para>
    /// Verified rather than assumed: across every process on a live Linux machine, this agreed with
    /// <see cref="Process.StartTime"/> to within 508 ms — the residual being <c>btime</c>'s
    /// whole-second granularity, comfortably inside
    /// <see cref="Remex.Core.Services.ProcessKillGuard.StartTimeToleranceMs"/>.
    /// </para>
    /// </remarks>
    private static long? TryComputeStartUnixMs(string[] statFieldsAfterComm, long bootTimeUnixSeconds)
    {
        if (bootTimeUnixSeconds <= 0
            || statFieldsAfterComm.Length <= StartTimeFieldIndex
            || !long.TryParse(
                statFieldsAfterComm[StartTimeFieldIndex],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var ticksSinceBoot))
        {
            return null;
        }

        return bootTimeUnixSeconds * 1000 + ticksSinceBoot * 1000 / UserHz;
    }

    /// <summary>
    /// A single process's start time, for the kill-time check. Null when it cannot be read, which
    /// the guard treats as unchecked.
    /// </summary>
    private static long? TryReadStartUnixMs(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var lastParen = stat.LastIndexOf(')');
            if (lastParen < 0 || lastParen + 2 >= stat.Length)
                return null;

            return TryComputeStartUnixMs(stat[(lastParen + 2)..].Split(' '), GetBootTimeUnixSeconds());
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ProcessKillResult KillProcess(int processId, string? expectedName = null, long? expectedStartUnixMs = null)
    {
        try
        {
            var p = Process.GetProcessById(processId);

            // Check identity immediately before killing, narrowing the window from a full client
            // round trip plus however long the user took to confirm, down to a single host-side
            // check-then-kill. PID recycling matters more here than on Windows, since Linux hands
            // PIDs out sequentially and wraps at a much lower ceiling (RemEx-druh).
            //
            // BOTH names are accepted, and that is not belt-and-braces — it is the fix for a real
            // regression. This host has TWO different names for the same process:
            // GetProcessesAsync parses /proc/<pid>/stat's comm field, which the kernel truncates to
            // TASK_COMM_LEN-1 = 15 characters, while Process.ProcessName is not truncated. The
            // client echoes back whichever one it was sent — the truncated one. Comparing that
            // against ProcessName alone refused EVERY process whose real name exceeds 15
            // characters, every time, with a message telling the user their list was stale when it
            // was not. gnome-terminal-server, xdg-desktop-portal and most Electron helpers are all
            // over that limit. Accepting either host-derived spelling costs nothing, because a
            // genuinely different program matches neither.
            var commName = TryReadCommName(processId);
            var nameMatches = ProcessKillGuard.IsExpectedProcess(expectedName, p.ProcessName)
                || (commName is not null && ProcessKillGuard.IsExpectedProcess(expectedName, commName));
            if (!nameMatches
                || !ProcessKillGuard.IsExpectedStartTime(expectedStartUnixMs, TryReadStartUnixMs(processId)))
            {
                var reported = commName ?? p.ProcessName;
                _logger.LogWarning(
                    "Refused to end process {Pid}: expected {Expected} but found {Actual} (comm {Comm}).",
                    processId, expectedName, p.ProcessName, commName);
                return new ProcessKillResult(
                    false,
                    ProcessKillErrorCodes.Format(
                        ProcessKillErrorCodes.IdentityMismatch,
                        ProcessKillGuard.MismatchMessage(processId, expectedName, reported),
                        reported));
            }

            p.Kill();
            return new ProcessKillResult(true, "Process killed.");
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
        catch
        {
            // Total CPU time is a denominator for percentage figures only. If /proc/stat cannot be
            // read, every percentage degrades to zero rather than the whole listing failing.
        }
        return 0;
    }

    private class ProcessCpuTracker
    {
        public long LastCpuTime { get; set; }
    }
}
