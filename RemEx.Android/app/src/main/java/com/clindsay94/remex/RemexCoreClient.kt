package com.clindsay94.remex

import android.util.Log

private const val TAG = "RemexCoreClient"

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
        fun onDesktopMeta(metaData: String)
    }

    init {
        try {
            System.loadLibrary("RemexCore")
            isLibraryLoaded = true
            Log.i(TAG, "Loaded libRemexCore.so successfully")
        } catch (e: UnsatisfiedLinkError) {
            Log.w(TAG, "Failed to load RemexCore, trying libRemexCore", e)
            try {
                System.loadLibrary("libRemexCore")
                isLibraryLoaded = true
                Log.i(TAG, "Loaded libRemexCore.so via fallback name")
            } catch (e2: UnsatisfiedLinkError) {
                Log.e(TAG, "Failed to load native library by any name", e2)
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
                Log.e(TAG, "RegisterCallbackNative not linked", e)
            }
        }
    }

    @JvmStatic
    private external fun RegisterCallbackNative(callback: RemexCallback?)

    @JvmStatic
    fun InitRemex(initJson: String): String {
        return if (isLibraryLoaded) {
            try {
                InitRemexNative(initJson)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "InitRemexNative not linked", e)
                "{\"success\":false,\"message\":\"Native method not linked.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("InitRemexNative")
    private external fun InitRemexNative(initJson: String): String

    @JvmStatic
    fun WakePc(macAddress: String, broadcastIp: String, port: Int): String {
        return if (isLibraryLoaded) {
            try {
                WakePcNative(macAddress, broadcastIp, port)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "WakePcNative not linked", e)
                "{\"success\":false,\"message\":\"Native method not linked.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("WakePcNative")
    private external fun WakePcNative(macAddress: String, broadcastIp: String, port: Int): String

    @JvmStatic
    fun GetTelemetry(): String {
        return if (isLibraryLoaded) {
            try {
                GetTelemetryNative()
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "GetTelemetryNative not linked", e)
                "{\"success\":false,\"message\":\"Native method not linked.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("GetTelemetryNative")
    private external fun GetTelemetryNative(): String

    @JvmStatic
    fun SendMessage(messageJson: String): String {
        return if (isLibraryLoaded) {
            try {
                SendMessageNative(messageJson)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SendMessageNative not linked", e)
                "{\"success\":false,\"message\":\"Native method not linked.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("SendMessageNative")
    private external fun SendMessageNative(messageJson: String): String

    @JvmStatic
    fun SendCommand(commandJson: String): String {
        return if (isLibraryLoaded) {
            try {
                SendCommandNative(commandJson)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SendCommandNative not linked", e)
                "{\"success\":false,\"message\":\"Native method not linked.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("SendCommandNative")
    private external fun SendCommandNative(commandJson: String): String

    @JvmStatic
    fun StartDesktopStream(configJson: String) {
        if (isLibraryLoaded) {
            try {
                StartDesktopStreamNative(configJson)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StartDesktopStreamNative not linked", e)
            }
        }
    }

    @JvmStatic
    @JvmName("StartDesktopStreamNative")
    private external fun StartDesktopStreamNative(configJson: String)

    @JvmStatic
    fun StopDesktopStream() {
        if (isLibraryLoaded) {
            try {
                StopDesktopStreamNative()
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StopDesktopStreamNative not linked", e)
            }
        }
    }

    @JvmStatic
    @JvmName("StopDesktopStreamNative")
    private external fun StopDesktopStreamNative()

    @JvmStatic
    private external fun FreeMemory(pointer: Long)
}
