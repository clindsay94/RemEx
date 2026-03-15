using System;
using System.Net;
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

        using var udpClient = new UdpClient();
        udpClient.EnableBroadcast = true;

        var broadcastEndpoint = new IPEndPoint(IPAddress.Parse(broadcastIp), port);

        await udpClient.SendAsync(magicPacket, magicPacket.Length, broadcastEndpoint);
    }
}
