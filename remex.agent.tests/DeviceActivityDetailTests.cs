using System.Linq;
using System.Reflection;
using Remex.Agent.Handlers;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The activity feed says WHICH device, and records departures as well as arrivals (RemEx-2xjv).
/// </summary>
/// <remarks>
/// The connected row recorded an empty detail, so the feed said something connected without saying
/// what — the session has carried the name since RemEx-xuyu and it was not there when that line was
/// written. And there was no DeviceDisconnected kind at all, so a feed of arrivals alone reads as
/// though every phone that ever connected is still attached.
/// </remarks>
public class DeviceActivityDetailTests
{
    private static string Describe(string? deviceName, string? clientId) =>
        (string)typeof(PingPongHandler)
            .GetMethod("DescribeDevice", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [deviceName, clientId])!;

    [Theory]
    [InlineData("Study Phone", "phone-a", "Study Phone")]
    [InlineData(null, "phone-a", "phone-a")]
    [InlineData("  ", "phone-a", "phone-a")]
    [InlineData("  Study Phone  ", "phone-a", "Study Phone")]
    public void ThePreferredNameWinsAndBlankIsNotAName(string? name, string? id, string expected)
        => Assert.Equal(expected, Describe(name, id));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    public void ItIsNEVERBlankEvenWithNothingToGoOn(string? name, string? id)
    {
        // The same rule PairedDeviceDisplayName follows (RemEx-nrsv): a row showing nothing is worse
        // than one showing a raw id, because an id at least says WHICH device and can be matched
        // against the paired list. Blank says a phone did something and refuses to say which.
        var described = Describe(name, id);

        Assert.False(string.IsNullOrWhiteSpace(described));
    }

    [Fact]
    public void TheFeedHasADepartureKindToPairWithTheArrival()
    {
        // A feed that records arrivals and not departures implies every phone that ever connected is
        // still attached. Asserted on the enum rather than on a rendered row because the recording
        // site is inside a live connection's teardown.
        var kinds = System.Enum.GetNames<Remex.Desktop.Services.ActivityKind>();

        Assert.Contains("DeviceConnected", kinds);
        Assert.Contains("DeviceDisconnected", kinds);

        // And it has a glyph of its own: the fallback bullet would make it indistinguishable from
        // every other unrecognised kind in the feed.
        var connected = new Remex.Desktop.Services.ActivityEntry
        { Kind = Remex.Desktop.Services.ActivityKind.DeviceConnected }.Glyph;
        var disconnected = new Remex.Desktop.Services.ActivityEntry
        { Kind = Remex.Desktop.Services.ActivityKind.DeviceDisconnected }.Glyph;
        Assert.NotEqual(connected, disconnected);
        Assert.NotEqual("•", disconnected);
    }
}
