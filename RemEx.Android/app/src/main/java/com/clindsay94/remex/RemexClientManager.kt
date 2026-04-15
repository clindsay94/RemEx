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
                connect()
                consecutiveFailures++
                delay(backoffMs)
            }
        }
    }

    fun toggleConnection() {
        if (isConnecting.value) return
        managerScope.launch {
            connect()
        }
    }

    private suspend fun connect() {
        val settings = settingsManager ?: return
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()
        val key = settings.accessKeyFlow.first()

        _isConnecting.value = true
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                val initRequest = JSONObject().apply {
                    put("host", host)
                    put("port", port)
                    put("accessKey", key)
                    put("startTelemetryPolling", true)
                }
                RemexCoreClient.InitRemex(initRequest.toString())
            } else {
                _isConnecting.value = false
            }
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
}
