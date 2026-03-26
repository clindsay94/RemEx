package com.clindsay94.remex.data

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull

data class DiscoveredHost(
    val serviceName: String,
    val host: String,
    val port: Int
)

class NsdDiscoveryManager(private val context: Context) {

    companion object {
        private const val TAG = "NsdDiscoveryManager"
        private const val SERVICE_TYPE = "_remex._tcp."
    }

    suspend fun discoverHost(timeoutMs: Long = 5000): DiscoveredHost? {
        val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
        val wifiManager =
            context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager

        // Multicast lock improves mDNS reliability but isn't strictly required
        // as NsdManager uses the system-level mDNS daemon.
        var multicastLock: WifiManager.MulticastLock? = null
        try {
            multicastLock = wifiManager?.createMulticastLock("remex_discovery")?.apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: Exception) {
            Log.w(TAG, "Could not acquire multicast lock, proceeding without it", e)
        }

        try {
            return withTimeoutOrNull(timeoutMs) {
                discoverAndResolve(nsdManager)
            }
        } finally {
            try {
                if (multicastLock?.isHeld == true) multicastLock.release()
            } catch (_: Exception) {}
        }
    }

    private suspend fun discoverAndResolve(nsdManager: NsdManager): DiscoveredHost? {
        // Phase 1: Discover a service instance
        val serviceInfo = suspendCancellableCoroutine<NsdServiceInfo?> { cont ->
            var discoveryActive = true
            val listener = object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(regType: String) {
                    Log.d(TAG, "mDNS discovery started for $regType")
                }

                override fun onServiceFound(service: NsdServiceInfo) {
                    Log.d(TAG, "Found service: ${service.serviceName} (${service.serviceType})")
                    if (discoveryActive) {
                        discoveryActive = false
                        try { nsdManager.stopServiceDiscovery(this) } catch (_: Exception) {}
                    }
                    if (cont.isActive) cont.resumeWith(Result.success(service))
                }

                override fun onServiceLost(service: NsdServiceInfo) {
                    Log.d(TAG, "Service lost: ${service.serviceName}")
                }

                override fun onDiscoveryStopped(serviceType: String) {
                    Log.d(TAG, "mDNS discovery stopped")
                    discoveryActive = false
                }

                override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                    Log.e(TAG, "Discovery start failed: $errorCode")
                    discoveryActive = false
                    if (cont.isActive) cont.resumeWith(Result.success(null))
                }

                override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                    Log.e(TAG, "Discovery stop failed: $errorCode")
                    discoveryActive = false
                }
            }

            try {
                nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
            } catch (e: Exception) {
                Log.e(TAG, "Failed to start discovery", e)
                discoveryActive = false
                if (cont.isActive) cont.resumeWith(Result.success(null))
                return@suspendCancellableCoroutine
            }

            cont.invokeOnCancellation {
                if (discoveryActive) {
                    discoveryActive = false
                    try { nsdManager.stopServiceDiscovery(listener) } catch (_: Exception) {}
                }
            }
        } ?: return null

        // Phase 2: Resolve the discovered service to get host + port
        return suspendCancellableCoroutine { cont ->
            try {
                @Suppress("DEPRECATION")
                nsdManager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(service: NsdServiceInfo, errorCode: Int) {
                        Log.e(TAG, "Resolve failed for ${service.serviceName}: $errorCode")
                        if (cont.isActive) cont.resumeWith(Result.success(null))
                    }

                    override fun onServiceResolved(service: NsdServiceInfo) {
                        @Suppress("DEPRECATION")
                        val host = service.host?.hostAddress ?: return run {
                            if (cont.isActive) cont.resumeWith(Result.success(null))
                        }
                        Log.i(TAG, "Resolved ${service.serviceName} → $host:${service.port}")
                        if (cont.isActive) cont.resumeWith(Result.success(
                            DiscoveredHost(
                                service.serviceName,
                                host,
                                service.port
                            )
                        ))
                    }
                })
            } catch (e: Exception) {
                Log.e(TAG, "Failed to start resolve", e)
                if (cont.isActive) cont.resumeWith(Result.success(null))
            }
        }
    }
}
