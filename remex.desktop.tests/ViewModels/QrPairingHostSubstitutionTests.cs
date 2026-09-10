using System;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// <see cref="ConnectionViewModel.ResolvePhoneReachableHost"/> decides the host that goes into the
/// pairing QR payload the phone scans. This is the path where the loopback-recognition defect
/// ACTUALLY SHIPPED — <c>GenerateQrCodeCommand</c> is bound in both ConnectionView and SettingsView,
/// so a loopback form the predicate failed to recognise went into the payload unchanged and the user
/// scanned an address their phone can never reach, with no error to explain it (RemEx-eskd).
///
/// The two forms that were missed are both consequences of testing <c>Uri.Host</c> against literal
/// strings: <c>Uri.Host</c> returns the BRACKETED <c>"[::1]"</c> for an IPv6 literal, so the
/// <c>"::1"</c> arm never matched anything, and all of <c>127.0.0.0/8</c> is loopback while only
/// <c>127.0.0.1</c> was listed.
///
/// Unlike the sibling tests for <c>LanHostAddress</c>, these are FULLY DETERMINISTIC: the LAN
/// address is supplied by the caller, so nothing depends on the adapters of the machine running the
/// suite.
/// </summary>
public class QrPairingHostSubstitutionTests
{
    private const string LanIp = "192.168.1.50";

    [Theory]
    [InlineData("wss://localhost:5005/ws")]
    [InlineData("wss://LOCALHOST:5005/ws")]
    [InlineData("wss://127.0.0.1:5005/ws")]
    [InlineData("wss://127.0.0.2:5005/ws")]
    [InlineData("wss://[::1]:5005/ws")]
    public void EveryLoopbackFormIsSubstitutedForTheLanAddress(string hostAddress)
    {
        var host = ConnectionViewModel.ResolvePhoneReachableHost(new Uri(hostAddress), () => LanIp);

        host.Should().Be(
            LanIp,
            "a phone cannot reach {0}, so putting it in the pairing QR gives the user a code that " +
            "silently cannot work", hostAddress);
    }

    [Theory]
    [InlineData("wss://192.168.1.25:5005/ws", "192.168.1.25")]
    [InlineData("wss://my-pc.local:5005/ws", "my-pc.local")]
    [InlineData("wss://10.0.0.7:5005/ws", "10.0.0.7")]
    public void ANonLoopbackHostIsLeftAlone(string hostAddress, string expectedHost)
    {
        // Substituting here would be worse than the bug it fixes: the user typed an address their
        // phone can already reach, and rewriting it would point the phone somewhere else entirely.
        var host = ConnectionViewModel.ResolvePhoneReachableHost(new Uri(hostAddress), () => LanIp);

        host.Should().Be(expectedHost);
    }

    [Fact]
    public void WithNoLanAddressAvailableTheLoopbackHostIsKeptRatherThanBlanked()
    {
        // A machine with no usable LAN adapter has nothing better to offer. Keeping the original
        // host means the QR still encodes a well-formed address — useless to a phone, but the
        // alternative (an empty or null host) would produce a malformed payload, which is worse.
        var host = ConnectionViewModel.ResolvePhoneReachableHost(new Uri("wss://[::1]:5005/ws"), () => null);

        host.Should().Be("[::1]");
    }

    [Fact]
    public void TheLanAddressIsNotFetchedForANonLoopbackHost()
    {
        // Obtaining the LAN address opens a socket. This runs on the UI thread from a button
        // command, so a host that needs no substitution must not pay for it.
        var fetched = 0;

        ConnectionViewModel.ResolvePhoneReachableHost(
            new Uri("wss://192.168.1.25:5005/ws"), () => { fetched++; return LanIp; });

        fetched.Should().Be(0);
    }
}
