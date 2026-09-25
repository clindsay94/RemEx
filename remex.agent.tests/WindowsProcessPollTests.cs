using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.ProcessMonitor;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Perf audit P4-21: one limited-query handle per process for start + CPU time, and no repeated
/// exceptions for protected processes on every poll.
/// </summary>
public class WindowsProcessPollTests
{
    private static readonly AsyncLocal<bool> Watching = new();

    [Fact]
    public void ProcessTimes_MatchTheFrameworkProperties_ForTheCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var self = Process.GetCurrentProcess();
        Assert.True(WindowsProcessTimes.TryGet(self.Id, out var times));

        // The kill guard compares this against a fresh Process.StartTime at kill time, so the two
        // conversions must agree exactly, not approximately.
        var expectedStart = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(expectedStart, times.StartUnixMs);

        var frameworkCpu = self.TotalProcessorTime;
        Assert.True(times.TotalProcessorTime > TimeSpan.Zero);
        Assert.True((frameworkCpu - times.TotalProcessorTime).Duration() < TimeSpan.FromSeconds(2),
            $"framework {frameworkCpu} vs direct {times.TotalProcessorTime}");
    }

    [Fact]
    public void ProcessTimes_RefuseTheIdleProcess_WithoutThrowing()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(WindowsProcessTimes.TryGet(0, out _));
    }

    [Fact]
    public async Task SecondPoll_DoesNotThrowAgain_ForProcessesThatRefusedTheFirst()
    {
        if (!OperatingSystem.IsWindows()) return;

        var service = new WindowsProcessMonitorService(NullLogger<WindowsProcessMonitorService>.Instance);
        int win32 = 0;
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) =>
        {
            if (Watching.Value && e.Exception is Win32Exception w)
            {
                Interlocked.Increment(ref win32);
                seen.Add(w.NativeErrorCode + ":" + new StackTrace(1, false).GetFrames().Take(8)
                    .Select(f => f.GetMethod()?.Name).Aggregate((a, b) => a + "<" + b));
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            Watching.Value = true;
            var first = await service.GetProcessesAsync();
            int firstPoll = Interlocked.Exchange(ref win32, 0);
            var second = await service.GetProcessesAsync();
            int secondPoll = Volatile.Read(ref win32);

            Assert.NotEmpty(first);
            Assert.NotEmpty(second);
            Assert.Contains(second, p => p.Id == Environment.ProcessId && !string.IsNullOrEmpty(p.FilePath));
            // A protected process started between the polls may still throw once; the steady state
            // must not repeat the first poll's denials.
            Assert.True(secondPoll <= 3,
                $"first poll threw {firstPoll} Win32Exceptions, second threw {secondPoll}: "
                + string.Join(" | ", seen.GroupBy(s => s).Select(g => g.Count() + "x " + g.Key)));
        }
        finally
        {
            Watching.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }
    }
}
