package com.clindsay94.remex

import android.content.Context
import android.util.Log
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.RemexConnectionService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.json.JSONObject

object RemexClientManager : RemexCoreClient.RemexCallback {

    private val managerScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var settingsManager: SettingsManager? = null

    private val _isConnected = MutableStateFlow(false)
    val isConnected = _isConnected.asStateFlow()

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting = _isConnecting.asStateFlow()

    private val _telemetry = MutableSharedFlow<String>(replay = 1)
    val telemetry = _telemetry.asSharedFlow()

    private val _launcherEntries = MutableSharedFlow<String>(replay = 1)
    val launcherEntries = _launcherEntries.asSharedFlow()

    private val _processList = MutableSharedFlow<String>(replay = 1)
    val processList = _processList.asSharedFlow()

    private val _frames =
            MutableSharedFlow<ByteArray>(
                    replay = 0,
                    extraBufferCapacity = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val frames = _frames.asSharedFlow()

    private val _hostCapabilities = MutableSharedFlow<String>(replay = 1)
    val hostCapabilities = _hostCapabilities.asSharedFlow()

    private val _desktopErrors = MutableSharedFlow<String>(replay = 1)
    val desktopErrors = _desktopErrors.asSharedFlow()

    private val _desktopMeta = MutableSharedFlow<String>(replay = 1)
    val desktopMeta = _desktopMeta.asSharedFlow()

    private val _desktopStreamDescriptor = MutableSharedFlow<String>(replay = 1)
    val desktopStreamDescriptor = _desktopStreamDescriptor.asSharedFlow()

    private val _desktopDisplayCatalog = MutableSharedFlow<String>(replay = 1)
    val desktopDisplayCatalog = _desktopDisplayCatalog.asSharedFlow()

    private val _desktopWindowResults = MutableSharedFlow<String>(replay = 0, extraBufferCapacity = 8)
    val desktopWindowResults = _desktopWindowResults.asSharedFlow()

    init {
        RemexCoreClient.setCallback(this)
    }

    fun initialize(context: Context) {
        if (settingsManager != null) return

        settingsManager = SettingsManager(context)

        managerScope.launch {
            val host = settingsManager?.hostFlow?.first().orEmpty()
            if (host.isNotBlank()) {
                runCatching { RemexConnectionService.start(context) }
                        .onFailure { Log.w("RemexManager", "Failed to start keepalive service during initialize", it) }
            }
        }

        // Start Global Connection Heartbeat with exponential backoff.
        // Interval = min(BASE_DELAY_MS * 2^failures, MAX_DELAY_MS).
        // The failure counter resets to 0 whenever the device is connected.
        managerScope.launch(Dispatchers.IO) {
            var consecutiveFailures = 0
            val baseDelayMs = 5_000L
            val maxDelayMs = 300_000L // 5 minutes

            while (true) {
                if (isConnected.value) {
                    // Connected — reset backoff and poll at base rate
                    consecutiveFailures = 0
                    delay(baseDelayMs)
                    continue
                }

                if (isConnecting.value) {
                    delay(baseDelayMs)
                    continue
                }

                val settings =
                        settingsManager
                                ?: run {
                                    delay(baseDelayMs)
                                    continue
                                }
                val host = settings.hostFlow.first()

                if (host.isBlank()) {
                    delay(baseDelayMs)
                    continue
                }

                // 2^20 * 5000ms ≈ 87 minutes, which already exceeds maxDelayMs (5 min),
                // so coerceAtMost(20) safely avoids Int overflow on the shift.
                val backoffMs =
                        minOf(
                                baseDelayMs * (1L shl consecutiveFailures.coerceAtMost(20)),
                                maxDelayMs
                        )

                // mDNS Self-Healing: If we fail consecutively, try to discover the host automatically in the background
                if (consecutiveFailures >= 3) {
                    Log.i("RemexManager", "Heartbeat consecutive failures >= 3 ($consecutiveFailures). Triggering background mDNS self-healing discovery...")
                    try {
                        val discoveryManager = com.clindsay94.remex.data.NsdDiscoveryManager(settings.context)
                        val discovered = discoveryManager.discoverHost(3000) // 3 seconds timeout
                        if (discovered != null) {
                            Log.i("RemexManager", "Self-healing discovered host: ${discovered.serviceName} at ${discovered.host}:${discovered.port}")
                            val context = settings.context
                            val hasPin = com.clindsay94.remex.security.PinnedHostStore.getPin(context, discovered.serviceName)?.isNotBlank() == true ||
                                         com.clindsay94.remex.security.PinnedHostStore.getPin(context, discovered.host)?.isNotBlank() == true
                            
                            if (hasPin) {
                                val currentPreferences = settings.connectionPreferencesFlow.first()
                                val currentMac = currentPreferences.macAddress ?: ""
                                val currentBroadcast = currentPreferences.broadcastIp ?: "255.255.255.255"
                                val currentSubnet = currentPreferences.subnetMask ?: "255.255.255.0"

                                Log.i("RemexManager", "Discovered host is verified and trusted. Updating saved address to: ${discovered.host}:${discovered.port}")
                                settings.saveConnectionSettings(
                                    host = discovered.host,
                                    port = discovered.port,
                                    mac = currentMac,
                                    broadcast = currentBroadcast,
                                    subnetMask = currentSubnet
                                )
                                // Address updated; reset failures and proceed to connect immediately
                                consecutiveFailures = 0
                            } else {
                                Log.w("RemexManager", "Discovered host '${discovered.serviceName}' is not verified, skipping auto-update.")
                            }
                        } else {
                            Log.d("RemexManager", "Self-healing discovery found no hosts.")
                        }
                    } catch (e: Exception) {
                        Log.e("RemexManager", "Self-healing mDNS error", e)
                    }
                }

                val currentHost = settings.hostFlow.first()
                Log.i(
                        "RemexManager",
                        "Heartbeat auto-connect to $currentHost (attempt #${consecutiveFailures + 1}, backoff ${backoffMs}ms)"
                )
                connect(null, true)
                consecutiveFailures++
                delay(backoffMs)
            }
        }
    }

    fun toggleConnection(pairingPin: String? = null) {
        if (_isConnecting.value) return
        _isConnecting.value = true
        managerScope.launch { connect(pairingPin) }
    }

    private val _pairingRequired =
            MutableSharedFlow<Pair<String, Int>>(
                    replay = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val pairingRequired = _pairingRequired.asSharedFlow()

    private val _connectionError =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val connectionError = _connectionError.asSharedFlow()

    private val _fileTransferMessages =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 8,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val fileTransferMessages = _fileTransferMessages.asSharedFlow()

    private suspend fun connect(pairingPin: String? = null, isAutoConnect: Boolean = false) {
        val settings = settingsManager ?: run {
            _isConnecting.value = false
            return
        }
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()
        val clientId = settings.getOrCreateClientId()

        _isConnecting.value = true
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                // If not pinned, emit event to UI and abort auto-connect
                val context = settings.context
                var spkiHash =
                        com.clindsay94.remex.security.PinnedHostStore.getPin(context, host)
                                ?.takeIf { it.isNotBlank() }

                if (spkiHash.isNullOrBlank()) {
                    val cachedHashResult = RemexCoreClient.GetPinnedHostHash(host)
                    val cachedHash =
                            cachedHashResult.getOrNull()?.takeIf {
                                it.isNotBlank() && !it.startsWith("{\"success\":false")
                            }
                    if (cachedHash != null) {
                        spkiHash = cachedHash
                        Log.i("RemexManager", "Using native cached SPKI hash for $host")
                    }
                }

                // If the user manually provided a PIN, they explicitly want to pair.
                // This clears any stale SPKI hashes and forces a re-pair.
                if (!pairingPin.isNullOrBlank() && pairingPin.length == 6) {
                    spkiHash = null
                }

                if (spkiHash.isNullOrBlank()) {
                    if (pairingPin != null && pairingPin.length == 6) {
                        // Attempt automatic pairing with the provided PIN
                        Log.i(
                                "RemexManager",
                                "Attempting automatic pairing for $host with provided PIN"
                        )
                        val pairResult =
                                RemexCoreClient.StartPairing(
                                                "wss://$host:$port/ws",
                                                "Android Client",
                                                "2.0.0",
                                                clientId
                                        )
                                        .getOrNull()

                        if (pairResult == "OK") {
                            val submitResult =
                                    RemexCoreClient.SubmitPairingPin(pairingPin).getOrNull() ?: ""
                            if (submitResult.startsWith("OK:")) {
                                val parts = submitResult.substring(3).split("|")
                                if (parts.size >= 2) {
                                    val hostId = parts[0]
                                    val newHash = parts[1]
                                    // Pin by both Unique ID (for discovery) and IP (for manual
                                    // connection)
                                    RemexCoreClient.SetPinnedHostHash(hostId, newHash)
                                    RemexCoreClient.SetPinnedHostHash(host, newHash)
                                    com.clindsay94.remex.security.PinnedHostStore.setPin(
                                            context,
                                            hostId,
                                            newHash
                                    )
                                    com.clindsay94.remex.security.PinnedHostStore.setPin(
                                            context,
                                            host,
                                            newHash
                                    )
                                    spkiHash = newHash
                                    Log.i("RemexManager", "Automatic pairing successful for $host")
                                }
                            } else {
                                Log.e(
                                        "RemexManager",
                                        "Automatic pairing PIN submission failed: $submitResult"
                                )
                                _connectionError.tryEmit("Pairing failed: $submitResult")
                                _isConnecting.value = false
                                return
                            }
                        } else {
                            Log.e("RemexManager", "Automatic pairing start failed: $pairResult")
                            _connectionError.tryEmit("Pairing failed: $pairResult")
                            _isConnecting.value = false
                            return
                        }
                    } else {
                        _isConnecting.value = false
                        if (!isAutoConnect) {
                            _pairingRequired.tryEmit(Pair(host, port))
                        }
                        return
                    }
                }

                val initRequest =
                        JSONObject().apply {
                            put("host", host)
                            put("port", port)
                            put("spkiHash", spkiHash)
                            put("clientId", clientId)
                            put("startTelemetryPolling", true)
                        }
                val initResult = RemexCoreClient.InitRemex(initRequest.toString())
                val result = initResult.getOrNull() ?: ""
                if (result.isBlank()) {
                    Log.w(
                            "RemexManager",
                            "InitRemex returned blank — possible native-side failure for $host:$port"
                    )
                    _isConnecting.value = false
                } else {
                    val json = JSONObject(result)
                    if (!json.optBoolean("success", false)) {
                        _isConnecting.value = false
                    }
                }
            } else {
                _isConnecting.value = false
            }
        } catch (e: UnsatisfiedLinkError) {
            Log.e("RemexManager", "JNI link failure during connect", e)
            _connectionError.tryEmit("Native library not linked: ${e.message}")
            _isConnecting.value = false
        } catch (e: Exception) {
            Log.e("RemexManager", "Connect failed", e)
            _isConnecting.value = false
        }
    }

    override fun onTelemetryUpdate(telemetryData: String?) {
        telemetryData?.let { _telemetry.tryEmit(it) }
    }

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    override fun onConnectionStateChanged(isConnected: Boolean) {
        _isConnected.value = isConnected
        _isConnecting.value = false
        // _pairingRequired is a replay=1 SharedFlow, so a stale "pairing needed" event would
        // otherwise linger in the replay cache and re-fire every time a fresh collector
        // subscribes (e.g. MainActivity recreated by a widget tap), wrongly throwing the user
        // onto the PIN screen even while paired/connected. Clear it once we're connected.
        if (isConnected) {
            _pairingRequired.resetReplayCache()
        }
    }

    fun setConnecting(isConnecting: Boolean) {
        _isConnecting.value = isConnecting
    }

    override fun onLauncherSync(launcherData: String?) {
        launcherData?.let { _launcherEntries.tryEmit(it) }
    }

    override fun onProcessListSync(processData: String?) {
        processData?.let { _processList.tryEmit(it) }
    }

    override fun onFrameReceived(frame: ByteArray?) {
        if (frame == null) return

        // Log frame arrival (keep it compact to avoid logcat flooding)
        if (System.currentTimeMillis() % 1000 < 50) {
            Log.d("RemexManager", "onFrameReceived: ${frame.size} bytes")
        }

        // Defensive copy to prevent native side from overwriting buffer
        // while we are still processing it in the ViewModel coroutine.
        _frames.tryEmit(frame.copyOf())
    }

    override fun onHostInfoUpdate(hostInfoData: String?) {
        hostInfoData?.let { _hostCapabilities.tryEmit(it) }
    }

    override fun onDesktopError(errorText: String?) {
        errorText?.let { _desktopErrors.tryEmit(it) }
    }

    override fun onDesktopMeta(metaData: String?) {
        metaData?.let { _desktopMeta.tryEmit(it) }
    }

    override fun onDesktopWindowResult(resultData: String?) {
        resultData?.let { _desktopWindowResults.tryEmit(it) }
    }

    override fun onDesktopStreamDescriptor(descriptor: String?) {
        descriptor?.let { _desktopStreamDescriptor.tryEmit(it) }
    }

    override fun onDesktopDisplayCatalog(catalogJson: String?) {
        catalogJson?.let { _desktopDisplayCatalog.tryEmit(it) }
    }

    override fun onFileTransferMessage(json: String?) {
        json?.let { _fileTransferMessages.tryEmit(it) }
    }

    override fun onConnectionError(reason: String?) {
        _isConnected.value = false
        _isConnecting.value = false
        reason?.let { _connectionError.tryEmit(it) }
    }
}
