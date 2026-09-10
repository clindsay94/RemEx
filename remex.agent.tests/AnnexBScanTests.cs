using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Cover for the Annex-B start-code scans after they were replaced with vectorized span searches
/// (RemEx-rpu2).
/// </summary>
/// <remarks>
/// <para>
/// Both were hand-rolled byte loops running over every access unit the encoder produces — 120+ a
/// second at the frame rates this targets, over buffers up to a megabyte. <c>IndexOf</c> over a span
/// searches on wide registers instead.
/// </para>
/// <para>
/// THE RISK IS FRAMING, NOT SPEED. The AUD scan decides where one access unit ends and the next
/// begins. Cut in the wrong place and the encoder emits access units that are not independently
/// decodable — the client shows tearing, blocking or a frozen picture, with no error anywhere,
/// because a malformed AU is still a perfectly well-formed WebSocket message. So these assert cut
/// POSITIONS and content, not that a search "found something".
/// </para>
/// <para>
/// One claim deliberately NOT made here: that these cover an "overlapping match" hazard. Annex-B
/// start codes cannot overlap — a match at i needs the byte at i+2 to be 1, which is exactly what a
/// match at i+1 or i+2 would need to be 0 — so how far the scanner advances after a rejected match
/// cannot change the answer, and no test can distinguish it. What DOES matter, and is covered, is
/// which pattern each scan looks for and that the NAL type is read from the low five bits.
/// </para>
/// </remarks>
public class AnnexBScanTests
{
    private const byte Idr = 0x65;      // NAL header: type 5, ref idc 3
    private const byte NonIdr = 0x41;   // type 1
    private const byte Aud = 0x09;      // type 9
    private const byte Sps = 0x67;      // type 7

    private static byte[] FourByte(byte nal, params byte[] payload) =>
        [0x00, 0x00, 0x00, 0x01, nal, .. payload];

    private static byte[] ThreeByte(byte nal, params byte[] payload) =>
        [0x00, 0x00, 0x01, nal, .. payload];

    // ── IndexOfAudStart: where access units get cut ───────────────────────────

    [Fact]
    public void TheFirstAudAfterTheStartIsFound()
    {
        // The shape the reader actually sees: an AU, then the next one's delimiter.
        byte[] stream = [.. FourByte(Aud), .. FourByte(NonIdr, 0xAA, 0xBB), .. FourByte(Aud)];

        // 5 bytes for the leading delimiter, 7 for the NAL and its two payload bytes.
        Assert.Equal(12, FFmpegH264Encoder.IndexOfAudStart(stream, 1));
    }

    [Fact]
    public void ScanningStartsAtTheGivenOffsetAndDoesNotRediscoverItself()
    {
        // The reader advances past each cut. Returning the same position twice would emit a
        // zero-length access unit forever.
        byte[] stream = [.. FourByte(Aud), .. FourByte(Aud), .. FourByte(Aud)];

        int first = FFmpegH264Encoder.IndexOfAudStart(stream, 1);
        int second = FFmpegH264Encoder.IndexOfAudStart(stream, first + 1);

        Assert.Equal(5, first);
        Assert.Equal(10, second);
        Assert.Equal(-1, FFmpegH264Encoder.IndexOfAudStart(stream, second + 1));
    }

    [Fact]
    public void AStartCodeIntroducingSomethingOtherThanAnAudIsNotACutPoint()
    {
        // THE DISCRIMINATING CASE. Every NAL is preceded by a start code; only the delimiter ends an
        // access unit. A scan that stopped at the first start code would cut mid-frame.
        byte[] stream = [.. FourByte(Aud), .. FourByte(Sps), .. FourByte(Idr), .. FourByte(Aud)];

        Assert.Equal(15, FFmpegH264Encoder.IndexOfAudStart(stream, 1));
    }

    [Fact]
    public void AThreeByteStartCodeIsNotAnAudBoundary()
    {
        // Four-byte only, on purpose: the encoder runs with -aud 1 and the reader frames on the long
        // form. Accepting the short one here would cut where the previous scanner did not, which is a
        // change to how the stream is framed rather than to how fast it is scanned.
        byte[] stream = [.. FourByte(Aud), .. ThreeByte(Aud), .. FourByte(NonIdr)];

        Assert.Equal(-1, FFmpegH264Encoder.IndexOfAudStart(stream[..9], 1));
    }

    [Fact]
    public void ATrailingStartCodeWithNoNalHeaderYetIsNotACutPoint()
    {
        // A read can split the stream mid-start-code. Cutting on a delimiter whose header byte has
        // not arrived would frame on a guess.
        byte[] truncated = [.. FourByte(Aud), 0x00, 0x00, 0x00, 0x01];

        Assert.Equal(-1, FFmpegH264Encoder.IndexOfAudStart(truncated, 1));
    }

    [Fact]
    public void AnEmptyOrTinyBufferIsHandled()
    {
        Assert.Equal(-1, FFmpegH264Encoder.IndexOfAudStart(System.Array.Empty<byte>(), 0));
        Assert.Equal(-1, FFmpegH264Encoder.IndexOfAudStart([0x00, 0x00], 0));
        Assert.Equal(-1, FFmpegH264Encoder.IndexOfAudStart(FourByte(Aud), 1));
    }

    // ── ContainsIdr: whether an access unit is independently decodable ─────────
    //
    // The straightforward cases — an IDR behind each start-code form, and a P-frame correctly
    // reported as not independently decodable — already live in FFmpegH264EncoderArgsTests
    // (ContainsIdr_*) and are deliberately not repeated here. What follows is only what those do not
    // reach.

    [Fact]
    public void AnIdrIsFoundAfterARunOfZeros()
    {
        // Zero runs are ordinary — padding and emulation-prevention both produce them — and the
        // start code sits at the END of one here, so the scan has to find a match that does not begin
        // at the first zero it sees.
        byte[] au = [0x00, 0x00, 0x00, 0x00, 0x00, 0x01, Idr, 0x11];

        Assert.True(FFmpegH264Encoder.ContainsIdr(au));
    }

    [Fact]
    public void AStartCodeWithNoHeaderByteIsNotAnIdr()
    {
        Assert.False(FFmpegH264Encoder.ContainsIdr([0x00, 0x00, 0x00, 0x01]));
        Assert.False(FFmpegH264Encoder.ContainsIdr([0x00, 0x00, 0x01]));
        Assert.False(FFmpegH264Encoder.ContainsIdr([]));
    }

    [Fact]
    public void TheNalTypeIsReadFromTheLowFiveBitsOnly()
    {
        // The upper three bits are nal_ref_idc and vary independently. Comparing the whole byte would
        // match only one of the four legal encodings of an IDR header.
        foreach (byte refIdc in new byte[] { 0x00, 0x20, 0x40, 0x60 })
        {
            byte header = (byte)(refIdc | 0x05);
            Assert.True(
                FFmpegH264Encoder.ContainsIdr([.. FourByte(header)]),
                $"header 0x{header:X2} is an IDR with nal_ref_idc {refIdc >> 5}");
        }
    }
}
