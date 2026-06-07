package com.clindsay94.remex.security

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities

/**
 * Classifies the network path to a host so the pairing flow can decide whether the
 * channel is already authenticated and MITM-resistant end-to-end — and therefore
 * trustworthy enough to auto-fetch the 6-digit pairing PIN over it — or an untrusted
 * LAN/internet path where the PIN must keep its purpose as a genuine out-of-band
 * secret and be entered manually.
 *
 * "Trusted transport" means: loopback (same device) or a Tailscale/WireGuard tunnel
 * (mutually authenticated, encrypted, no on-path attacker). On any other transport the
 * PIN is the only thing standing between an active network attacker and a successful
 * first-pair, so it must NOT be served to the client automatically.
 */
object TransportTrust {

    fun isLoopback(host: String): Boolean {
        val h = host.trim().lowercase()
        return h == "localhost" || h == "::1" || h.startsWith("127.")
    }

    /**
     * Tailscale assigns addresses from the CGNAT range 100.64.0.0/10 (IPv4) and the
     * fd7a:115c:a1e0::/48 ULA prefix (IPv6). Such a host is only reachable through the
     * WireGuard tunnel, so being able to reach it at all implies the tunnel is up and
     * the remote peer has been authenticated by Tailscale.
     */
    fun isTailscaleAddress(host: String): Boolean {
        val h = host.trim().lowercase()
        if (h.startsWith("fd7a:115c:a1e0:")) return true
        val octets = h.split(".")
        if (octets.size != 4) return false
        val first = octets[0].toIntOrNull() ?: return false
        val second = octets[1].toIntOrNull() ?: return false
        return first == 100 && second in 64..127
    }

    /** True when the device currently routes through a VPN transport (e.g. Tailscale). */
    fun isVpnActive(context: Context): Boolean {
        return try {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
                ?: return false
            val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
            caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        } catch (_: Exception) {
            false
        }
    }

    /**
     * Whether the pairing PIN may be auto-fetched over the network to [host]. Allowed
     * only for loopback, or for a Tailscale address while a VPN tunnel is actually
     * active. Everything else requires manual, out-of-band PIN entry.
     */
    fun canAutoFetchPin(context: Context, host: String): Boolean {
        if (isLoopback(host)) return true
        return isTailscaleAddress(host) && isVpnActive(context)
    }

    /**
     * Whether connecting to [host] needs LAN-scoped runtime permissions
     * (NEARBY_WIFI_DEVICES / ACCESS_LOCAL_NETWORK). A loopback or VPN/Tailscale target
     * is not on the local network, so those permissions are irrelevant and must never
     * block the connection — that was the cause of "won't even try to connect" when a
     * user denied local-network access but reached the host over Tailscale.
     */
    fun requiresLocalNetworkAccess(host: String): Boolean {
        return !(isLoopback(host) || isTailscaleAddress(host))
    }
}
