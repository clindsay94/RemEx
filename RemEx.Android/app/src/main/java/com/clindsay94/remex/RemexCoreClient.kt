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
            // Standard Android behavior is to strip 'lib' and '.so'
            System.loadLibrary("RemexCore")
            isLibraryLoaded = true
        } catch (e: UnsatisfiedLinkError) {
            android.util.Log.e("RemexCoreClient", "Failed to load native library RemexCore, trying libRemexCore", e)
            try {
                System.loadLibrary("libRemexCore")
                isLibraryLoaded = true
            } catch (e2: UnsatisfiedLinkError) {
                android.util.Log.e("RemexCoreClient", "Failed to load native library libRemexCore", e2)
            }
        }
    }

    @JvmStatic
    fun setCallback(callback: RemexCallback?) {
        this.callback = callback
        if (isLibraryLoaded) {
            try {
                RegisterCallbackNative(callback)
            } catch (e: UnsatisfiedLinkError) {
                android.util.Log.e("RemexCoreClient", "Native method RegisterCallbackNative not found", e)
            }
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
    fun InitRemex(initJson: String): String {
        return if (isLibraryLoaded) {
            try {
                InitRemexNative(initJson)
            } catch (e: UnsatisfiedLinkError) {
                android.util.Log.e("RemexCoreClient", "Native method InitRemexNative not found", e)
                "{\"success\":false,\"message\":\"Native method not found.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("InitRemexNative")
    private external fun InitRemexNative(initJson: String): String

    /**
     * Sends a Wake-on-LAN packet.
     * @param macAddress MAC address of the target PC
     * @param broadcastIp Broadcast IP (e.g., "255.255.255.255")
     * @param port WOL port (usually 9)
     * @return JSON string of AndroidNativeOperationResponse
     */
    @JvmStatic
    fun WakePc(macAddress: String, broadcastIp: String, port: Int): String {
        return if (isLibraryLoaded) {
            try {
                WakePcNative(macAddress, broadcastIp, port)
            } catch (e: UnsatisfiedLinkError) {
                "{\"success\":false,\"message\":\"Native method not found.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("WakePcNative")
    private external fun WakePcNative(macAddress: String, broadcastIp: String, port: Int): String

    /**
     * Retrieves the latest telemetry data from the cache or service.
     * @return JSON string of AndroidNativeTelemetryResponse
     */
    @JvmStatic
    fun GetTelemetry(): String {
        return if (isLibraryLoaded) {
            try {
                GetTelemetryNative()
            } catch (e: UnsatisfiedLinkError) {
                "{\"success\":false,\"message\":\"Native method not found.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("GetTelemetryNative")
    private external fun GetTelemetryNative(): String

    /**
     * Sends a raw RemexMessage to the host.
     * @param messageJson Full JSON of RemexMessage
     * @return JSON string of operation response
     */
    @JvmStatic
    fun SendMessage(messageJson: String): String {
        return if (isLibraryLoaded) {
            try {
                SendMessageNative(messageJson)
            } catch (e: UnsatisfiedLinkError) {
                "{\"success\":false,\"message\":\"Native method not found.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("SendMessageNative")
    private external fun SendMessageNative(messageJson: String): String

    /**
     * Dispatches an IPC command to the remote host.
     * @param commandJson JSON string of CommandRequest
     * @return JSON string of CommandResponse
     */
    @JvmStatic
    fun SendCommand(commandJson: String): String {
        return if (isLibraryLoaded) {
            try {
                SendCommandNative(commandJson)
            } catch (e: UnsatisfiedLinkError) {
                "{\"success\":false,\"message\":\"Native method not found.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("SendCommandNative")
    private external fun SendCommandNative(commandJson: String): String

    /**
     * Starts the remote desktop stream.
     */
    @JvmStatic
    fun StartDesktopStream(configJson: String) {
        if (isLibraryLoaded) {
            try {
                StartDesktopStreamNative(configJson)
            } catch (e: UnsatisfiedLinkError) { }
        }
    }

    @JvmStatic
    @JvmName("StartDesktopStreamNative")
    private external fun StartDesktopStreamNative(configJson: String)

    /**
     * Stops the remote desktop stream.
     */
    @JvmStatic
    fun StopDesktopStream() {
        if (isLibraryLoaded) {
            try {
                StopDesktopStreamNative()
            } catch (e: UnsatisfiedLinkError) { }
        }
    }

    @JvmStatic
    @JvmName("StopDesktopStreamNative")
    private external fun StopDesktopStreamNative()

    /**
     * Triggers mDNS service discovery on the native side.
     */
    @JvmStatic
    fun StartMdnsDiscovery() {
        if (isLibraryLoaded) {
            try {
                StartMdnsDiscoveryNative()
            } catch (e: UnsatisfiedLinkError) { }
        }
    }

    @JvmStatic
    @JvmName("StartMdnsDiscoveryNative")
    private external fun StartMdnsDiscoveryNative()

    /**
     * Frees memory allocated by the native side using Marshal.FreeCoTaskMem.
     * @param pointer The memory address to free.
     */
    @JvmStatic
    private external fun FreeMemory(pointer: Long)
}
