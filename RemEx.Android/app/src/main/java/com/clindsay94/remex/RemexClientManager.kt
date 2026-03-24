package com.clindsay94.remex

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

object RemexClientManager : RemexCoreClient.RemexCallback {

    private val _isConnected = MutableStateFlow(false)
    val isConnected = _isConnected.asStateFlow()

    private val _telemetry = MutableSharedFlow<String>(replay = 1)
    val telemetry = _telemetry.asSharedFlow()

    private val _launcherEntries = MutableSharedFlow<String>(replay = 1)
    val launcherEntries = _launcherEntries.asSharedFlow()

    private val _processList = MutableSharedFlow<String>(replay = 1)
    val processList = _processList.asSharedFlow()

    private val _frames = MutableSharedFlow<ByteArray>(replay = 1)
    val frames = _frames.asSharedFlow()

    init {
        RemexCoreClient.setCallback(this)
    }

    override fun onTelemetryUpdate(telemetryData: String) {
        _telemetry.tryEmit(telemetryData)
    }

    override fun onConnectionStateChanged(isConnected: Boolean) {
        _isConnected.value = isConnected
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
}
