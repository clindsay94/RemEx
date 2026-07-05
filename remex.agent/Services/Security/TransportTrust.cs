using System;
using System.Net;
using System.Net.Sockets;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Classifies the network path a request arrived over so the pairing endpoints can decide whether
/// the channel is already mutually-authenticated and MITM-resistant — and therefore trustworthy
/// enough to relay the 6-digit pairing PIN automatically — or an untrusted LAN/internet path where
/// the PIN must keep its purpose as a genuine out-of-band secret and be entered by hand.
/// </summary>
/// <remarks>
/// This is the host-side mirror of the Android client's <c>TransportTrust</c>
/// (<c>remex.android/.../security/TransportTrust.kt</c>); both sides must agree on what counts as a
/// "trusted transport" or the PIN auto-fill silently fails on one end. "Trusted" means loopback
/// (same machine) or an active Tailscale/WireGuard tunnel. Tailscale assigns addresses from the
/// CGNAT range <c>100.64.0.0/10</c> (IPv4) and the <c>fd7a:115c:a1e0::/48</c> ULA prefix (IPv6);
/// such an address is only reachable through the WireGuard tunnel, so a request arriving on one
/// implies the tunnel is up and the peer has been authenticated by Tailscale.
/// Pure and static so it is unit-testable without a live connection.
/// </remarks>
public static class TransportTrust
{
    // First 6 bytes of the fd7a:115c:a1e0::/48 Tailscale IPv6 ULA prefix.
    private static readonly byte[] TailscaleV6Prefix = { 0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0 };

    /// <summary>True when <paramref name="ip"/> is an IPv4/IPv6 loopback address (same machine).</summary>
    public static bool IsLoopback(IPAddress? ip) => ip is not null && IPAddress.IsLoopback(ip);

    /// <summary>
    /// True when <paramref name="ip"/> falls in a Tailscale-assigned range: IPv4 <c>100.64.0.0/10</c>
    /// or IPv6 <c>fd7a:115c:a1e0::/48</c>. IPv4-mapped IPv6 addresses are unwrapped first.
    /// </summary>
    public static bool IsTailscaleAddress(IPAddress? ip)
    {
        if (ip is null)
        {
            return false;
        }

        // Kestrel may surface an IPv4 peer as an IPv4-mapped IPv6 address (::ffff:100.64.x.x).
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // 100.64.0.0/10: first octet 100, second octet 64..127.
            var octets = ip.GetAddressBytes();
            return octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            for (var i = 0; i < TailscaleV6Prefix.Length; i++)
            {
                if (bytes[i] != TailscaleV6Prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the active pairing PIN may be served to a caller. Allowed only when the caller is on
    /// loopback, or when both the caller (<paramref name="remote"/>) and the host-side address the
    /// caller reached (<paramref name="local"/>) are Tailscale addresses — i.e. the connection genuinely
    /// traversed the Tailscale tunnel. Requiring <paramref name="local"/> to also be Tailscale defeats a
    /// LAN attacker who spoofs a <c>100.64.x.x</c> source while connecting to the host's LAN IP (there the
    /// host-side address is the LAN IP, not a Tailscale one). Everything else must enter the PIN manually.
    /// </summary>
    public static bool IsTrustedForPinAutoFetch(IPAddress? remote, IPAddress? local) =>
        IsLoopback(remote) || (IsTailscaleAddress(remote) && IsTailscaleAddress(local));
}
