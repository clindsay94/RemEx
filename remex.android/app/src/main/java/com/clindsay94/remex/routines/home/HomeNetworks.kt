package com.clindsay94.remex.routines.home

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import com.clindsay94.remex.routines.RoutineLog
import java.net.InetAddress
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Reads the phone's networks for home presence (spec §8.3.1). `LinkProperties` and
 * `NetworkCapabilities` only: ACCESS_NETWORK_STATE, never location, never SSID or BSSID.
 */
internal object HomeNetworks {
    /** Wi-Fi or Ethernet, VPNs excluded, with or without internet (a home LAN can be offline). */
    fun request(): NetworkRequest =
        NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()

    fun snapshot(caps: NetworkCapabilities?, lp: LinkProperties?): NetworkSnapshot? {
        if (caps == null || lp == null) return null
        val routes = lp.routes
        return NetworkSnapshot(
            wifiOrEthernet = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET),
            vpn = caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN),
            gateways = routes.filter { it.isDefaultRoute && it.hasGateway() }.mapNotNull { it.gateway?.hostAddress },
            prefixes =
                routes.filter { !it.isDefaultRoute && !it.hasGateway() }.map { it.destination.toString() } +
                    lp.linkAddresses.map { "${it.address.hostAddress}/${it.prefixLength}" },
            dnsServers = lp.dnsServers.mapNotNull { it.hostAddress },
            domains = listOfNotNull(lp.domains),
            dhcpServer = lp.dhcpServerAddress?.hostAddress,
            addresses = lp.linkAddresses.mapNotNull { it.address.hostAddress },
        )
    }

    /** The network the phone uses by default right now (a VPN, when one is up). */
    fun active(context: Context): NetworkSnapshot? {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val network = cm.activeNetwork ?: return null
        return snapshot(cm.getNetworkCapabilities(network), cm.getLinkProperties(network))
    }

    /**
     * Every current Wi-Fi and Ethernet network (spec §8.3.1): a transient callback for [windowMs],
     * which reports each network that satisfies the request, then unregisters. `getAllNetworks()`
     * is deprecated.
     */
    suspend fun current(context: Context, windowMs: Long = 2_000): List<NetworkSnapshot> {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return emptyList()
        val seen = ConcurrentHashMap<Network, Pair<NetworkCapabilities?, LinkProperties?>>()
        val callback =
            object : ConnectivityManager.NetworkCallback() {
                override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                    seen.compute(network) { _, old -> caps to old?.second }
                }

                override fun onLinkPropertiesChanged(network: Network, lp: LinkProperties) {
                    seen.compute(network) { _, old -> old?.first to lp }
                }

                override fun onLost(network: Network) {
                    seen.remove(network)
                }
            }
        try {
            cm.registerNetworkCallback(request(), callback)
        } catch (e: RuntimeException) {
            RoutineLog.w("Listing networks for home presence failed.", e)
            return emptyList()
        }
        try {
            delay(windowMs)
        } finally {
            runCatching { cm.unregisterNetworkCallback(callback) }
        }
        return seen.values.mapNotNull { (caps, lp) -> snapshot(caps, lp) }
    }

    /** The PC's address as an IP literal; a name is resolved, bounded, off the main thread. */
    suspend fun literal(host: String?): String? {
        if (host.isNullOrBlank()) return null
        if (IpText.parse(host) != null) return host
        return withContext(Dispatchers.IO) {
            withTimeoutOrNull(3_000) { runCatching { InetAddress.getByName(host).hostAddress }.getOrNull() }
        }
    }
}
