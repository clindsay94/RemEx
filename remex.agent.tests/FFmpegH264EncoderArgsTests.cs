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

    // Self-contained GOP keyframes (RemEx-vj7b): a decoder that joins (or is rebuilt) mid-stream
    // can only configure from a keyframe that carries in-band SPS/PPS. nvenc repeats them
    // implicitly for raw -f h264 output; these codecs need an explicit flag — losing one
    // reintroduces the "black until monitor switch" startup race.
    [Theory]
    [InlineData("libx264", "repeat-headers=1")]
    [InlineData("h264_amf", "-header_spacing 60")] // spacing == ComputeGop(60) so headers ride the IDRs
    [InlineData("h264_qsv", "-repeat_pps 1")]
    public void BuildEncoderArgs_RepeatsParameterSetsOnNaturalIdrs(string codec, string expectedFlag)
    {
        var args = FFmpegH264Encoder.BuildEncoderArgs(codec, 1920, 1080, 60, 24, forProbe: false);

        Assert.Contains(expectedFlag, args);
    }

    [Theory]
    [InlineData("h264_nvenc_bgra")]
    [InlineData("h264_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    [InlineData("h264_vaapi")]
    [InlineData("libx264")]
    public void BuildEncoderArgs_EveryCodecEmitsAccessUnitDelimiters(string codec)
    {
        // The stdout ReaderLoop splits encoded access units on AUD start codes; a codec argset
        // without AUD emission silently breaks frame framing.
        var args = FFmpegH264Encoder.BuildEncoderArgs(codec, 1920, 1080, 60, 24, forProbe: false);

        Assert.True(
            args.Contains("-aud 1") || args.Contains("aud=1"),
            $"No AUD flag in args for {codec}: {args}");
    }

    [Fact]
    public void BuildEncoderArgs_ProbeExercisesTheHeaderRepetitionFlags()
    {
        // The capability probe must run the exact encoder flags of the real stream, so an
        // unsupported header-repetition option fails the probe (silent fallback to the next
        // codec) instead of killing the live stream.
        var probe = FFmpegH264Encoder.BuildEncoderArgs("h264_amf", 1920, 1080, 60, 24, forProbe: true);

        Assert.Contains("-header_spacing 60", probe);
    }
}
