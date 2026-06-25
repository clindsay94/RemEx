using System.Net;
using Remex.Agent.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for <see cref="TransportTrust"/> — the host-side gate that decides whether the active pairing
/// PIN may be auto-served to a caller (RemEx-i9e). Mirrors the Android client's Tailscale trust model so
/// the PIN auto-fill works end-to-end over a tunnel while staying closed on plain LAN/internet.
/// </summary>
public class TransportTrustTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    public void IsLoopback_TrueForLoopback(string ip)
    {
        Assert.True(TransportTrust.IsLoopback(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("192.168.1.10")]
    public void IsLoopback_FalseForNonLoopback(string ip)
    {
        Assert.False(TransportTrust.IsLoopback(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsLoopback_FalseForNull()
    {
        Assert.False(TransportTrust.IsLoopback(null));
    }

    [Theory]
    [InlineData("100.64.0.1")]      // bottom of 100.64.0.0/10
    [InlineData("100.100.5.5")]     // middle
    [InlineData("100.127.255.255")] // top of 100.64.0.0/10
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("fd7a:115c:a1e0:ab12:cd34::9")]
    public void IsTailscaleAddress_TrueInRange(string ip)
    {
        Assert.True(TransportTrust.IsTailscaleAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("100.63.255.255")]  // just below the /10
    [InlineData("100.128.0.0")]     // just above the /10
    [InlineData("99.64.0.1")]       // wrong first octet
    [InlineData("192.168.1.10")]    // private LAN
    [InlineData("8.8.8.8")]         // public
    [InlineData("127.0.0.1")]       // loopback is not Tailscale
    [InlineData("fd7a:115c:a1e1::1")] // one bit off the /48 prefix
    [InlineData("fe80::1")]         // link-local
    public void IsTailscaleAddress_FalseOutOfRange(string ip)
    {
        Assert.False(TransportTrust.IsTailscaleAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsTailscaleAddress_FalseForNull()
    {
        Assert.False(TransportTrust.IsTailscaleAddress(null));
    }

    [Fact]
    public void IsTailscaleAddress_TrueForIPv4MappedTailscale()
    {
        // Kestrel may surface an IPv4 peer as ::ffff:100.64.x.x.
        Assert.True(TransportTrust.IsTailscaleAddress(IPAddress.Parse("::ffff:100.64.0.5")));
    }

    [Theory]
    // Loopback caller is always trusted, regardless of local address.
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::1", "::1", true)]
    // Both ends Tailscale => the connection genuinely traversed the tunnel.
    [InlineData("100.64.0.5", "100.64.0.9", true)]
    [InlineData("fd7a:115c:a1e0::5", "fd7a:115c:a1e0::9", true)]
    // Tailscale source but host-side address is LAN => spoof case, rejected.
    [InlineData("100.64.0.5", "192.168.1.10", false)]
    // Plain LAN / public => rejected.
    [InlineData("192.168.1.10", "192.168.1.20", false)]
    [InlineData("8.8.8.8", "203.0.113.7", false)]
    public void IsTrustedForPinAutoFetch(string remote, string local, bool expected)
    {
        Assert.Equal(
            expected,
            TransportTrust.IsTrustedForPinAutoFetch(IPAddress.Parse(remote), IPAddress.Parse(local)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IsTrustedForPinAutoFetch_FalseWhenEitherEndNull(bool remoteNull, bool localNull)
    {
        var remote = remoteNull ? null : IPAddress.Parse("100.64.0.5");
        var local = localNull ? null : IPAddress.Parse("100.64.0.9");
        Assert.False(TransportTrust.IsTrustedForPinAutoFetch(remote, local));
    }
}
