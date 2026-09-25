using System;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Remex.Agent.Services.RemoteDesktop.Linux.Capture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Perf audit P4-15: "no new frame within the timeout" is the normal case on a static PipeWire screen,
/// once per capture tick, so it must come back as null without an exception.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxFrameChannelTimeoutTests
{
    // Scopes the first-chance counter to this test's own async flow; other tests run in parallel.
    private static readonly AsyncLocal<bool> Watching = new();

    private static LinuxFrameSnapshot Snapshot() => new()
    {
        Width = 2,
        Height = 2,
        Stride = 8,
        Format = 12u,
        TimestampNs = 0,
        Seq = 1,
        BufferKind = LinuxBufferKind.Memfd,
        Data = null,
    };

    [Fact]
    public async Task Timeout_ReturnsNull_WithoutThrowing()
    {
        using var channel = new LinuxCaptureSessionCoordinator.LinuxFrameChannel();
        int cancellations = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) =>
        {
            if (Watching.Value && e.Exception is OperationCanceledException)
                Interlocked.Increment(ref cancellations);
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            Watching.Value = true;
            for (int i = 0; i < 3; i++)
            {
                Assert.Null(await channel.ReadAsync(timeoutMs: 10, CancellationToken.None));
            }
        }
        finally
        {
            Watching.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        Assert.Equal(0, Volatile.Read(ref cancellations));
    }

    [Fact]
    public async Task WrittenFrame_IsReturned_AndAConsumedSlotTimesOutAgain()
    {
        using var channel = new LinuxCaptureSessionCoordinator.LinuxFrameChannel();
        var frame = Snapshot();
        channel.Write(frame);

        Assert.Same(frame, await channel.ReadAsync(timeoutMs: 1000, CancellationToken.None));
        Assert.Null(await channel.ReadAsync(timeoutMs: 10, CancellationToken.None));
    }

    [Fact]
    public async Task FrameArrivingDuringTheWait_IsReturned()
    {
        using var channel = new LinuxCaptureSessionCoordinator.LinuxFrameChannel();
        var frame = Snapshot();
        var read = channel.ReadAsync(timeoutMs: 5000, CancellationToken.None);
        channel.Write(frame);

        Assert.Same(frame, await read);
    }

    [Fact]
    public async Task CallerCancellation_StillReturnsNull()
    {
        using var channel = new LinuxCaptureSessionCoordinator.LinuxFrameChannel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await channel.ReadAsync(timeoutMs: 5000, cts.Token));
    }
}
