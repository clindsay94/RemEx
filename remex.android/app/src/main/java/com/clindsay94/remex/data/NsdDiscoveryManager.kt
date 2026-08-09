package com.clindsay94.remex.data

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.util.Log
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
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

        // Pre-API-34, NsdManager.resolveService() allows only a single in-flight resolve
        // per process; a second concurrent call fails with FAILURE_ALREADY_ACTIVE. The
        // manual discovery path and the heartbeat self-heal path (RemexClientManager) can
        // race, so serialise resolves process-wide. On API 34+ we use the concurrent,
        // cancellable registerServiceInfoCallback API and do not contend on this lock.
        private val resolveMutex = Mutex()
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
            // FILTERED THROUGH THE SHIPPED RULE RATHER THAN TRUSTED (RemEx-7gk69). Both resolve
            // paths below guard the HOST and neither validates the port, and NsdServiceInfo.port is
            // zero until the SRV record resolves — so a partially-resolved announcement reaches here
            // looking like a usable PC. Handing that to the connect form offers the user a
            // connection that cannot succeed, with nothing saying why.
            //
            // DiscoveredHostList already decided what "usable" means, with tests, for the list this
            // feeds; until RemEx-8ih5 wires that list up, this was the rule's only would-be caller
            // and it was not calling it. Returning null is the same answer discoverHost already
            // gives for a failed resolve, so nothing downstream needs to learn a new case.
            return withTimeoutOrNull(timeoutMs) {
                discoverAndResolve(nsdManager)
            }?.takeIf(DiscoveredHostList::isUsable)
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

        // Phase 2: Resolve the discovered service to get host + port.
        // On API 34+ the modern registerServiceInfoCallback API supports concurrent,
        // cancellable resolves, so no process-wide serialisation is required. On older
        // platforms only one resolveService() may be in flight per process, so guard it
        // with the shared mutex to avoid the FAILURE_ALREADY_ACTIVE race.
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            resolveServiceModern(nsdManager, serviceInfo)
        } else {
            resolveMutex.withLock {
                resolveServiceLegacy(nsdManager, serviceInfo)
            }
        }
    }

    @Suppress("DEPRECATION")
    private suspend fun resolveServiceLegacy(
        nsdManager: NsdManager,
        serviceInfo: NsdServiceInfo
    ): DiscoveredHost? = suspendCancellableCoroutine { cont ->
        val listener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(service: NsdServiceInfo, errorCode: Int) {
                Log.e(TAG, "Resolve failed for ${service.serviceName}: $errorCode")
                if (cont.isActive) cont.resumeWith(Result.success(null))
            }

            override fun onServiceResolved(service: NsdServiceInfo) {
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
        }

        try {
            nsdManager.resolveService(serviceInfo, listener)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start resolve", e)
            if (cont.isActive) cont.resumeWith(Result.success(null))
            return@suspendCancellableCoroutine
        }

        // The legacy ResolveListener cannot be cancelled; the single-resolve slot frees
        // itself once the callback fires. Nothing to unregister here, but install the
        // hook so cancellation does not leave the continuation dangling silently.
        cont.invokeOnCancellation {
            Log.d(TAG, "Legacy resolve cancelled before completion")
        }
    }

    @androidx.annotation.RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private suspend fun resolveServiceModern(
        nsdManager: NsdManager,
        serviceInfo: NsdServiceInfo
    ): DiscoveredHost? = suspendCancellableCoroutine { cont ->
        // Single-thread executor that SILENTLY DISCARDS tasks submitted after shutdown instead of
        // throwing RejectedExecutionException. unregisterServiceInfoCallback() is asynchronous, so
        // NsdManager can post a final ServiceInfoCallback event (e.g. onServiceInfoCallbackUnregistered)
        // to this executor after cleanup() has already shut it down. With the default AbortPolicy that
        // rejected task is thrown on the system ConnectivityThread and crashes the whole app
        // ("RemEx has stopped"); DiscardPolicy makes the late post a harmless no-op. (RemEx-0ov)
        val executor = java.util.concurrent.ThreadPoolExecutor(
            1, 1, 0L, java.util.concurrent.TimeUnit.MILLISECONDS,
            java.util.concurrent.LinkedBlockingQueue(),
            java.util.concurrent.ThreadPoolExecutor.DiscardPolicy(),
        )
        var unregistered = false
        lateinit var callback: NsdManager.ServiceInfoCallback

        fun cleanup() {
            if (!unregistered) {
                unregistered = true
                try { nsdManager.unregisterServiceInfoCallback(callback) } catch (_: Exception) {}
            }
            executor.shutdown()
        }

        callback = object : NsdManager.ServiceInfoCallback {
            override fun onServiceInfoCallbackRegistrationFailed(errorCode: Int) {
                Log.e(TAG, "Resolve registration failed for ${serviceInfo.serviceName}: $errorCode")
                cleanup()
                if (cont.isActive) cont.resumeWith(Result.success(null))
            }

            override fun onServiceUpdated(service: NsdServiceInfo) {
                val host = service.hostAddresses.firstOrNull()?.hostAddress
                if (host == null) {
                    // No usable address yet; wait for a subsequent update.
                    return
                }
                Log.i(TAG, "Resolved ${service.serviceName} → $host:${service.port}")
                cleanup()
                if (cont.isActive) cont.resumeWith(Result.success(
                    DiscoveredHost(
                        service.serviceName,
                        host,
                        service.port
                    )
                ))
            }

            override fun onServiceLost() {
                Log.d(TAG, "Service lost during resolve: ${serviceInfo.serviceName}")
            }

            override fun onServiceInfoCallbackUnregistered() {
                Log.d(TAG, "Resolve callback unregistered")
            }
        }

        try {
            nsdManager.registerServiceInfoCallback(serviceInfo, executor, callback)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start resolve", e)
            cleanup()
            if (cont.isActive) cont.resumeWith(Result.success(null))
            return@suspendCancellableCoroutine
        }

        cont.invokeOnCancellation {
            cleanup()
        }
    }
}
