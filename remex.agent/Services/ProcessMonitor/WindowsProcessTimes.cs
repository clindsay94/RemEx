using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Remex.Agent.Services.ProcessMonitor;

/// <summary>
/// Start time and CPU time for one process from a single <c>OpenProcess</c> + <c>GetProcessTimes</c>
/// (perf audit P4-21). <c>Process.StartTime</c> and <c>Process.TotalProcessorTime</c> each open their
/// own handle for the same call, and each throws a Win32Exception for every protected process on
/// every poll; this reports "unavailable" with a bool instead.
/// </summary>
/// <remarks>
/// The conversions mirror the framework's own (ProcessThreadTimes: <c>DateTime.FromFileTime</c> for
/// the start, <c>user + kernel</c> ticks for the CPU total), so the values are the ones the Process
/// properties returned. That matters for the start time: the kill guard compares it against a fresh
/// <c>Process.StartTime</c> read at kill time (<see cref="WindowsProcessMonitorService.KillProcess"/>),
/// so both must convert the same way, DST fold included.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessTimes
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    internal readonly record struct Times(long CreationFileTime, long KernelTicks, long UserTicks)
    {
        /// <summary>Same conversion as the list used on <c>Process.StartTime</c> (local, then UTC).</summary>
        public long StartUnixMs =>
            new DateTimeOffset(DateTime.FromFileTime(CreationFileTime).ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();

        public TimeSpan TotalProcessorTime => new(UserTicks + KernelTicks);
    }

    /// <summary>
    /// False when the process cannot be opened even for limited query (the Idle process, some
    /// protected ones), when the call fails, or when it has already exited - every case in which the
    /// Process properties threw.
    /// </summary>
    public static bool TryGet(int processId, out Times times)
    {
        times = default;
        if (processId == 0)
            return false; // the Idle pseudo-process; the framework refuses it the same way

        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return false;

        if (!GetProcessTimes(handle, out var creation, out var exit, out var kernel, out var user))
            return false;

        // An exited process still reachable through a lingering handle: the framework's handle
        // helper refuses these (InvalidOperationException), so no values, as before.
        if (exit != 0)
            return false;

        times = new Times(creation, kernel, user);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
}
