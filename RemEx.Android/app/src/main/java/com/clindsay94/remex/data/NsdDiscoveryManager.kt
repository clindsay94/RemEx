package com.clindsay94.remex.data

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.resume

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
        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val multicastLock = wifiManager.createMulticastLock("remex_discovery").apply {
            setReferenceCounted(false)
            acquire()
        }

        try {
            return withTimeoutOrNull(timeoutMs) {
                discoverAndResolve(nsdManager)
            }
        } finally {
            multicastLock.release()
        }
    }

    private suspend fun discoverAndResolve(nsdManager: NsdManager): DiscoveredHost? {
        // Phase 1: Discover a service instance
        val serviceInfo = suspendCancellableCoroutine { cont ->
            val listener = object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(regType: String) {
                    Log.d(TAG, "mDNS discovery started for $regType")
                }

                override fun onServiceFound(service: NsdServiceInfo) {
                    Log.d(TAG, "Found service: ${service.serviceName} (${service.serviceType})")
                    nsdManager.stopServiceDiscovery(this)
                    if (cont.isActive) cont.resume(service)
                }

                override fun onServiceLost(service: NsdServiceInfo) {
                    Log.d(TAG, "Service lost: ${service.serviceName}")
                }

                override fun onDiscoveryStopped(serviceType: String) {
                    Log.d(TAG, "mDNS discovery stopped")
                }

                override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                    Log.e(TAG, "Discovery start failed: $errorCode")
                    if (cont.isActive) cont.resume(null)
                }

                override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                    Log.e(TAG, "Discovery stop failed: $errorCode")
                }
            }

            nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)

            cont.invokeOnCancellation {
                try {
                    nsdManager.stopServiceDiscovery(listener)
                } catch (_: Exception) { }
            }
        } ?: return null

        // Phase 2: Resolve the discovered service to get host + port
        return suspendCancellableCoroutine { cont ->
            nsdManager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                override fun onResolveFailed(service: NsdServiceInfo, errorCode: Int) {
                    Log.e(TAG, "Resolve failed for ${service.serviceName}: $errorCode")
                    if (cont.isActive) cont.resume(null)
                }

                override fun onServiceResolved(service: NsdServiceInfo) {
                    val host = service.host?.hostAddress ?: return run {
                        if (cont.isActive) cont.resume(null)
                    }
                    Log.i(TAG, "Resolved ${service.serviceName} → $host:${service.port}")
                    if (cont.isActive) cont.resume(DiscoveredHost(service.serviceName, host, service.port))
                }
            })
        }
    }
}
