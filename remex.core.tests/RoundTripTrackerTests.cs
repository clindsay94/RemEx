using Remex.Core.Services;

namespace Remex.Core.Tests;

/// <summary>
/// Latency is measured, and clock artefacts do not become latency (RemEx-s2ksi).
/// </summary>
/// <remarks>
/// The ping stamped a timestamp and the host echoed it back with a comment saying it was echoing for
/// a consumer — and <c>RemexNativeClient.HandleMessage</c> had no Pong case at all, so the round trip
/// the stamp existed for was never computed.
/// </remarks>
public class RoundTripTrackerTests
{
    [Fact]
    public void NothingMeasuredYetIsNullRatherThanZero()
    {
        // "0 ms" reads as an impossibly good connection. Null says "not known", which is the truth
        // before the first pong and after a reset.
        Assert.Null(new RoundTripTracker().RoundTripMilliseconds);
    }

    [Fact]
    public void TheFirstSampleIsTakenOutright()
    {
        // Easing in from zero would show a latency nobody has, for several pings, at exactly the
        // moment a user is deciding whether the connection is any good.
        var tracker = new RoundTripTracker();

        Assert.True(tracker.Observe(40.0));

        Assert.Equal(40.0, tracker.RoundTripMilliseconds);
    }

    [Fact]
    public void OneSlowReplyMovesTheFigureWithoutSpikingIt()
    {
        // The point of smoothing: a single retransmit should not make the display claim the link got
        // five times worse, but a sustained change must still show.
        var tracker = new RoundTripTracker(smoothingSamples: 5.0);

        tracker.Observe(20.0);
        tracker.Observe(120.0);

        var afterSpike = tracker.RoundTripMilliseconds!.Value;
        Assert.True(afterSpike > 20.0 && afterSpike < 60.0, $"moved to {afterSpike}, expected a partial step");

        for (var i = 0; i < 20; i++) tracker.Observe(120.0);

        // APPROACHES rather than reaches, which is what exponential smoothing means. Each sample
        // closes a fifth of the gap, so after twenty it is within about a millisecond - asserting
        // equality to the decimal would be asserting a property this deliberately does not have.
        Assert.InRange(tracker.RoundTripMilliseconds!.Value, 118.0, 120.0);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(RoundTripTracker.MaximumPlausibleMilliseconds + 1)]
    public void AClockArtefactIsRefusedRatherThanAveragedIn(double sample)
    {
        // THE REASON THIS GUARD EXISTS, and it is not defensive. Both ends of the measurement come
        // from the SAME machine's wall clock, so an NTP correction landing between send and receive
        // corrupts exactly one sample and can make it negative. Averaging that in would move the
        // figure for every later sample; refusing it costs one ping.
        var tracker = new RoundTripTracker();
        tracker.Observe(30.0);

        Assert.False(tracker.Observe(sample));

        Assert.Equal(30.0, tracker.RoundTripMilliseconds);
        Assert.Equal(30.0, tracker.LastSampleMilliseconds);
        Assert.Equal(1, tracker.RefusedSamples);
    }

    [Fact]
    public void ARefusalBeforeAnyGoodSampleLeavesTheFigureUnknown()
    {
        // Not zero, and not the refused value. The connection has still told us nothing.
        var tracker = new RoundTripTracker();

        Assert.False(tracker.Observe(-5.0));

        Assert.Null(tracker.RoundTripMilliseconds);
        Assert.Null(tracker.LastSampleMilliseconds);
    }

    [Fact]
    public void AZeroMillisecondRoundTripIsAcceptedRatherThanRefused()
    {
        // Loopback and a coarse clock genuinely produce this. Refusing it as implausible would blank
        // the figure exactly where the connection is best.
        var tracker = new RoundTripTracker();

        Assert.True(tracker.Observe(0.0));
        Assert.Equal(0.0, tracker.RoundTripMilliseconds);
    }

    [Fact]
    public void ResettingForgetsTheLinkRatherThanCarryingItAcross()
    {
        // Carrying a latency figure across a reconnect would describe a link that no longer exists -
        // the same reasoning that makes the transfer rate estimator drop its average when a byte
        // counter goes backwards.
        var tracker = new RoundTripTracker();
        tracker.Observe(25.0);

        tracker.Reset();

        Assert.Null(tracker.RoundTripMilliseconds);
        Assert.Null(tracker.LastSampleMilliseconds);
    }
}
