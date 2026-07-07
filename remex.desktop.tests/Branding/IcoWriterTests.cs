using FluentAssertions;
using Remex.Branding;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

public class IcoWriterTests
{
    [Fact]
    public void Build_WritesValidHeaderAndDirectory()
    {
        var frames = new List<(int, byte[])> { (16, new byte[] { 1, 2, 3 }), (256, new byte[] { 9, 9 }) };
        byte[] ico = IcoWriter.Build(frames);

        // ICONDIR: reserved=0, type=1 (icon), count=2
        BitConverter.ToUInt16(ico, 0).Should().Be(0);
        BitConverter.ToUInt16(ico, 2).Should().Be(1);
        BitConverter.ToUInt16(ico, 4).Should().Be(2);

        // Entry 0: width byte = 16
        ico[6].Should().Be(16);
        // Entry 1: width byte = 0 means 256
        ico[6 + 16].Should().Be(0);

        // Offsets: first image sits right after header (6) + 2 entries (32) = 38
        int offset0 = BitConverter.ToInt32(ico, 6 + 12);
        offset0.Should().Be(6 + 16 * 2);
        int len0 = BitConverter.ToInt32(ico, 6 + 8);
        len0.Should().Be(3);
        // Second image offset = 38 + 3
        int offset1 = BitConverter.ToInt32(ico, 6 + 16 + 12);
        offset1.Should().Be(6 + 16 * 2 + 3);
    }

    [Fact]
    public void Build_AppendsPayloadsInOrder()
    {
        byte[] ico = IcoWriter.Build(new List<(int, byte[])> { (16, new byte[] { 7, 7, 7 }) });
        ico.Skip(6 + 16).Take(3).Should().Equal(7, 7, 7);
    }
}
