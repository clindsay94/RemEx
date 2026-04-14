using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Remex.Core.Services.Network;

public class WakeOnLanService : IWakeOnLanService
{
    public async Task WakeAsync(string macAddress, string broadcastIp = "255.255.255.255", int port = 9)
    {
        var cleanedMac = Regex.Replace(macAddress, "[: -]", "");
        if (cleanedMac.Length != 12)
        {
            throw new ArgumentException("Invalid MAC address format", nameof(macAddress));
        }

        byte[] macBytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            macBytes[i] = Convert.ToByte(cleanedMac.Substring(i * 2, 2), 16);
        }

        // Standard WoL Magic Packet format:
        // 6 bytes of 0xFF followed by 16 repetitions of the target MAC address
        byte[] magicPacket = new byte[102];
        for (int i = 0; i < 6; i++)
        {
            magicPacket[i] = 0xFF;
        }

        for (int i = 1; i <= 16; i++)
        {
            Buffer.BlockCopy(macBytes, 0, magicPacket, i * 6, 6);
        }

        if (!IPAddress.TryParse(broadcastIp, out var parsedBroadcast))
            throw new ArgumentException($"Invalid broadcast IP: {broadcastIp}", nameof(broadcastIp));

        var broadcastEndpoint = new IPEndPoint(parsedBroadcast, port);
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                         !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                         !ni.Description.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) &&
                         (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || 
                          ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));

        var tasks = new List<Task<bool>>();

        foreach (var ni in interfaces)
        {
            var ipProps = ni.GetIPProperties();
            foreach (var ip in ipProps.UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    tasks.Add(SendToEndpointAsync(ip.Address, broadcastEndpoint, magicPacket));
                }
            }
        }

        if (tasks.Count == 0)
        {
            // Fallback to default if no suitable interfaces found
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;
            await udpClient.SendAsync(magicPacket, magicPacket.Length, broadcastEndpoint);
        }
        else
        {
            var results = await Task.WhenAll(tasks);
            if (!results.Any(r => r))
                throw new InvalidOperationException("Failed to send magic packet: all network interfaces failed.");
        }
    }

    private static async Task<bool> SendToEndpointAsync(IPAddress localAddress, IPEndPoint broadcastEndpoint, byte[] magicPacket)
    {
        try
        {
            using var udpClient = new UdpClient(new IPEndPoint(localAddress, 0));
            udpClient.EnableBroadcast = true;
            await udpClient.SendAsync(magicPacket, magicPacket.Length, broadcastEndpoint);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
