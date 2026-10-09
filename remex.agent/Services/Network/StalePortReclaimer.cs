using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Remex.Agent.Services.Network;

/// <summary>
/// Reclaims the host's canonical listening port from a stale or duplicate Remex.Agent instance.
///
/// The Android app — the only client — dials a fixed port
/// (<see cref="Remex.Core.RemexConstants.DefaultPort"/>). If a previous host instance is still
/// holding that port (left over from a crash, or a second launch), silently drifting the new
/// instance onto a fallback port desyncs every client: they keep connecting to the dead/stale
/// instance and the stream never appears. Terminating the stale Remex.Agent lets the fresh
/// instance keep the canonical port the clients expect.
///
/// Only processes that are themselves Remex.Agent — and never the current process — are ever
/// terminated. A non-Remex occupant is left untouched (the caller falls back to an alternate port).
/// </summary>
internal static class StalePortReclaimer
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Attempts to free <paramref name="port"/> by terminating any stale Remex.Agent process
    /// listening on it. Returns true if at least one stale host was terminated (the caller
    /// should then re-probe the port before binding).
    /// </summary>
    public static bool TryReclaim(int port) =>
        TryReclaim(port, FindListenerPids, GetProcessNameOrNull, KillProcessTree);

    // Test seam: the OS lookups and the kill are injected so the kill decision runs without real processes.
    internal static bool TryReclaim(
        int port,
        Func<int, IReadOnlyList<int>> findListenerPids,
        Func<int, string?> getProcessName,
        Action<int> killProcessTree)
    {
        bool reclaimedAny = false;
        try
        {
            foreach (var pid in findListenerPids(port))
            {
                if (pid == Environment.ProcessId)
                {
                    continue;
                }

                var name = getProcessName(pid);
                if (name is null || !name.StartsWith("Remex.Agent", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Port {port} is held by pid {pid}, which is not a Remex.Agent process; leaving it alone.");
                    continue;
                }

                Log($"Port {port} held by a stale Remex.Agent (pid {pid}); terminating it to reclaim the canonical port.");
                try
                {
                    killProcessTree(pid);
                    reclaimedAny = true;
                }
                catch (Exception ex)
                {
                    Log($"Failed to terminate stale host pid {pid}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Port reclaim probe failed for {port}: {ex.Message}");
        }

        if (!reclaimedAny)
        {
            return false;
        }

        // Give the OS a moment to release the listening socket after the process exits.
        var deadline = DateTime.UtcNow + ReleaseTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (findListenerPids(port).Count == 0)
            {
                break;
            }

            Thread.Sleep(150);
        }

        return true;
    }

    // The published apphost (installed service or `dotnet run` launcher) is named "Remex.Agent" on
    // every platform ("Remex.Agent.exe" → ProcessName "Remex.Agent"); the prefix check lives in TryReclaim.
    private static string? GetProcessNameOrNull(int pid)
    {
        try
        {
            using var candidate = Process.GetProcessById(pid);
            return candidate.ProcessName;
        }
        catch
        {
            // Process exited between enumeration and lookup, or access denied.
            return null;
        }
    }

    private static void KillProcessTree(int pid)
    {
        using var proc = Process.GetProcessById(pid);
        proc.Kill(entireProcessTree: true);
        proc.WaitForExit((int)ReleaseTimeout.TotalMilliseconds);
    }

    private static IReadOnlyList<int> FindListenerPids(int port)
    {
        if (OperatingSystem.IsLinux())
        {
            return FindListenerPidsLinux(port);
        }

        if (OperatingSystem.IsWindows())
        {
            return FindListenerPidsWindows(port);
        }

        return Array.Empty<int>();
    }

    private static IReadOnlyList<int> FindListenerPidsLinux(int port)
    {
        // `ss -H -ltnp` lists listening TCP sockets with owning process, e.g.:
        //   LISTEN 0 512 *:5005 *:* users:(("Remex.Agent",pid=80741,fd=198))
        var output = RunTool("ss", "-H -ltnp");
        if (string.IsNullOrEmpty(output))
        {
            return Array.Empty<int>();
        }

        var pids = new List<int>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Fields: State Recv-Q Send-Q LocalAddress:Port PeerAddress:Port [users:(...)]
            if (fields.Length < 4)
            {
                continue;
            }

            if (!EndpointMatchesPort(fields[3], port))
            {
                continue;
            }

            var pidMatch = Regex.Match(line, @"pid=(\d+)");
            // Invariant: this is `ss`/`netstat` output, which is C-locale regardless of the user's.
            if (pidMatch.Success
                && int.TryParse(pidMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                pids.Add(pid);
            }
        }

        return pids.Distinct().ToList();
    }

    private static IReadOnlyList<int> FindListenerPidsWindows(int port)
    {
        // `netstat -ano -p TCP` rows: TCP  0.0.0.0:5005  0.0.0.0:0  LISTENING  80741
        var output = RunTool("netstat", "-ano -p TCP");
        if (string.IsNullOrEmpty(output))
        {
            return Array.Empty<int>();
        }

        var pids = new List<int>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("LISTENING", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            if (!EndpointMatchesPort(parts[1], port))
            {
                continue;
            }

            if (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                pids.Add(pid);
            }
        }

        return pids.Distinct().ToList();
    }

    /// <summary>
    /// True when a "host:port" endpoint string (e.g. <c>*:5005</c>, <c>[::]:5005</c>,
    /// <c>0.0.0.0:5005</c>) names the given port.
    /// </summary>
    private static bool EndpointMatchesPort(string endpoint, int port)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon >= 0
            && int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            && p == port;
    }

    private static string RunTool(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output;
        }
        catch
        {
            // Tool missing (ss/netstat absent) or not permitted — treat as "no listeners found".
            return string.Empty;
        }
    }

    private static void Log(string message) => Console.WriteLine($"[StalePortReclaimer] {message}");
}
