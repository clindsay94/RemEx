package com.clindsay94.remex

/**
 * JNI Bridge for Remex.Core NativeAOT library.
 *
 * This class corresponds to the entry points defined in AndroidNativeExports.cs.
 * Ensure the compiled native library (libRemexCore.so) is located in
 * src/main/jniLibs/arm64-v8a/
 */
object RemexCoreClient {

    var isLibraryLoaded = false
        private set

    private var callback: RemexCallback? = null

    interface RemexCallback {
        fun onTelemetryUpdate(telemetryData: String)
        fun onConnectionStateChanged(isConnected: Boolean)
        fun onLauncherSync(launcherData: String)
        fun onProcessListSync(processData: String)
        fun onFrameReceived(frame: ByteArray)
        fun onHostInfoUpdate(hostInfoData: String)
        fun onDesktopError(errorText: String)
    }

    init {
        try {
            // Loads libRemexCore.so
            System.loadLibrary("RemexCore")
            isLibraryLoaded = true
        } catch (e: UnsatisfiedLinkError) {
            android.util.Log.e("RemexCoreClient", "Failed to load native library RemexCore", e)
        }
    }

    @JvmStatic
    fun setCallback(callback: RemexCallback?) {
        this.callback = callback
        if (isLibraryLoaded) {
            RegisterCallbackNative(callback)
        }
    }

    /**
     * Registers the callback object in the native layer for real-time notifications.
     */
    @JvmStatic
    private external fun RegisterCallbackNative(callback: RemexCallback?)

    /**
     * Initializes background services (telemetry polling, etc.)
     * @param initJson JSON string of AndroidNativeInitRequest
     * @return JSON string of AndroidNativeInitializationResponse
     */
    @JvmStatic
    external fun InitRemex(initJson: String): String

    /**
     * Sends a Wake-on-LAN packet.
     * @param macAddress MAC address of the target PC
     * @param broadcastIp Broadcast IP (e.g., "255.255.255.255")
     * @param port WOL port (usually 9)
     * @return JSON string of AndroidNativeOperationResponse
     */
    @JvmStatic
    external fun WakePc(macAddress: String, broadcastIp: String, port: Int): String

    /**
     * Retrieves the latest telemetry data from the cache or service.
     * @return JSON string of AndroidNativeTelemetryResponse
     */
    @JvmStatic
    external fun GetTelemetry(): String

    /**
     * Sends a raw RemexMessage to the host.
     * @param messageJson Full JSON of RemexMessage
     * @return JSON string of operation response
     */
    @JvmStatic
    external fun SendMessage(messageJson: String): String

    /**
     * Dispatches an IPC command to the remote host.
     * @param commandJson JSON string of CommandRequest
     * @return JSON string of CommandResponse
     */
    @JvmStatic
    external fun SendCommand(commandJson: String): String

    /**
     * Starts the remote desktop stream.
     */
    @JvmStatic
    external fun StartDesktopStream(configJson: String)

    /**
     * Stops the remote desktop stream.
     */
    @JvmStatic
    external fun StopDesktopStream()

    /**
     * Triggers mDNS service discovery on the native side.
     */
    @JvmStatic
    external fun StartMdnsDiscovery()

    /**
     * Frees memory allocated by the native side using Marshal.FreeCoTaskMem.
     * @param pointer The memory address to free.
     */
    @JvmStatic
    private external fun FreeMemory(pointer: Long)
}
