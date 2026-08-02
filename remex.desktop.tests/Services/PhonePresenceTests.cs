using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the difference between "a phone is attached" and "the loopback link is up" (RemEx-porg).
/// </summary>
/// <remarks>
/// Every status dot on the PC binds the UI's own WebSocket to its embedded host, so a user with ZERO
/// phones paired sees a green "Connected". The two states are not merely different - they are almost
/// uncorrelated, because the loopback link is up essentially always.
/// </remarks>
public class PhonePresenceTests
{
    private static ClientSession Loopback(string address = "127.0.0.1") => new(address, "RemEx Desktop");
    private static ClientSession Phone(string address = "192.168.1.42", string? name = "Pixel 9") =>
        new(address, name);

    [Fact]
    public void TheLoopbackLinkAloneIsNoPhone()
    {
        // THE BUG, stated as a test. This is the state a user with nothing paired is in, and it
        // currently renders as a green "Connected".
        var status = PhonePresence.Evaluate([Loopback()]);

        Assert.Equal(PhonePresenceState.NoPhone, status.State);
        Assert.Equal(0, status.PhoneCount);
    }

    [Fact]
    public void ALoopbackLinkDoesNotInflateThePhoneCount()
    {
        // The realistic case: the desktop UI is always attached, so every count must exclude it or
        // one phone reads as two.
        var status = PhonePresence.Evaluate([Loopback(), Phone()]);

        Assert.Equal(PhonePresenceState.OnePhone, status.State);
        Assert.Equal(1, status.PhoneCount);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]     // anywhere in 127/8 is loopback
    [InlineData("::1")]
    [InlineData("127.0.0.1:5005")] // with a port
    [InlineData("[::1]:5005")]     // IPv6 bracket form with a port
    public void EveryShapeOfLoopbackIsRecognised(string address)
    {
        // A peer that arrives with its port attached, or in bracket form, must not slip through as
        // a phone just because the string looks unfamiliar.
        Assert.False(PhonePresence.IsPhone(new ClientSession(address, "RemEx Desktop")));
    }

    [Theory]
    [InlineData("192.168.1.42")]
    [InlineData("192.168.1.42:41234")]
    [InlineData("100.72.10.4")]              // Tailscale
    [InlineData("fe80::1c2d:3e4f:5a6b:7c8d")] // bare IPv6 literal, several colons, no brackets
    public void ARealPeerIsAPhone(string address)
    {
        Assert.True(PhonePresence.IsPhone(new ClientSession(address, "Pixel 9")));
    }

    [Fact]
    public void AnUnknownAddressIsNotCountedAsAPhone()
    {
        // FAIL-CLOSED, AND THIS IS THE DIRECTION THAT MATTERS. Counting an unknown session as a
        // phone reproduces the original bug - a confident "1 phone connected" with nothing
        // attached. Failing the other way merely under-reports, which the user can see is wrong,
        // because their phone is in their hand.
        Assert.False(PhonePresence.IsPhone(new ClientSession(null, "Pixel 9")));
        Assert.False(PhonePresence.IsPhone(new ClientSession("", "Pixel 9")));
        Assert.False(PhonePresence.IsPhone(new ClientSession("   ", "Pixel 9")));
        Assert.False(PhonePresence.IsPhone(new ClientSession("not-an-address", "Pixel 9")));
    }

    [Fact]
    public void TwoPhonesReportAsSeveralRatherThanAsOne()
    {
        var status = PhonePresence.Evaluate([Loopback(), Phone("192.168.1.42"), Phone("192.168.1.43")]);

        Assert.Equal(PhonePresenceState.SeveralPhones, status.State);
        Assert.Equal(2, status.PhoneCount);
    }

    [Fact]
    public void ANameIsOfferedOnlyWhenExactlyOnePhoneIsAttached()
    {
        // With several attached, naming one of them is arbitrary and reads as though it is the only
        // one - which is a worse error than showing no name at all.
        Assert.Equal("Pixel 9", PhonePresence.Evaluate([Phone(name: "Pixel 9")]).FirstDeviceName);
        Assert.Null(PhonePresence.Evaluate([Phone("192.168.1.42"), Phone("192.168.1.43")]).FirstDeviceName);
    }

    [Fact]
    public void APhoneThatHasNotIdentifiedItselfStillCounts()
    {
        // Presence and identity are separate facts. A phone whose name has not arrived yet is
        // still attached, and the header must say so rather than waiting for a label.
        var status = PhonePresence.Evaluate([Phone(name: null), Loopback()]);

        Assert.Equal(PhonePresenceState.OnePhone, status.State);
        Assert.Null(status.FirstDeviceName);
    }

    [Fact]
    public void ABlankNameIsTreatedAsNoNameRatherThanRenderedEmpty()
    {
        // PingPongHandler records DeviceConnected with Detail = string.Empty today, which is the
        // blank-subject bug in the activity feed. A blank must not become an empty label here too.
        Assert.Null(PhonePresence.Evaluate([Phone(name: "   ")]).FirstDeviceName);
    }

    [Fact]
    public void NoSessionsAtAllIsNoPhone()
    {
        Assert.Equal(PhonePresenceState.NoPhone, PhonePresence.Evaluate([]).State);
        Assert.Equal(PhonePresenceState.NoPhone, PhonePresence.Evaluate(null).State);
    }
}
