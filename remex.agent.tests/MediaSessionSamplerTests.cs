using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Media;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the media sampler publishes a reading only when it actually changes (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// THE "ONLY ON CHANGE" RULE IS THE FEATURE, NOT AN OPTIMISATION. The gate behind this defines
/// "already sent" as REFERENCE equality, so publishing an equal-but-new record every second would
/// wake every parked client stream and push an identical <c>media_state</c> down every socket, once a
/// second, forever — a phone sitting on the Remote Control screen would receive a steady trickle of
/// messages saying nothing had happened. A reviewer cannot see that from the diff, because the wrong
/// version and the right one differ by one <c>!=</c>.
/// </para>
/// <para>
/// WHAT THESE DO NOT COVER, since the sampler is only half the path: they exercise the loop and the
/// gate, not the platform reads. <c>WindowsMediaSessionReader</c> needs a live SMTC session and
/// <c>LinuxMediaSessionReader</c> needs a session bus with a player on it, so both are verified from a
/// real machine rather than here. The seam is <see cref="IMediaSessionReader"/> and that is the point
/// of its existing.
/// </para>
/// </remarks>
public class MediaSessionSamplerTests
{
    /// <summary>Hands out a scripted sequence of readings, then repeats the last one forever.</summary>
    private sealed class ScriptedReader(params MediaPlaybackState[] script) : IMediaSessionReader
    {
        private int _index = -1;

        public bool IsSupported { get; init; } = true;

        /// <summary>How many times the sampler asked. Pins that the loop is actually running.</summary>
        public int Reads => Volatile.Read(ref _index) + 1;

        public Task<MediaPlaybackState> ReadAsync(CancellationToken ct)
        {
            var next = Interlocked.Increment(ref _index);
            return Task.FromResult(script[Math.Min(next, script.Length - 1)]);
        }
    }

    private static MediaPlaybackState Playing(string? title = null)
        => new() { Status = MediaPlaybackStatus.Playing, Title = title };

    private static MediaSessionBackgroundService NewSampler(IMediaSessionReader reader)
        => new(reader, NullLogger<MediaSessionBackgroundService>.Instance);

    /// <summary>Waits for a condition rather than for a duration, up to a generous ceiling.</summary>
    /// <remarks>
    /// The sampler's period is a real one-second timer, so a fixed sleep would either be flaky or slow.
    /// Polling a predicate is neither.
    /// </remarks>
    private static async Task<bool> Within(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task AnUnchangedReadingIsSampledAgainButNotPublishedAgain()
    {
        // THE BEAD. One reading, repeated forever: the loop must keep asking and stop publishing.
        var reader = new ScriptedReader(Playing("Same song"));
        var sampler = NewSampler(reader);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Within(() => sampler.Current is not null), "the first reading should publish");
            var first = sampler.Current;

            // Three more polls at a one-second period. If an equal reading republished, the gate would
            // hold a DIFFERENT instance by now even though the value never moved.
            Assert.True(await Within(() => reader.Reads >= 4), "the sampler should keep polling");

            Assert.Same(first, sampler.Current);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AChangedReadingPublishesANewSnapshot()
    {
        var reader = new ScriptedReader(
            Playing("First"),
            new MediaPlaybackState { Status = MediaPlaybackStatus.Paused, Title = "First" });
        var sampler = NewSampler(reader);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Within(() => sampler.Current?.Status == MediaPlaybackStatus.Paused),
                "pausing at the PC must reach the gate");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnUnsupportedHostPublishesNothingAtAll()
    {
        // NOT EVEN AN "unknown". A host that cannot read says so once, in HostCapabilities, and then
        // stays quiet; a published Unknown would put a message on every socket to report a fact that
        // never changes and was already answered on connect.
        var reader = new ScriptedReader(Playing()) { IsSupported = false };
        var sampler = NewSampler(reader);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);
            Assert.Null(sampler.Current);
            Assert.Equal(0, reader.Reads);
            Assert.False(sampler.IsSupported);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AWaiterHoldingNothingGetsTheCurrentReadingImmediately()
    {
        // THE FIRST SEND FOR A CLIENT THAT CONNECTS MID-SONG. Without this, a phone joining while one
        // long album plays would wait for the next CHANGE — up to a whole track — with the icon wrong
        // the entire time, which is the complaint this feature answers.
        var reader = new ScriptedReader(Playing("Already going"));
        var sampler = NewSampler(reader);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Within(() => sampler.Current is not null));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var state = await sampler.WaitForNextAsync(null, cts.Token);

            Assert.Equal(MediaPlaybackStatus.Playing, state.Status);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void TheStatusTokensAreTheOnesTheClientParses()
    {
        // A STRING CONTRACT WITH NO COMPILER BETWEEN THE TWO ENDS. Kotlin's
        // MediaPlaybackStatus.fromToken matches these literals; renaming one here compiles cleanly on
        // both sides and degrades every phone to UNKNOWN, which renders as the old static triangle —
        // i.e. exactly the bug this feature fixed, silently restored.
        Assert.Equal("playing", MediaPlaybackStatus.Playing);
        Assert.Equal("paused", MediaPlaybackStatus.Paused);
        Assert.Equal("stopped", MediaPlaybackStatus.Stopped);
        Assert.Equal("none", MediaPlaybackStatus.None);
        Assert.Equal("unknown", MediaPlaybackStatus.Unknown);
    }

    [Fact]
    public void ADefaultStateIsUnknownRatherThanPlayingOrNone()
    {
        // The wire default has to be the one that renders as "say nothing". A record whose Status
        // defaulted to "" would parse on the phone as UNKNOWN too, but a default of "none" would have
        // an idle PC claim a reading it never took.
        Assert.Equal(MediaPlaybackStatus.Unknown, new MediaPlaybackState().Status);
    }

    [Fact]
    public void ReadingsAreComparedByValue()
    {
        // The sampler's change test is `reading != lastPublished`. That is only correct while this
        // holds — turn MediaPlaybackState into a class and every poll republishes.
        var a = new MediaPlaybackState { Status = MediaPlaybackStatus.Playing, Title = "x", Artist = "y" };
        var b = new MediaPlaybackState { Status = MediaPlaybackStatus.Playing, Title = "x", Artist = "y" };

        Assert.Equal(a, b);
        Assert.NotSame(a, b);
        Assert.NotEqual(a, a with { Title = "z" });
    }
}
