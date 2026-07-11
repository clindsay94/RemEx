using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

public sealed class FFmpegH264EncoderArgsTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(30)]
    [InlineData(45)]
    public void ComputeMaxRateBps_ClampsToSaneRange(int qp)
    {
        var maxRate = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 60, qp);

        Assert.InRange(maxRate, 2_000_000L, 60_000_000L);
    }

    [Fact]
    public void ComputeMaxRateBps_LowerQpYieldsHigherBudget()
    {
        var sharp = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 60, 16);
        var soft = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 60, 38);

        Assert.True(sharp > soft, "Lower QP (higher quality) should budget more bits than higher QP.");
    }

    [Fact]
    public void ComputeMaxRateBps_MonotonicWithFps()
    {
        var at60 = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 60, 24);
        var at120 = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 120, 24);

        Assert.True(at120 >= at60, "Doubling fps should not decrease the bitrate budget.");
    }

    [Fact]
    public void ComputeMaxRateBps_MonotonicWithResolution()
    {
        var small = FFmpegH264Encoder.ComputeMaxRateBps(1280, 720, 60, 24);
        var large = FFmpegH264Encoder.ComputeMaxRateBps(3840, 2160, 60, 24);

        Assert.True(large >= small, "A larger surface should not budget fewer bits than a smaller one.");
    }

    [Fact]
    public void ComputeMaxRateBps_ExtremeGeometryStaysWithinCeiling()
    {
        var maxRate = FFmpegH264Encoder.ComputeMaxRateBps(7680, 4320, 240, 16);

        Assert.Equal(60_000_000L, maxRate);
    }

    [Fact]
    public void ComputeMaxRateBps_TinyGeometryStaysAboveFloor()
    {
        var maxRate = FFmpegH264Encoder.ComputeMaxRateBps(64, 64, 5, 45);

        Assert.Equal(2_000_000L, maxRate);
    }

    [Fact]
    public void ComputeBufSizeBps_IsHalfOfMaxRate()
    {
        Assert.Equal(5_000_000L, FFmpegH264Encoder.ComputeBufSizeBps(10_000_000L));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(120, 120)]
    [InlineData(240, 120)]
    public void ComputeGop_ClampsBetween30And120(int fps, int expectedGop)
    {
        Assert.Equal(expectedGop, FFmpegH264Encoder.ComputeGop(fps));
    }

    [Fact]
    public void ContainsIdr_DetectsFourByteStartCodeIdr()
    {
        // AUD (nal type 9) followed by an IDR slice (nal type 5), 4-byte start codes.
        byte[] au = { 0x00, 0x00, 0x00, 0x01, 0x09, 0xF0, 0x00, 0x00, 0x00, 0x01, 0x65, 0xAA, 0xBB };

        Assert.True(FFmpegH264Encoder.ContainsIdr(au));
    }

    [Fact]
    public void ContainsIdr_DetectsThreeByteStartCodeIdr()
    {
        byte[] au = { 0x00, 0x00, 0x01, 0x09, 0xF0, 0x00, 0x00, 0x01, 0x65, 0xAA, 0xBB };

        Assert.True(FFmpegH264Encoder.ContainsIdr(au));
    }

    [Fact]
    public void ContainsIdr_NonIdrPFrameReturnsFalse()
    {
        // AUD followed by a non-IDR slice (nal type 1).
        byte[] au = { 0x00, 0x00, 0x00, 0x01, 0x09, 0xF0, 0x00, 0x00, 0x00, 0x01, 0x41, 0xAA, 0xBB };

        Assert.False(FFmpegH264Encoder.ContainsIdr(au));
    }

    [Fact]
    public void ContainsIdr_AudPrefixedIdrStillDetected()
    {
        // AUD + SPS (7) + PPS (8) + IDR slice (5) — a realistic IDR access unit.
        byte[] au =
        {
            0x00, 0x00, 0x00, 0x01, 0x09, 0xF0,
            0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x68, 0xCE,
            0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84,
        };

        Assert.True(FFmpegH264Encoder.ContainsIdr(au));
    }

    [Fact]
    public void ContainsIdr_EmptyOrTooShortReturnsFalse()
    {
        Assert.False(FFmpegH264Encoder.ContainsIdr(Array.Empty<byte>()));
        Assert.False(FFmpegH264Encoder.ContainsIdr(new byte[] { 0x00, 0x00 }));
    }
}
