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
        fun onFileTransferMessage(json: String)
        fun onConnectionError(reason: String)
    }

    init {
        try {
            System.loadLibrary("RemexCore")
            isLibraryLoaded = true
            Log.i(TAG, "Loaded libRemexCore.so successfully")
        } catch (e: UnsatisfiedLinkError) {
            Log.e(TAG, "Failed to load native library libRemexCore.so. Ensure the compiled .so is present in jniLibs/arm64-v8a/", e)
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "InitRemexNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "WakePcNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "GetTelemetryNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "SendMessageNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "SendCommandNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "StartDesktopStreamNative crashed", e)
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
            } catch (e: RuntimeException) {
                Log.e(TAG, "StopDesktopStreamNative crashed", e)
            }
        }
    }

    @JvmStatic
    @JvmName("StopDesktopStreamNative")
    private external fun StopDesktopStreamNative()

    @JvmStatic
    @JvmName("StartPairingNative")
    private external fun StartPairingNative(hostUrl: String, clientName: String, clientVersion: String): String

    @JvmStatic
    fun StartPairing(hostUrl: String, clientName: String, clientVersion: String): String {
        return if (isLibraryLoaded) {
            try {
                StartPairingNative(hostUrl, clientName, clientVersion)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StartPairingNative not loaded", e)
                "{\"success\":false,\"message\":\"Native method not loaded.\"}"
            } catch (e: RuntimeException) {
                Log.e(TAG, "StartPairingNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("SubmitPairingPinNative")
    private external fun SubmitPairingPinNative(pin: String): String

    @JvmStatic
    fun SubmitPairingPin(pin: String): String {
        return if (isLibraryLoaded) {
            try {
                SubmitPairingPinNative(pin)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SubmitPairingPinNative not loaded", e)
                "{\"success\":false,\"message\":\"Native method not loaded.\"}"
            } catch (e: RuntimeException) {
                Log.e(TAG, "SubmitPairingPinNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("GetPinnedHostHashNative")
    private external fun GetPinnedHostHashNative(hostId: String): String

    @JvmStatic
    fun GetPinnedHostHash(hostId: String): String {
        return if (isLibraryLoaded) {
            try {
                GetPinnedHostHashNative(hostId)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "GetPinnedHostHashNative not loaded", e)
                "{\"success\":false,\"message\":\"Native method not loaded.\"}"
            } catch (e: RuntimeException) {
                Log.e(TAG, "GetPinnedHostHashNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    @JvmStatic
    @JvmName("SetPinnedHostHashNative")
    private external fun SetPinnedHostHashNative(hostId: String, spkiHashBase64: String): String

    @JvmStatic
    fun SetPinnedHostHash(hostId: String, spkiHashBase64: String): String {
        return if (isLibraryLoaded) {
            try {
                SetPinnedHostHashNative(hostId, spkiHashBase64)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SetPinnedHostHashNative not loaded", e)
                "{\"success\":false,\"message\":\"Native method not loaded.\"}"
            } catch (e: RuntimeException) {
                Log.e(TAG, "SetPinnedHostHashNative crashed", e)
                "{\"success\":false,\"message\":\"Native method crashed.\"}"
            }
        } else {
            "{\"success\":false,\"message\":\"Library not loaded.\"}"
        }
    }

    /**
     * Frees unmanaged memory previously allocated on the native heap and returned
     * as a pointer. Reserved for future use if [Export] is changed to use
     * Marshal.AllocHGlobal instead of JNI-managed jstring references.
     */
    @JvmStatic
    internal external fun FreeMemory(pointer: Long)
}
