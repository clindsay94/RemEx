using System.IO;
using System.Runtime.CompilerServices;
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
        var args = FFmpegH264Encoder.BuildEncoderArgs(codec, 1920, 1080, 1920, 1080, 60, 24, forProbe: false);

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
        var args = FFmpegH264Encoder.BuildEncoderArgs(codec, 1920, 1080, 1920, 1080, 60, 24, forProbe: false);

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
        var probe = FFmpegH264Encoder.BuildEncoderArgs("h264_amf", 1920, 1080, 1920, 1080, 60, 24, forProbe: true);

        Assert.Contains("-header_spacing 60", probe);
    }

    // ── Capture-side downscale moved into ffmpeg (RemEx-evzv) ────────────────────
    //
    // The capture backend only has a fast path when it is NOT resampling; asking it to shrink drops
    // it into a GDI+ bilinear DrawImage measured at 20.7 ms/frame for 2560x1440 -> 0.6 on the
    // reference box, single-threaded on the capture thread. So the handler now captures full-res and
    // ffmpeg scales. These pin the two halves of that contract: the input size on the pipe, and the
    // filter that gets the frame down to the encoded size.

    [Fact]
    public void BuildEncoderArgs_InputSizeIsTheCaptureSize_NotTheEncodedSize()
    {
        var args = FFmpegH264Encoder.BuildEncoderArgs(
            "h264_nvenc", 2560, 1440, 1536, 864, 60, 24, forProbe: false);

        // -s describes what arrives on the pipe. If this followed the ENCODED size the rawvideo
        // stream would desync and nvenc would emit zero frames.
        Assert.Contains("-s 2560x1440", args);
        Assert.DoesNotContain("-s 1536x864", args);
    }

    /// <summary>
    /// EVERY codec must scale, not just the ones someone remembered.
    /// </summary>
    /// <remarks>
    /// This list is the full set the encoder can select. A branch that forgets the filter does not
    /// fail loudly — it encodes at the CAPTURE resolution, so the stream silently comes out the wrong
    /// size on whichever GPUs select that codec. h264_nvenc_bgra, h264_qsv and h264_amf were each
    /// missed in the first draft of this change and caught only because the list is exhaustive.
    /// </remarks>
    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_nvenc_bgra")]
    [InlineData("libx264")]
    [InlineData("h264_vaapi")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    public void BuildEncoderArgs_ScalesToTheEncodedSize_WhenCaptureIsLarger(string codec)
    {
        var args = FFmpegH264Encoder.BuildEncoderArgs(
            codec, 2560, 1440, 1536, 864, 60, 24, forProbe: false);

        Assert.Contains("scale=1536:864:flags=bilinear", args);
    }

    [Fact]
    public void BuildEncoderArgs_VaapiKeepsItsUploadChain_WithTheScaleAhead()
    {
        // VAAPI already had a -vf; the scale must JOIN that chain. A second -vf would silently
        // override the first, and losing format=nv12,hwupload means the frame never reaches the GPU.
        var args = FFmpegH264Encoder.BuildEncoderArgs(
            "h264_vaapi", 2560, 1440, 1536, 864, 60, 24, forProbe: false);

        Assert.Contains("-vf scale=1536:864:flags=bilinear,format=nv12,hwupload", args);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(args, @"-vf "));
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_nvenc_bgra")]
    [InlineData("libx264")]
    [InlineData("h264_vaapi")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    public void BuildEncoderArgs_OmitsTheScaleFilter_WhenNoResampleIsNeeded(string codec)
    {
        var args = FFmpegH264Encoder.BuildEncoderArgs(
            codec, 1920, 1080, 1920, 1080, 60, 24, forProbe: false);

        Assert.DoesNotContain("scale=", args);
    }

    [Fact]
    public void BuildEncoderArgs_ProbeUsesTheSameScaleChainAsTheStream()
    {
        // The probe must exercise the real filter chain, or an unsupported filter fails the live
        // stream instead of failing the probe and falling through to the next codec.
        var probe = FFmpegH264Encoder.BuildEncoderArgs(
            "h264_nvenc", 2560, 1440, 1536, 864, 60, 24, forProbe: true);

        Assert.Contains("scale=1536:864:flags=bilinear", probe);
        Assert.Contains("-s 2560x1440", probe);
    }

    /// <summary>
    /// The probe must write the INPUT frame size, like every other producer.
    /// </summary>
    /// <remarks>
    /// This is a source-text assertion because the behaviour needs a real ffmpeg to observe, and it
    /// exists because the first draft of RemEx-evzv got it wrong in exactly the way no arg-string test
    /// could see: <c>BuildEncoderArgs</c> correctly emitted <c>-s 2560x1440</c> while
    /// <c>RunEncoderProbe</c> still wrote <c>width * height * 4</c>. Verified against real ffmpeg
    /// during review: the short frame is rejected with "packet size 5308416 &lt; expected frame_size
    /// 14745600", the probe fails for EVERY codec, and H.264 falls back to MJPEG permanently — with
    /// no error surfaced anywhere. Six passing arg-string tests said nothing about it.
    /// </remarks>
    [Fact]
    public void RunEncoderProbe_SizesTheBlackFrameFromTheInputDimensions()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.agent", "Services", "RemoteDesktop", "FFmpegH264Encoder.cs"));

        Assert.Contains("long remaining = (long)inputWidth * inputHeight * 4;", source);
        Assert.DoesNotContain("long remaining = (long)width * height * 4;", source);
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
