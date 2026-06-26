using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// RD-E wire-format guards: the binary cursor packet must round-trip exactly (including NEGATIVE
/// coordinates for left/top monitors) and must NOT be confused with an H.264 frame on the shared binary
/// channel (the demux relies on the "RDXC" magic being distinct from a NAL start code and "RDXF").
/// </summary>
public class DesktopCursorBinaryEnvelopeTests
{
    [Theory]
    [InlineData(0, 0, true, 0L, 0L)]
    [InlineData(-1920, -100, true, 42L, 123456789L)]   // monitor with a negative virtual-desktop origin
    [InlineData(5119, 1439, false, 7L, -1L)]
    public void WriteThenTryRead_RoundTrips(int x, int y, bool visible, long shapeSerial, long streamSerial)
    {
        var packet = DesktopCursorBinaryEnvelope.Write(x, y, visible, shapeSerial, streamSerial);

        Assert.Equal(DesktopCursorBinaryEnvelope.Size, packet.Length);
        Assert.True(DesktopCursorBinaryEnvelope.HasMagic(packet));

        Assert.True(DesktopCursorBinaryEnvelope.TryRead(
            packet, out var rx, out var ry, out var rvis, out var rshape, out var rstream));
        Assert.Equal(x, rx);
        Assert.Equal(y, ry);
        Assert.Equal(visible, rvis);
        Assert.Equal(shapeSerial, rshape);
        Assert.Equal(streamSerial, rstream);
    }

    [Fact]
    public void TryRead_RejectsH264AndFrameEnvelopeAndShortBuffers()
    {
        // A raw Annex-B NAL start code must NOT be mistaken for a cursor packet.
        var nal = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42 };
        Assert.False(DesktopCursorBinaryEnvelope.HasMagic(nal));
        Assert.False(DesktopCursorBinaryEnvelope.TryRead(nal, out _, out _, out _, out _, out _));

        // The H.264 frame envelope magic ("RDXF") must not match the cursor magic ("RDXC").
        var rdxf = new byte[DesktopCursorBinaryEnvelope.Size];
        rdxf[0] = (byte)'R';
        rdxf[1] = (byte)'D';
        rdxf[2] = (byte)'X';
        rdxf[3] = (byte)'F';
        Assert.False(DesktopCursorBinaryEnvelope.HasMagic(rdxf));
        Assert.False(DesktopCursorBinaryEnvelope.TryRead(rdxf, out _, out _, out _, out _, out _));

        Assert.False(DesktopCursorBinaryEnvelope.TryRead(new byte[4], out _, out _, out _, out _, out _));
    }
}
