using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Remex.Agent.Services.Network;
using Xunit.Abstractions;

namespace Remex.Agent.Tests;

// NSD-5 (RemEx-lr9): mDNS must advertise only real interface addresses — never virtual ones
// (docker/virbr/tailscale/wg/veth), loopback, or link-local — otherwise phones on the real LAN
// receive unroutable addresses. This drives the real GetAdvertisableAddresses filter against the
// host's actual NICs. The loopback/link-local assertions are universal invariants; the Tailscale
// CGNAT (100.64.0.0/10) assertion meaningfully exercises the "tailscale" fragment filter on any box
// that has Tailscale up, without hardcoding a machine-specific address.
public sealed class MdnsInterfaceFilterTests
{
    private readonly ITestOutputHelper _output;

    public MdnsInterfaceFilterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GetAdvertisableAddresses_ExcludesVirtualLoopbackAndLinkLocal()
    {
        var method = typeof(MdnsAdvertisingService).GetMethod(
            "GetAdvertisableAddresses",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var advertised = ((IEnumerable<IPAddress>)method!.Invoke(null, null)!).ToList();

        // Record what the real filter chose on this box (visible with detailed logger verbosity).
        _output.WriteLine("Advertisable addresses: " + string.Join(", ", advertised));

        Assert.DoesNotContain(advertised, IPAddress.IsLoopback);
        Assert.DoesNotContain(advertised, a => a.IsIPv6LinkLocal);
        Assert.DoesNotContain(advertised, IsIPv4LinkLocal);
        // Tailscale (and any CGNAT) addresses live in 100.64.0.0/10 and must never be advertised.
        Assert.DoesNotContain(advertised, IsCarrierGradeNat);
    }

    private static bool IsIPv4LinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [169, 254, _, _];

    private static bool IsCarrierGradeNat(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [100, >= 64 and <= 127, _, _];
}
