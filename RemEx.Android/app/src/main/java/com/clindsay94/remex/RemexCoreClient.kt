package com.clindsay94.remex

import android.util.Log

private const val TAG = "RemexCoreClient"

/**
 * JNI Bridge for Remex.Core NativeAOT library.
 *
 * This class corresponds to the entry points defined in AndroidNativeExports.cs. Ensure the
 * compiled native library (libRemexCore.so) is located in src/main/jniLibs/arm64-v8a/
 */
object RemexCoreClient {

    var isLibraryLoaded = false
        private set

    private var callback: RemexCallback? = null

    interface RemexCallback {
        fun onTelemetryUpdate(telemetryData: String?)
        fun onConnectionStateChanged(isConnected: Boolean)
        fun onLauncherSync(launcherData: String?)
        fun onProcessListSync(processData: String?)
        fun onFrameReceived(frame: ByteArray?)
        fun onHostInfoUpdate(hostInfoData: String?)
        fun onDesktopError(errorText: String?)
        fun onDesktopMeta(metaData: String?)
        fun onDesktopWindowResult(resultData: String?)
        fun onDesktopStreamDescriptor(descriptor: String?)
        fun onDesktopDisplayCatalog(catalogJson: String?)
        fun onFileTransferMessage(json: String?)
        fun onConnectionError(reason: String?)
    }

    init {
        try {
            System.loadLibrary("RemexCore")
            isLibraryLoaded = true
            Log.i(TAG, "Loaded libRemexCore.so successfully")
        } catch (e: UnsatisfiedLinkError) {
            Log.e(
                    TAG,
                    "Failed to load native library libRemexCore.so. Ensure the compiled .so is present in jniLibs/arm64-v8a/",
                    e
            )
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

    @JvmStatic private external fun RegisterCallbackNative(callback: RemexCallback?)

    @JvmStatic
    fun InitRemex(initJson: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(InitRemexNative(initJson))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "InitRemexNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "InitRemexNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("InitRemexNative")
    private external fun InitRemexNative(initJson: String): String

    @JvmStatic
    fun WakePc(macAddress: String, broadcastIp: String, port: Int): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(WakePcNative(macAddress, broadcastIp, port))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "WakePcNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "WakePcNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("WakePcNative")
    private external fun WakePcNative(macAddress: String, broadcastIp: String, port: Int): String

    @JvmStatic
    fun GetTelemetry(): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(GetTelemetryNative())
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "GetTelemetryNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "GetTelemetryNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic @JvmName("GetTelemetryNative") private external fun GetTelemetryNative(): String

    @JvmStatic
    fun SendMessage(messageJson: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(SendMessageNative(messageJson))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SendMessageNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SendMessageNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SendMessageNative")
    private external fun SendMessageNative(messageJson: String): String

    @JvmStatic
    fun SendCommand(commandJson: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(SendCommandNative(commandJson))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SendCommandNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SendCommandNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SendCommandNative")
    private external fun SendCommandNative(commandJson: String): String

    @JvmStatic
    fun StartDesktopStream(configJson: String): Result<Unit> {
        return if (isLibraryLoaded) {
            try {
                StartDesktopStreamNative(configJson)
                Result.success(Unit)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StartDesktopStreamNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "StartDesktopStreamNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("StartDesktopStreamNative")
    private external fun StartDesktopStreamNative(configJson: String)

    @JvmStatic
    fun StopDesktopStream(): Result<Unit> {
        return if (isLibraryLoaded) {
            try {
                StopDesktopStreamNative()
                Result.success(Unit)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StopDesktopStreamNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "StopDesktopStreamNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic @JvmName("StopDesktopStreamNative") private external fun StopDesktopStreamNative()

    @JvmStatic
    @JvmName("StartPairingNative")
    private external fun StartPairingNative(
            hostUrl: String,
            clientName: String,
            clientVersion: String,
            clientId: String
    ): String

    @JvmStatic
    fun StartPairing(
            hostUrl: String,
            clientName: String,
            clientVersion: String,
            clientId: String
    ): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Log.d(
                        TAG,
                        "StartPairing → native (host=$hostUrl, client=$clientName v$clientVersion)"
                )
                val result = StartPairingNative(hostUrl, clientName, clientVersion, clientId)
                Log.d(TAG, "StartPairing ← native result: $result")
                Result.success(result)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StartPairingNative not loaded", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "StartPairingNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SubmitPairingPinNative")
    private external fun SubmitPairingPinNative(pin: String): String

    @JvmStatic
    fun SubmitPairingPin(pin: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Log.d(TAG, "SubmitPairingPin → native (pin length=${pin.length})")
                val result = SubmitPairingPinNative(pin)
                // Don't log the raw OK result — it contains hostId and SPKI hash. Just log shape.
                val redacted = if (result.startsWith("OK:")) "OK:<hostId>|<spkiHash>" else result
                Log.d(TAG, "SubmitPairingPin ← native result: $redacted")
                Result.success(result)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SubmitPairingPinNative not loaded", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SubmitPairingPinNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("GetPinnedHostHashNative")
    private external fun GetPinnedHostHashNative(hostId: String): String

    @JvmStatic
    fun GetPinnedHostHash(hostId: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(GetPinnedHostHashNative(hostId))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "GetPinnedHostHashNative not loaded", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "GetPinnedHostHashNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SetPinnedHostHashNative")
    private external fun SetPinnedHostHashNative(hostId: String, spkiHashBase64: String): String

    @JvmStatic
    fun SetPinnedHostHash(hostId: String, spkiHashBase64: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(SetPinnedHostHashNative(hostId, spkiHashBase64))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SetPinnedHostHashNative not loaded", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SetPinnedHostHashNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    /**
     * Sends a serialized `DesktopPointerBatch` JSON string to the connected host. Used for stylus
     * and high-fidelity pointer data (pressure, tilt, hover, barrel buttons).
     *
     * The JSON must match the `Remex.Core.Messages.RemexMessage` envelope with `type =
     * "desktop_pointer_batch"` and a `desktopPointerBatch` payload.
     *
     * Entry point: `Java_com_clindsay94_remex_RemexCoreClient_SendDesktopPointerBatchNative` in
     * `AndroidNativeExports.cs`.
     */
    @JvmStatic
    fun SendDesktopPointerBatch(batchJson: String): Result<Unit> {
        return if (isLibraryLoaded) {
            try {
                SendDesktopPointerBatchNative(batchJson)
                Result.success(Unit)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SendDesktopPointerBatchNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SendDesktopPointerBatchNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SendDesktopPointerBatchNative")
    private external fun SendDesktopPointerBatchNative(batchJson: String)
}
