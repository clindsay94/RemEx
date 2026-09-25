using System.Net;
using Remex.Agent.Services.Network;

namespace Remex.Agent.Tests;

/// <summary>
/// A NetworkAddressChanged that leaves the advertisable address set as it was must not goodbye and
/// re-register the mDNS profile (perf audit P3-46).
/// </summary>
public class MdnsReadvertiseSkipTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Wifi = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress V6 = IPAddress.Parse("fd00::20");

    [Fact]
    public void SameAddressesInADifferentOrder_AreTheSameSet()
        => Assert.True(MdnsAdvertisingService.SameAddressSet([Lan, Wifi, V6], [V6, Lan, Wifi]));

    [Fact]
    public void AnAddedAddress_IsAChange()
        => Assert.False(MdnsAdvertisingService.SameAddressSet([Lan], [Lan, Wifi]));

    [Fact]
    public void ARemovedAddress_IsAChange()
        => Assert.False(MdnsAdvertisingService.SameAddressSet([Lan, Wifi], [Lan]));

    [Fact]
    public void AReplacedAddress_IsAChange()
        => Assert.False(MdnsAdvertisingService.SameAddressSet([Lan], [Wifi]));

    [Fact]
    public void EqualAddressesFromSeparateInstances_CompareByValue()
        => Assert.True(MdnsAdvertisingService.SameAddressSet(
            [IPAddress.Parse("192.168.1.20")], [IPAddress.Parse("192.168.1.20")]));

    [Fact]
    public void TheDebounceCoversABurstButStaysShort()
    {
        // 1-2 s per the audit: long enough to swallow a DHCP/VPN burst, short enough that a real
        // NIC switch is re-advertised before a user notices discovery is stale.
        Assert.InRange(MdnsAdvertisingService.ReadvertiseDebounce, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }
}
