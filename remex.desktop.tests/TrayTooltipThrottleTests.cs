using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// The tray tooltip is rebuilt on a slow beat, not on every telemetry tick (RemEx-zcos item 3).
/// </summary>
/// <remarks>
/// It ran once a second forever, including while the window was minimised to the tray - which is
/// precisely when the UI thread should be idlest - to rebuild a string that is only ever read on
/// hover. The assignment was already cheap when the text had not changed, because the generated
/// <c>[ObservableProperty]</c> setter skips equal values; the waste was building the string to find
/// that out.
/// </remarks>
public class TrayTooltipThrottleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    [Fact]
    public void ATickInsideTheIntervalDoesNothing()
    {
        // THE BEAD. Telemetry arrives at 1 Hz; four ticks in five must do no work at all.
        Assert.False(ShellViewModel.ShouldRebuildTray(
            T0.AddSeconds(1), T0, connected: true, lastConnected: true, Interval));
    }

    [Fact]
    public void TheIntervalBoundaryRebuilds()
    {
        // Inclusive: `>` instead of `>=` would push every rebuild out by one tick for ever.
        Assert.True(ShellViewModel.ShouldRebuildTray(
            T0.AddSeconds(5), T0, connected: true, lastConnected: true, Interval));
    }

    [Fact]
    public void LosingTheConnectionRebuildsImmediately()
    {
        // **THE ONE CASE THE THROTTLE MUST NOT DELAY.** The readings are worth five seconds of
        // staleness; "Disconnected" is not. Without this a user who has just lost their PC could
        // hover and be told everything is fine.
        Assert.True(ShellViewModel.ShouldRebuildTray(
            T0.AddMilliseconds(50), T0, connected: false, lastConnected: true, Interval));
    }

    [Fact]
    public void RegainingTheConnectionAlsoRebuildsImmediately()
    {
        // Both directions, because a stale "Disconnected" after a reconnect is the same lie inverted.
        Assert.True(ShellViewModel.ShouldRebuildTray(
            T0.AddMilliseconds(50), T0, connected: true, lastConnected: false, Interval));
    }

    [Fact]
    public void TheFirstEverUpdateRebuilds()
    {
        // lastUtc starts at DateTime.MinValue, so the tooltip is populated on the first telemetry
        // rather than showing nothing for the first five seconds after launch.
        Assert.True(ShellViewModel.ShouldRebuildTray(
            T0, DateTime.MinValue, connected: false, lastConnected: false, Interval));
    }
}
