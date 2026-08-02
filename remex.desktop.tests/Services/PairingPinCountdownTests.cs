using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins when a displayed pairing PIN stops being worth typing (RemEx-scwy).
/// </summary>
/// <remarks>
/// The failure that matters is a PIN presented as valid after it is dead. The user types six digits,
/// gets a rejection that names no cause, and the PIN is screen-only so there is nothing to re-read.
/// Pairing is the one flow where a confusing failure sends someone to support.
/// </remarks>
public class PairingPinCountdownTests
{
    private static readonly DateTimeOffset Issued = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ThreeMinutes = TimeSpan.FromMinutes(3);

    private static PairingPinStatus At(TimeSpan sinceIssue) =>
        PairingPinCountdown.Evaluate(Issued, ThreeMinutes, Issued + sinceIssue);

    [Fact]
    public void AFreshPinIsValidWithItsFullWindow()
    {
        var status = At(TimeSpan.Zero);

        Assert.Equal(PairingPinState.Valid, status.State);
        Assert.Equal(ThreeMinutes, status.Remaining);
    }

    [Fact]
    public void APinPastItsWindowIsExpired()
    {
        var status = At(ThreeMinutes + TimeSpan.FromSeconds(1));

        Assert.Equal(PairingPinState.Expired, status.State);
    }

    [Fact]
    public void ExpiryIsInclusive_ExactlyAtTheWindowIsAlreadyDead()
    {
        // The host stops accepting it at the boundary, so the UI must not show it as usable for the
        // instant either side. Off-by-one here produces a PIN that is rejected at the exact moment
        // the countdown reads zero, which looks like the host is broken.
        var status = At(ThreeMinutes);

        Assert.Equal(PairingPinState.Expired, status.State);
    }

    [Fact]
    public void AnExpiredPinReportsZeroRemainingRatherThanANegative()
    {
        // A negative would format as something like "-00:14 left", which reads as a bug rather than
        // as an expiry.
        var status = At(ThreeMinutes + TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.Zero, status.Remaining);
    }

    [Fact]
    public void TheWarningArrivesWithEnoughTimeToActOnIt()
    {
        // The threshold is chosen from the TASK: read six digits off one screen, type them into a
        // phone, possibly one-handed. A warning with three seconds left tells the user something
        // they can no longer do anything about.
        Assert.Equal(PairingPinState.Valid, At(ThreeMinutes - TimeSpan.FromSeconds(16)).State);
        Assert.Equal(PairingPinState.ExpiringSoon, At(ThreeMinutes - TimeSpan.FromSeconds(15)).State);
        Assert.Equal(PairingPinState.ExpiringSoon, At(ThreeMinutes - TimeSpan.FromSeconds(1)).State);
    }

    [Fact]
    public void AClockThatStepsBackwardsCannotGiveThePinMoreLifeThanItHas()
    {
        // THE ONE DIRECTION THAT MUST NEVER HAPPEN. An NTP correction can move a wall clock
        // backwards, making elapsed negative and remaining LONGER than the validity window - and a
        // user acts on extra time they have been shown. Clamping elapsed to zero caps remaining at
        // the window instead.
        var status = PairingPinCountdown.Evaluate(Issued, ThreeMinutes, Issued - TimeSpan.FromMinutes(10));

        Assert.Equal(PairingPinState.Valid, status.State);
        Assert.Equal(ThreeMinutes, status.Remaining);
        Assert.True(status.Remaining <= ThreeMinutes);
    }

    [Fact]
    public void AZeroOrNegativeValidityWindowIsExpiredRatherThanUnlimited()
    {
        // Treating it as "no limit" would present a PIN the host will refuse, and a zero window
        // almost certainly means the caller read it from a field that was never populated.
        Assert.Equal(PairingPinState.Expired,
            PairingPinCountdown.Evaluate(Issued, TimeSpan.Zero, Issued).State);
        Assert.Equal(PairingPinState.Expired,
            PairingPinCountdown.Evaluate(Issued, TimeSpan.FromSeconds(-30), Issued).State);
    }

    [Fact]
    public void RemainingIsNeverNegativeAtAnyPointAcrossTheWholeLifetime()
    {
        // Swept rather than sampled: a formatter that receives a negative renders a minus sign to
        // the user, and the boundary is the easiest place to get an inequality backwards.
        for (var second = -60; second <= 300; second++)
        {
            var status = At(TimeSpan.FromSeconds(second));

            Assert.True(status.Remaining >= TimeSpan.Zero, $"negative remaining at {second}s");
            Assert.True(status.Remaining <= ThreeMinutes, $"remaining exceeded the window at {second}s");
        }
    }

    [Fact]
    public void AnExpiredPinIsReplacedRatherThanGreyedOut()
    {
        // Greying it out leaves six digits on screen, and a user looking at their phone will type
        // them - the visual treatment carries no information to someone not looking at the PC. The
        // surface must show a "get a new PIN" action instead, which is the only thing that helps.
        Assert.True(PairingPinCountdown.ShouldDisplayPin(PairingPinState.Valid));
        Assert.True(PairingPinCountdown.ShouldDisplayPin(PairingPinState.ExpiringSoon));
        Assert.False(PairingPinCountdown.ShouldDisplayPin(PairingPinState.Expired));
    }
}
