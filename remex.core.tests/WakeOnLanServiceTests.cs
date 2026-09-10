using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Remex.Core.Services.Network;

namespace Remex.Core.Tests;

public class WakeOnLanServiceTests
{
    private readonly WakeOnLanService _service = new();

    /// <summary>
    /// WakeOnLanService enumerates real network interfaces (filtered to Up, non-loopback, non-virtual
    /// Ethernet/Wireless80211 adapters) and sends one UDP datagram per interface from a socket BOUND to
    /// that interface's own local address. A socket bound to a real NIC's address cannot route to
    /// 127.0.0.1 (that destination is only reachable via the loopback interface), so pointing the service
    /// straight at 127.0.0.1 makes every per-interface send fail and the service throws before a packet is
    /// ever observable. Empirically, sending to some OTHER local address (e.g. a Tailscale/VPN adapter's
    /// own IP) from a socket bound to a different adapter is not reliably delivered locally either — only
    /// sending an interface's own datagram to that same interface's own address is guaranteed.
    ///
    /// So this mirrors WakeOnLanService's own interface filter exactly, picking one of the SAME addresses
    /// the service will bind its sending socket to. Listening there and pointing the service at it captures
    /// a real packet, because that is then a self-send: bind address == destination address. When no
    /// interface matches (a fully isolated sandbox), the service itself falls back to an unbound client and
    /// 127.0.0.1 works there instead. Either way this is deterministic on both Windows and Linux with no
    /// assumption a broadcast actually succeeds, and no hardcoded interface name.
    /// </summary>
    private static IPAddress GetLoopbackReachableTargetAddress()
    {
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                             !ni.Description.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) &&
                             (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                              ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(ua => ua.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return candidate ?? IPAddress.Loopback;
        }
        catch (NetworkInformationException)
        {
            return IPAddress.Loopback;
        }
    }

    /// <summary>
    /// Captures one UDP datagram sent by the service and asserts it is a real WoL magic packet: 102 bytes,
    /// 6 bytes of 0xFF, then the target MAC repeated 16 times.
    /// </summary>
    private static async Task<byte[]> CaptureMagicPacketAsync(WakeOnLanService service, string macAddress)
    {
        var targetAddress = GetLoopbackReachableTargetAddress();
        using var listener = new UdpClient(new IPEndPoint(targetAddress, 0));
        var listenPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var receiveTask = listener.ReceiveAsync();
        await service.WakeAsync(macAddress, targetAddress.ToString(), listenPort);

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == receiveTask, "No UDP datagram arrived within the timeout.");

        var result = await receiveTask;
        return result.Buffer;
    }

    private static byte[] ExpectedMacBytes(string cleanedUpperHex) =>
        Enumerable.Range(0, 6)
            .Select(i => Convert.ToByte(cleanedUpperHex.Substring(i * 2, 2), 16))
            .ToArray();

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    public async Task WakeAsync_Accepts_Valid_MAC_Formats_And_Builds_A_Correct_Magic_Packet(string mac)
    {
        var packet = await CaptureMagicPacketAsync(_service, mac);

        // Standard WoL magic packet: 102 bytes total — 6 bytes of 0xFF, then the 6-byte MAC 16 times.
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal((byte)0xFF, b));

        var expectedMac = ExpectedMacBytes("AABBCCDDEEFF");
        for (var i = 1; i <= 16; i++)
        {
            var block = packet.Skip(i * 6).Take(6).ToArray();
            Assert.Equal(expectedMac, block);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB")]
    [InlineData("not-a-mac")]
    [InlineData("GG:HH:II:JJ:KK:LL")]
    public async Task WakeAsync_Rejects_Invalid_MAC(string mac)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _service.WakeAsync(mac));
    }

    [Fact]
    public async Task WakeAsync_Rejects_Invalid_BroadcastIp()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.WakeAsync("AA:BB:CC:DD:EE:FF", "not-an-ip"));
    }

    [Fact]
    public async Task WakeAsync_Accepts_The_Real_Default_BroadcastIp_Without_Throwing()
    {
        // Distinct from the loopback-capturing tests above: this pins that the real default
        // broadcast address ("255.255.255.255", used when no broadcastIp is passed) is accepted by
        // IPAddress.TryParse and does not throw during send, even though a sandboxed test host has no
        // guaranteed route to actually deliver it and so the datagram itself cannot be captured here.
        await _service.WakeAsync("AA:BB:CC:DD:EE:FF", "255.255.255.255");
    }

    [Fact]
    public async Task WakeAsync_Honors_An_Explicit_NonDefault_Port()
    {
        // The theory above always listens on an ephemeral port it hands back as the target port, so
        // it cannot tell "any port" from "the requested port" apart. This pins the port parameter
        // specifically: nothing arrives on a DIFFERENT port than the one requested.
        var targetAddress = GetLoopbackReachableTargetAddress();

        using var wrongPortListener = new UdpClient(new IPEndPoint(targetAddress, 0));
        var wrongPort = ((IPEndPoint)wrongPortListener.Client.LocalEndPoint!).Port;
        var wrongPortReceive = wrongPortListener.ReceiveAsync();

        using var rightPortListener = new UdpClient(new IPEndPoint(targetAddress, 0));
        var rightPort = ((IPEndPoint)rightPortListener.Client.LocalEndPoint!).Port;
        var rightPortReceive = rightPortListener.ReceiveAsync();

        await _service.WakeAsync("AA:BB:CC:DD:EE:FF", targetAddress.ToString(), rightPort);

        var completed = await Task.WhenAny(rightPortReceive, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == rightPortReceive, "No UDP datagram arrived on the requested port within the timeout.");

        Assert.False(wrongPortReceive.IsCompleted, "A datagram arrived on a port that was never requested.");
    }
}
