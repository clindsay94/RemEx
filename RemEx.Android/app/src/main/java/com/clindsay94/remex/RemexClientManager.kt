package com.clindsay94.remex

import kotlinx.coroutines.channels.BufferOverflow
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

    // Frame buffer: no replay (don't hold stale frames in memory), drop oldest under back-pressure
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
