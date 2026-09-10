using System.Net.NetworkInformation;

namespace Remex.Core.Services.Network;

/// <summary>
/// Finds this machine's primary MAC address, so the host can tell a paired phone what to wake
/// rather than the user reading it off a settings screen and typing it in.
/// </summary>
/// <remarks>
/// <para>
/// WAKE-ON-LAN WAS ALREADY BUILT ON BOTH ENDS (RemEx-izuj) and still needed the one setup step a
/// non-technical user cannot do: find their PC's MAC address and hand-type it into the phone. The PC
/// has always known it — this used to be a private method on a desktop view-model that only offered
/// copy-to-clipboard.
/// </para>
/// <para>
/// Lives in <c>Remex.Core</c> rather than the UI layer so the answer is the same headless as it is
/// with a window open, and so both the capability that ships it to the phone and the settings card
/// that displays it read one implementation. <c>NetworkInterface</c> is cross-platform, and this
/// assembly already links <c>System.Net.NetworkInformation</c> for
/// <see cref="WakeOnLanService"/> — so nothing new enters the NativeAOT surface.
/// </para>
/// </remarks>
public static class PrimaryNetworkAdapter
{
    /// <summary>MAC bytes in a standard EUI-48 address. Anything else is not an Ethernet-style NIC.</summary>
    private const int Eui48Length = 6;

    /// <summary>
    /// The primary adapter's MAC (colon-separated, upper-case) and interface name, or empty strings
    /// when no suitable adapter exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PREFERS WIRED, which is the point rather than a detail: Wake-on-LAN is overwhelmingly an
    /// Ethernet feature — most Wi-Fi adapters drop off the network entirely when the machine sleeps,
    /// so a Wi-Fi MAC is the one least likely to be wakeable. Loopback and tunnel adapters are
    /// excluded outright; a Tailscale or VPN interface has a MAC that nothing on the LAN can reach
    /// with a magic packet.
    /// </para>
    /// <para>
    /// Empty rather than throwing, and empty rather than a placeholder: the caller ships this to a
    /// phone, and a wrong-looking MAC is worse than an absent one — absent means "ask the user",
    /// while wrong means "silently fail to wake and give no reason".
    /// </para>
    /// </remarks>
    public static (string Mac, string Adapter) Find()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && n.GetPhysicalAddress().GetAddressBytes().Length == Eui48Length)
                .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .FirstOrDefault();

            if (nic is null) return (string.Empty, string.Empty);

            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            return (Format(bytes), nic.Name);
        }
        catch
        {
            // Enumerating interfaces can throw on a locked-down or transitioning network stack. A
            // host that cannot answer "what is my MAC" should still start and still serve everything
            // else, so this degrades to "no MAC known" rather than failing the capability handshake.
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Formats EUI-48 bytes as <c>AA:BB:CC:DD:EE:FF</c>.
    /// </summary>
    /// <remarks>
    /// Separated and upper-cased for the person who has to compare it against a router's DHCP table.
    /// The wire format does not care — <see cref="WakeOnLanService"/> strips separators before
    /// building the magic packet — so this is chosen entirely for the human.
    /// </remarks>
    internal static string Format(byte[] bytes) =>
        string.Join(":", bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
