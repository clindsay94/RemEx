using System;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins perf P1-7 (RemEx-4j8ls): ffmpeg detection spawns <c>where</c>/<c>which</c> and can block for up
/// to 1.5s, and every remote-desktop connection builds two encoders (probe + stream). Detection must
/// therefore run once and be shared, not repeat per encoder instance.
///
/// Each test uses its own <see cref="FFmpegH264Encoder.FFmpegDetector"/> with a counting finder, so
/// nothing here touches the process-wide shared detector other tests' encoders use.
/// </summary>
public sealed class FFmpegDetectionCacheTests
{
    private const string FakeFfmpegPath = "/fake/bin/ffmpeg";

    [Fact]
    public void TwoEncoders_RunTheExecutableLookupOnlyOnce()
    {
        var lookups = 0;
        var detector = new FFmpegH264Encoder.FFmpegDetector(_ =>
        {
            Interlocked.Increment(ref lookups);
            return FakeFfmpegPath;
        });

        using var probeEncoder = new FFmpegH264Encoder(NullLogger.Instance, detector);
        using var streamEncoder = new FFmpegH264Encoder(NullLogger.Instance, detector);

        Assert.Equal(1, lookups);
        Assert.True(probeEncoder.IsAvailable);
        Assert.True(streamEncoder.IsAvailable);
    }

    [Fact]
    public void FoundPath_StaysCachedPastTheRetryWindow()
    {
        var lookups = 0;
        long now = 0;
        var detector = new FFmpegH264Encoder.FFmpegDetector(
            _ =>
            {
                lookups++;
                return FakeFfmpegPath;
            },
            () => now);

        Assert.Equal(FakeFfmpegPath, detector.GetResult().Path);
        now += FFmpegH264Encoder.FFmpegDetector.FailedDetectionRetryMs * 10;
        Assert.Equal(FakeFfmpegPath, detector.GetResult().Path);

        Assert.Equal(1, lookups);
    }

    [Fact]
    public void FailedLookup_IsCachedInsideTheWindow_AndRetriedAfterIt()
    {
        // The finder throws rather than returning null so the Windows common-install-folder fallback
        // (real File.Exists checks on this machine) cannot turn the verdict into a found path.
        var lookups = 0;
        long now = 0;
        var detector = new FFmpegH264Encoder.FFmpegDetector(
            _ =>
            {
                lookups++;
                throw new InvalidOperationException("lookup failed");
            },
            () => now);

        using (var first = new FFmpegH264Encoder(NullLogger.Instance, detector))
        using (var second = new FFmpegH264Encoder(NullLogger.Instance, detector))
        {
            Assert.False(first.IsAvailable);
            Assert.False(second.IsAvailable);
        }

        Assert.Equal(1, lookups);

        now += FFmpegH264Encoder.FFmpegDetector.FailedDetectionRetryMs;
        using var afterWindow = new FFmpegH264Encoder(NullLogger.Instance, detector);

        Assert.False(afterWindow.IsAvailable);
        Assert.Equal(2, lookups);
    }
}
