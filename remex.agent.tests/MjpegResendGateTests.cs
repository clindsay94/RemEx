using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// P1-13: the per-stream MJPEG gate skips re-publishing the frame it last published (same buffer),
/// sends anything else, and still re-sends an unchanged frame on the forced-refresh cadence.
/// Pure state machine, so no real timing is involved.
/// </summary>
public class MjpegResendGateTests
{
    private static ReadOnlyMemory<byte> Frame(byte fill) => new byte[] { 0xFF, 0xD8, fill, 0xFF, 0xD9 };

    [Fact]
    public void FirstFrameIsPublished()
    {
        var gate = new MjpegResendGate();

        Assert.True(gate.ShouldPublish(Frame(1), streamSerial: 1, nowMs: 0));
    }

    [Fact]
    public void TheSameBufferAgainIsSkippedWithinTheRefreshInterval()
    {
        var gate = new MjpegResendGate();
        var frame = Frame(1);

        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 0));
        Assert.False(gate.ShouldPublish(frame, 1, nowMs: 100));
        Assert.False(gate.ShouldPublish(frame, 1, nowMs: MjpegResendGate.ForcedRefreshIntervalMs - 1));
    }

    [Fact]
    public void AChangedFrameIsPublished()
    {
        var gate = new MjpegResendGate();

        Assert.True(gate.ShouldPublish(Frame(1), 1, nowMs: 0));
        Assert.True(gate.ShouldPublish(Frame(2), 1, nowMs: 10));
    }

    [Fact]
    public void ANewBufferIsPublishedEvenWithIdenticalBytes()
    {
        // Identity, not content: the backend promises an unchanged screen comes back as the SAME
        // buffer, so a different buffer is treated as new. Erring toward sending is the safe side.
        var gate = new MjpegResendGate();

        Assert.True(gate.ShouldPublish(Frame(1), 1, nowMs: 0));
        Assert.True(gate.ShouldPublish(Frame(1), 1, nowMs: 10));
    }

    [Fact]
    public void ADifferentSliceOfTheSameArrayIsPublished()
    {
        var gate = new MjpegResendGate();
        var backing = new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 };

        Assert.True(gate.ShouldPublish(backing.AsMemory(0, 5), 1, nowMs: 0));
        Assert.True(gate.ShouldPublish(backing.AsMemory(0, 7), 1, nowMs: 10));
    }

    [Fact]
    public void AnUnchangedFrameIsForcedOutOnTheRefreshCadence()
    {
        var gate = new MjpegResendGate();
        var frame = Frame(1);
        const double interval = MjpegResendGate.ForcedRefreshIntervalMs;

        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 0));
        Assert.False(gate.ShouldPublish(frame, 1, nowMs: interval / 2));
        Assert.True(gate.ShouldPublish(frame, 1, nowMs: interval));

        // The cadence restarts from the forced send, not from the original publish.
        Assert.False(gate.ShouldPublish(frame, 1, nowMs: interval + 100));
        Assert.True(gate.ShouldPublish(frame, 1, nowMs: interval * 2));
    }

    [Fact]
    public void AStreamSerialChangePublishesTheSameBuffer()
    {
        var gate = new MjpegResendGate();
        var frame = Frame(1);

        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 0));
        Assert.True(gate.ShouldPublish(frame, 2, nowMs: 10));
        Assert.False(gate.ShouldPublish(frame, 2, nowMs: 20));
    }

    [Fact]
    public void ForcePublishesTheSameBuffer()
    {
        var gate = new MjpegResendGate();
        var frame = Frame(1);

        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 0));
        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 10, force: true));
    }

    [Fact]
    public void ResetPublishesTheSameBuffer()
    {
        var gate = new MjpegResendGate();
        var frame = Frame(1);

        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 0));
        gate.Reset();
        Assert.True(gate.ShouldPublish(frame, 1, nowMs: 10));
    }
}
