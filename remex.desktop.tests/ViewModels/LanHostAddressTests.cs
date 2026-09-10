using System;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Remex.Core.Services.Network;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// <see cref="ConnectionViewModel.LanHostAddress"/> exists for exactly one purpose: turn the
/// loopback address this PC's UI connects on into the LAN address the user types into their phone.
/// If it fails to recognise a loopback form it falls through to returning the address unchanged —
/// a LOOPBACK URL offered as a pairing address, which a phone can never reach, with no error to
/// explain why (RemEx-eskd).
///
/// BE ACCURATE ABOUT THE BLAST RADIUS, in both directions. As of this commit THIS PROPERTY has no
/// consumer — nothing binds it and no code reads it — so nobody hit the bug here; RemEx-19al asks
/// whether to wire it up or delete it. But the defect itself was NOT latent: the same predicate,
/// copied, sat in <c>GenerateQrCodeAsync</c>, whose command IS bound in ConnectionView and
/// SettingsView, and it put the loopback host straight into the pairing QR payload the phone
/// scans. Both were fixed together under RemEx-eskd; this file covers the property, and
/// <see cref="QrPairingHostSubstitutionTests"/> covers the shipping one.
///
/// The forms that were missed both come from the predicate being three literal strings:
/// <c>Uri.Host</c> returns the BRACKETED <c>"[::1]"</c> for an IPv6 literal, so that arm never
/// matched anything, and all of <c>127.0.0.0/8</c> is loopback to the host while only
/// <c>127.0.0.1</c> matched here.
///
/// THE ASSERTIONS ARE DELIBERATELY MACHINE-INDEPENDENT. The property substitutes this machine's LAN
/// IPv4, which a test cannot know and which is null on a host with no suitable adapter. So rather
/// than pin a value, these pin the PROPERTY THAT MATTERS: whatever comes back for a loopback input
/// must not itself be loopback. That holds on a build agent with no LAN address (null) and on a
/// developer machine (a real LAN URL) alike, and it still fails on the bug — which returned the
/// loopback URI unchanged.
/// </summary>
public class LanHostAddressTests : IDisposable
{
    private readonly ConnectionViewModel _viewModel;

    public LanHostAddressTests()
    {
        _viewModel = new ConnectionViewModel(
            new Mock<IMdnsDiscoveryService>().Object, null, new Mock<ILogger<ConnectionViewModel>>().Object);
    }

    public void Dispose() => _viewModel.Dispose();

    [Theory]
    [InlineData("wss://localhost:5005/ws")]
    [InlineData("wss://LOCALHOST:5005/ws")]
    [InlineData("wss://127.0.0.1:5005/ws")]
    [InlineData("wss://127.0.0.2:5005/ws")]
    [InlineData("wss://[::1]:5005/ws")]
    public void ALoopbackHostIsNeverHandedBackAsThePairingAddress(string hostAddress)
    {
        _viewModel.HostAddress = hostAddress;

        var lan = _viewModel.LanHostAddress;

        // A machine with no LAN IPv4 has nothing to substitute, and the property answers null. This
        // branch is NOT falsifiable here and is not pretended to be: every input in the theory is
        // loopback by construction, so any assertion about the input would be a tautology. An
        // earlier revision of this test did exactly that and claimed it closed the gap; it did not.
        //
        // The teeth against THIS defect do not depend on the branch. The bug returned the loopback
        // URL unchanged — non-null — so it never reached here and always hit the assertion below,
        // on any machine. What is genuinely uncovered is a future regression that returns null when
        // a LAN address WAS available; QrPairingHostSubstitutionTests covers that shape
        // deterministically by injecting the address instead of discovering it.
        if (lan is null) return;

        Uri.TryCreate(lan, UriKind.Absolute, out var uri).Should().BeTrue(
            "the pairing address is shown to the user to type into a phone, so it must be a real URI");
        uri!.IsLoopback.Should().BeFalse(
            $"'{hostAddress}' is loopback, so the phone pairing address must be substituted for the " +
            "LAN address; handing back a loopback URL gives the user something that can never connect");
    }

    [Fact]
    public void ANonLoopbackHostIsReturnedUnchanged()
    {
        // The substitution must apply ONLY to loopback. A user who typed a real LAN or remote address
        // already has the address their phone needs, and rewriting it would send them somewhere else.
        const string lanAddress = "wss://192.168.1.50:5005/ws";
        _viewModel.HostAddress = lanAddress;

        _viewModel.LanHostAddress.Should().Be(lanAddress);
    }

    [Fact]
    public void TheBracketedIpv6LiteralIsWhatUriActuallyReports()
    {
        // Pins the platform fact the bug rested on, so nobody re-derives it from memory: matching
        // Uri.Host against the string "::1" cannot ever succeed, because Uri.Host keeps the brackets.
        new Uri("wss://[::1]:5005/ws").Host.Should().Be("[::1]");
        new Uri("wss://[::1]:5005/ws").IsLoopback.Should().BeTrue();
    }

    [Fact]
    public void AnUnparseableHostAddressYieldsNullRatherThanThrowing()
    {
        // The property is read from a binding on every HostAddress change, including mid-typing.
        _viewModel.HostAddress = "not a uri";

        _viewModel.LanHostAddress.Should().BeNull();
    }
}
