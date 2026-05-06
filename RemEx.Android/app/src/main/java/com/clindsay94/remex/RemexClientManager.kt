package com.clindsay94.remex

import android.content.Context
import android.util.Log
import com.clindsay94.remex.data.SettingsManager
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

    private val _frames = MutableSharedFlow<ByteArray>(
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

    init {
        RemexCoreClient.setCallback(this)
    }

    fun initialize(context: Context) {
        if (settingsManager != null) return

        settingsManager = SettingsManager(context)

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

                val settings = settingsManager ?: run { delay(baseDelayMs); continue }
                val host = settings.hostFlow.first()

                if (host.isBlank()) {
                    delay(baseDelayMs)
                    continue
                }

                // 2^20 * 5000ms ≈ 87 minutes, which already exceeds maxDelayMs (5 min),
                // so coerceAtMost(20) safely avoids Int overflow on the shift.
                val backoffMs = minOf(baseDelayMs * (1L shl consecutiveFailures.coerceAtMost(20)), maxDelayMs)
                Log.i("RemexManager", "Heartbeat auto-connect to $host (attempt #${consecutiveFailures + 1}, backoff ${backoffMs}ms)")
                connect(null, true)
                consecutiveFailures++
                delay(backoffMs)
            }
        }
    }

    fun toggleConnection(pairingPin: String? = null) {
        if (_isConnecting.value) return
        _isConnecting.value = true
        managerScope.launch {
            connect(pairingPin)
        }
    }

    private val _pairingRequired = MutableSharedFlow<Pair<String, Int>>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val pairingRequired = _pairingRequired.asSharedFlow()

    private val _connectionError = MutableSharedFlow<String>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val connectionError = _connectionError.asSharedFlow()

    private val _fileTransferMessages = MutableSharedFlow<String>(extraBufferCapacity = 8, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val fileTransferMessages = _fileTransferMessages.asSharedFlow()

    private suspend fun connect(pairingPin: String? = null, isAutoConnect: Boolean = false) {
        val settings = settingsManager ?: return
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()

        _isConnecting.value = true
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                // If not pinned, emit event to UI and abort auto-connect
                val context = settings.context
                var spkiHash = com.clindsay94.remex.security.PinnedHostStore.getPin(context, host)
                
                // If the user manually provided a PIN, they explicitly want to pair. 
                // This clears any stale SPKI hashes and forces a re-pair.
                if (!pairingPin.isNullOrBlank() && pairingPin.length == 6) {
                    spkiHash = null
                }
                
                if (spkiHash == null) {
                    if (pairingPin != null && pairingPin.length == 6) {
                        // Attempt automatic pairing with the provided PIN
                        Log.i("RemexManager", "Attempting automatic pairing for $host with provided PIN")
                        val pairResult = RemexCoreClient.StartPairing(
                            "wss://$host:$port/ws",
                            "Android Client",
                            "2.0.0"
                        )
                        
                        if (pairResult == "OK") {
                            val submitResult = RemexCoreClient.SubmitPairingPin(pairingPin)
                            if (submitResult.startsWith("OK:")) {
                                val parts = submitResult.substring(3).split("|")
                                if (parts.size >= 2) {
                                    val hostId = parts[0]
                                    val newHash = parts[1]
                                    // Pin by both Unique ID (for discovery) and IP (for manual connection)
                                    com.clindsay94.remex.security.PinnedHostStore.setPin(context, hostId, newHash)
                                    com.clindsay94.remex.security.PinnedHostStore.setPin(context, host, newHash)
                                    spkiHash = newHash
                                    Log.i("RemexManager", "Automatic pairing successful for $host")
                                }
                            } else {
                                Log.e("RemexManager", "Automatic pairing PIN submission failed: $submitResult")
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

                val initRequest = JSONObject().apply {
                    put("host", host)
                    put("port", port)
                    put("spkiHash", spkiHash)
                    put("startTelemetryPolling", true)
                }
                val result = RemexCoreClient.InitRemex(initRequest.toString())
                if (result.isBlank()) {
                    Log.w("RemexManager", "InitRemex returned blank — possible native-side failure for $host:$port")
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

    override fun onTelemetryUpdate(telemetryData: String) {
        _telemetry.tryEmit(telemetryData)
    }

    override fun onConnectionStateChanged(isConnected: Boolean) {
        _isConnected.value = isConnected
        if (isConnected) _isConnecting.value = false
    }

    fun setConnecting(isConnecting: Boolean) {
        _isConnecting.value = isConnecting
    }

    override fun onLauncherSync(launcherData: String) {
        _launcherEntries.tryEmit(launcherData)
    }

    override fun onProcessListSync(processData: String) {
        _processList.tryEmit(processData)
    }

    override fun onFrameReceived(frame: ByteArray) {
        // Log frame arrival (keep it compact to avoid logcat flooding)
        if (System.currentTimeMillis() % 1000 < 50) {
            Log.d("RemexManager", "onFrameReceived: ${frame.size} bytes")
        }

        // Defensive copy to prevent native side from overwriting buffer
        // while we are still processing it in the ViewModel coroutine.
        _frames.tryEmit(frame.copyOf())
    }

    override fun onHostInfoUpdate(hostInfoData: String) {
        _hostCapabilities.tryEmit(hostInfoData)
    }

    override fun onDesktopError(errorText: String) {
        _desktopErrors.tryEmit(errorText)
    }

    override fun onDesktopMeta(metaData: String) {
        _desktopMeta.tryEmit(metaData)
    }

    override fun onFileTransferMessage(json: String) {
        _fileTransferMessages.tryEmit(json)
    }

    override fun onConnectionError(reason: String) {
        _connectionError.tryEmit(reason)
    }
}
