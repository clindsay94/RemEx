using Remex.Core.Models;

namespace Remex.Core.Tests;

public class DesktopFrameEnvelopeTests
{
    [Fact]
    public void WrapAndRead_RoundTripsHeaderAndPayload()
    {
        byte[] payload = [1, 2, 3, 4, 5];

        var frame = DesktopFrameEnvelope.Wrap(
            payload,
            streamSerial: 42,
            sequence: 7,
            codec: DesktopCodecKind.Mjpeg,
            flags: DesktopFrameFlags.KeyFrame);

        var parsed = DesktopFrameEnvelope.TryRead(frame, out var header, out var parsedPayload);

        Assert.True(parsed);
        Assert.Equal(42, header.StreamSerial);
        Assert.Equal(7, header.Sequence);
        Assert.Equal(DesktopCodecKind.Mjpeg, header.Codec);
        Assert.Equal(DesktopFrameFlags.KeyFrame, header.Flags);
        Assert.Equal(payload, parsedPayload.ToArray());
    }

    [Fact]
    public void TryRead_RejectsInvalidMagic()
    {
        byte[] invalidFrame = [0, 1, 2, 3, 4, 5, 6, 7];

        var parsed = DesktopFrameEnvelope.TryRead(invalidFrame, out _, out _);

        Assert.False(parsed);
    }
}
