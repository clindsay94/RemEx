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
        
        // Start Global Connection Heartbeat
        managerScope.launch(Dispatchers.IO) {
            while (true) {
                if (!isConnected.value && !isConnecting.value) {
                    val settings = settingsManager ?: continue
                    val host = settings.hostFlow.first()
                    // Auto-connect if a valid host is configured
                    if (host.isNotBlank()) {
                        Log.i("RemexManager", "Heartbeat triggering auto-connect to $host")
                        connect()
                    }
                }
                delay(5000)
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
        _frames.tryEmit(frame)
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
