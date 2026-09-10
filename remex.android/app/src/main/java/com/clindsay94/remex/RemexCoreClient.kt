package com.clindsay94.remex

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.CancellationException

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
        fun onDesktopCursorState(stateJson: String?)
        // RD-E: raw 32-byte "RDXC" cursor-position packet (parsed with ByteBuffer; no JSON/JSONObject).
        fun onDesktopCursorBinary(packet: ByteArray?)
        fun onDesktopCursorShape(shapeJson: String?)
        fun onFileTransferMessage(json: String?)

        /**
         * Any `clipboard_*` message from the PC, as a whole RemexMessage envelope (RemEx-ci98m).
         *
         * The native router forwards the family by PREFIX, exactly as it does for `file_*`, so a
         * clipboard type added later arrives here without anyone having to remember the router
         * exists. Switch on `type` and ignore what you do not handle.
         */
        fun onClipboardMessage(json: String?)
        /**
         * Link quality measured by the native layer (RemEx-93n2).
         *
         * A JSON OBJECT rather than a bare number, because the quality meter this feeds will want
         * loss and bitrate beside the round trip. Adding a field to an object costs nothing; widening
         * a JNI signature later costs a coordinated change on both sides at once.
         *
         * Emitted on each pong, so its cadence is the ping interval - nothing new is scheduled to
         * produce it.
         */
        fun onLinkQuality(json: String?)
        fun onConnectionError(reason: String?)

        /**
         * Which phase of pairing has just started, as a stable token (RemEx-g87x).
         *
         * Tokens rather than sentences: the native side does not know the phone's language. Map
         * these to localized strings on this side, and treat an unrecognised one as "say nothing"
         * so a phase added later needs no coordinated release.
         */
        fun onPairingProgress(phase: String?)

        /**
         * What the PC is playing, as a `MediaPlaybackState` payload (RemEx-xx6xf).
         *
         * Pushed on connect and then only when the reading actually changes, so a quiet machine sends
         * nothing at all. Parse with
         * [com.clindsay94.remex.data.MediaPlaybackSnapshot.parse], which degrades rather than throws.
         */
        fun onMediaState(mediaStateJson: String?)

        /**
         * One cover image, as a `MediaArtwork` payload (RemEx-vtorl).
         *
         * ONE JSON STRING, `{"artworkId":"…","pngBase64":"…"}`, rather than the two arguments the
         * design sketched — every other callback here is a single JSON string through
         * `NotifyJavaData`, and the information is identical either way.
         *
         * A **missing `pngBase64` is a real answer**: the host's store is a small LRU and that id has
         * been evicted. Stop asking for it and draw the placeholder glyph; retrying will not bring it
         * back. Delivered only in reply to [RequestMediaArtwork], never pushed.
         *
         * The bytes are whatever the PC's platform supplied — often JPEG despite the field name —
         * which is fine, `BitmapFactory` sniffs the format.
         *
         * Defaulted to a no-op so a callback implementation that has no use for artwork does not have
         * to say so.
         */
        fun onMediaArtwork(mediaArtworkJson: String?) {}
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

    /**
     * Broadcasts a Wake-on-LAN magic packet and reports whether it left this phone (RemEx-52n0).
     *
     * Suspending and dispatcher-owning for the same reason as [SendCommand]: the native side now
     * waits for the send instead of discarding it, so this must not sit on the main thread. The wait
     * is far shorter — a UDP broadcast completes as soon as the OS has the datagram, with no reply to
     * wait for — but "short" is not a thing to leave a UI thread depending on.
     *
     * Success means SENT, not woken. There is no acknowledgement from a machine that is switched off.
     */
    @JvmStatic
    suspend fun WakePc(macAddress: String, broadcastIp: String, port: Int): Result<String> = withContext(Dispatchers.IO) {
        if (isLibraryLoaded) {
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

    /**
     * Sends one input event on the CONTROL socket, never on `/ws/desktop` (RemEx-035d6).
     *
     * **THE ARGUMENT IS THE `inputEvent` PAYLOAD ALONE, NOT A `desktop_input` ENVELOPE**, and the
     * native side builds the envelope. A `desktop_input` handed to [SendMessage] is intercepted by
     * type and routed to the Remote Desktop client instead — correct for the Remote Desktop screen,
     * whose input belongs on the same socket as its stream, and wrong for every screen that has no
     * stream. That is how the media and volume row (RemEx-hulc) ended up either silently discarded
     * for the rest of the process or starting a full screen capture on the PC to press one key.
     *
     * Callers must serialise their own sends. A key press is `keyDown` then `keyUp`, and the two are
     * separate calls; the native queue preserves the order it is handed, but it cannot invent one.
     * [com.clindsay94.remex.ui.screens.RemoteControlViewModel] uses a single-threaded dispatcher for
     * exactly this.
     */
    @JvmStatic
    fun SendControlInput(inputEventJson: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(SendControlInputNative(inputEventJson))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SendControlInputNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SendControlInputNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SendControlInputNative")
    private external fun SendControlInputNative(inputEventJson: String): String

    /**
     * Asks the PC for the cover image behind one artwork id (RemEx-vtorl).
     *
     * The `Result` describes the QUEUEING only. The image arrives later on
     * [RemexCallback.onMediaArtwork], or does not arrive at all — an id the host has evicted comes
     * back with no `pngBase64`, and an id it has never seen is answered the same way.
     *
     * ASK ONCE PER ID. The reply can be a megabyte or more on a socket that is also carrying input,
     * so the caller is responsible for caching what it gets and for not re-asking while a request is
     * in flight; see `MediaArtworkCache`.
     */
    @JvmStatic
    fun RequestMediaArtwork(artworkId: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(RequestMediaArtworkNative(artworkId))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "RequestMediaArtworkNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "RequestMediaArtworkNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("RequestMediaArtworkNative")
    private external fun RequestMediaArtworkNative(artworkId: String): String

    /**
     * Asks the PC to move the current track's playback position (RemEx-vtorl).
     *
     * The `Result` describes the QUEUEING only, and a successful queue is not a moved track. Some
     * sessions accept the call and do nothing — Apple Music on Windows does this — so the caller
     * must reconcile against the next [RemexCallback.onMediaState] rather than treat its own
     * optimistic update as fact.
     *
     * A negative [positionMs] is refused rather than clamped: clamping would silently restart the
     * track and report success, which is the wrong thing to learn from a unit mistake on this side.
     * There is no upper check here, because this process does not know what the PC is playing;
     * the PC answers an unreachable position by leaving the position where it was.
     */
    @JvmStatic
    fun SeekMedia(positionMs: Long): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(SeekMediaNative(positionMs))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SeekMediaNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "SeekMediaNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("SeekMediaNative")
    private external fun SeekMediaNative(positionMs: Long): String

    /**
     * Judges a clipboard payload with the SAME rule the PC applies (RemEx-hgqs).
     *
     * **DO NOT REIMPLEMENT THIS IN KOTLIN, WHICH IS THE ONLY REASON IT CROSSES JNI FOR A LENGTH
     * CHECK.** The cap is 256 KB of UTF-8 BYTES, and the obvious Kotlin version — `text.length` —
     * measures characters. CJK text is three bytes per character, so that version would admit 768 KB,
     * and only for people writing in Chinese, Japanese or Korean. The shared implementation exists
     * precisely so the two sides cannot drift on that, and calling it is cheap: no network, no
     * suspension, pure string math on the caller's thread.
     *
     * @return `{"reason":"none|empty|too_large","byteCount":N,"maxBytes":N}`, or a failure when the
     * native library is missing. **Never contains the text.**
     */
    @JvmStatic
    fun ValidateClipboard(text: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(ValidateClipboardNative(text))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "ValidateClipboardNative not linked", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "ValidateClipboardNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("ValidateClipboardNative")
    private external fun ValidateClipboardNative(text: String): String

    /**
     * Sends a command to the PC and returns what the PC said about it.
     *
     * **THIS BLOCKS FOR A NETWORK ROUND TRIP, WHICH IS WHY IT OWNS ITS OWN THREAD (RemEx-66rf).**
     * The native export waits for the host's real `command_response` — up to 10 seconds if the host
     * has gone quiet — so calling it on the main thread would be an ANR. Rather than leave that as a
     * rule all eight call sites have to remember, and that a new one would break silently until a PC
     * happened to be unreachable, the switch to [Dispatchers.IO] lives here. Callers may invoke it
     * from anywhere they can suspend.
     *
     * Suspending is not by itself enough for a caller on a deadline: the two Glance widgets sit in a
     * `goAsync()` broadcast window of about the same length as this call's own budget, so they hand
     * the work to a detached scope instead of awaiting it (see `sendWidgetCommand`).
     *
     * One consequence worth knowing: because the hop happens inside, the dispatcher a caller launched
     * on no longer decides where the send lands. Commands are therefore not ordered against the input
     * stream, which uses [SendMessage] and keeps the caller's dispatcher. That is fine — no command
     * is order-sensitive with a key or pointer event — but it is not a guarantee to rely on.
     *
     * @return the host's answer as `CommandResponse` JSON (`success`, `message`, `errorDetails`), or
     * a failure when the native library is missing or its stub throws.
     */
    @JvmStatic
    suspend fun SendCommand(commandJson: String): Result<String> = withContext(Dispatchers.IO) {
        if (isLibraryLoaded) {
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
            clientId: String,
            attemptId: Long
    ): String

    /**
     * Runs the pairing handshake against a host, blocking for as long as it takes.
     *
     * **SUSPENDS BECAUSE THE WORST CASE IS NINETY SECONDS** — a 10s TCP probe, then a 20s TLS and
     * WebSocket upgrade, then 60s waiting for the host's PairingResponse. Those budgets are the
     * native side's; a caller can now abandon the wait (see below) but cannot shorten the work.
     *
     * The switch to [Dispatchers.IO] is INSIDE, for the same reason it is inside [SendCommand]
     * (RemEx-66rf): it stops being a rule every call site has to remember, and stops a new call site
     * failing silently until a PC happens to be unreachable — which is precisely how RemexClientManager
     * came to run this on `Dispatchers.Main` and ANR the app when a user tapped Connect with a typed
     * PIN (RemEx-uach).
     *
     * BE CLEAR ABOUT WHICH PART DOES WHAT. Suspending keeps the UI responsive; it is
     * [abandonablePairing] that makes a caller's `withTimeout` mean anything. A blocking JNI frame
     * cannot be interrupted by coroutine cancellation, so until RemEx-defb a `withTimeout` here took
     * effect only once the native call returned on its own. It now runs the call on a thread the
     * caller can walk away from AND tells the native side to abandon the attempt. The work still
     * takes as long as it takes — what changed is that nobody has to wait for it.
     */
    @JvmStatic
    suspend fun StartPairing(
            hostUrl: String,
            clientName: String,
            clientVersion: String,
            clientId: String
    ): Result<String> = withContext(Dispatchers.IO) {
        if (isLibraryLoaded) {
            try {
                // The client name is NOT logged. It used to be the constant "Android Client";
                // since RemEx-8m3r it is the name the user gave their phone, which is very often
                // their real name — and minify is on but no assumenosideeffects rule strips
                // android.util.Log, so this line survives into release builds and any bug report
                // taken from one. Its length is enough to debug an ArgMissing rejection.
                Log.d(
                        TAG,
                        "StartPairing → native (host=$hostUrl, clientNameLength=${clientName.length} v$clientVersion)"
                )
                val result = abandonablePairing("start") { id -> StartPairingNative(hostUrl, clientName, clientVersion, clientId, id) }
                Log.d(TAG, "StartPairing ← native result: $result")
                Result.success(result)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "StartPairingNative not loaded", e)
                Result.failure(e)
            } catch (e: CancellationException) {
                // AHEAD OF RuntimeException, WHICH IT IS A SUBCLASS OF. Since RemEx-defb the caller's
                // cancellation resumes this coroutine from inside the try, so without this every
                // timeout and every user cancel would be logged as the native having crashed and
                // briefly become a Result.failure. The outer coroutine re-throws it either way, but
                // only because a cancelled job's cause outranks a returned value — an undocumented
                // dependency to be resting a log line on.
                throw e
            } catch (e: RuntimeException) {
                Log.e(TAG, "StartPairingNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("CancelPairingNative")
    private external fun CancelPairingNative(attemptId: Long): String

    /**
     * Runs a blocking pairing native so that cancelling the caller actually ends it (RemEx-defb).
     *
     * The mechanism lives in [abandonable] — extracted so it can be tested for real, since nothing
     * about a JNI call can be exercised in a unit test. What is supplied here is the pairing-specific
     * part: how to tell the native side to abandon the attempt.
     */
    private suspend fun <T> abandonablePairing(name: String, block: (Long) -> T): T {
        // MINTED PER CALL, and the cancel carries it. Without an identity the cancel lands on
        // "whatever is in flight", which is routinely NOT this attempt: the cancellation handler is
        // registered before the JNI call is made, and an attempt can sit queued on the native pairing
        // lock while a previous one runs. Unidentified, a cancel would either miss (this attempt then
        // runs its full budget with nobody waiting) or abort a DIFFERENT surface's pairing — and
        // aborting a PIN submission tears the session down.
        val attemptId = nextPairingAttemptId.incrementAndGet()
        return abandonable(
            name = name,
            cancel = { CancelPairingNative(attemptId) },
            onCancelFailure = {
                // LOUD, because a failure here silently restores the bug this whole mechanism fixes:
                // the caller stops waiting, the native runs on holding the pairing lock, and the next
                // attempt queues behind it with nothing in the logs to explain the wait.
                Log.e(TAG, "Could not abandon pairing attempt $attemptId ($name) — it will run to its own budget", it)
            },
            block = { block(attemptId) },
        )
    }

    /** Source of the attempt ids above. Monotonic, so an id is never reused. */
    private val nextPairingAttemptId = java.util.concurrent.atomic.AtomicLong()

    @JvmStatic
    @JvmName("SubmitPairingPinNative")
    private external fun SubmitPairingPinNative(pin: String, attemptId: Long): String

    /**
     * Sends the PIN the user read off the host's screen, and waits up to 30s for confirmation.
     *
     * Suspends for the same reason as [StartPairing], and abandonable in the same way (RemEx-defb).
     * Reached from the same two places: the pairing screen, which already dispatched correctly, and
     * RemexClientManager's Connect tap, which did not (RemEx-uach).
     *
     * ABANDONING THIS ONE TEARS THE SESSION DOWN, unlike the other two. The PairingComplete has gone
     * out and its answer was never read, so the session is indeterminate and the user must start
     * again rather than submit a second time.
     */
    @JvmStatic
    suspend fun SubmitPairingPin(pin: String): Result<String> = withContext(Dispatchers.IO) {
        if (isLibraryLoaded) {
            try {
                Log.d(TAG, "SubmitPairingPin → native (pin length=${pin.length})")
                val result = abandonablePairing("submit-pin") { id -> SubmitPairingPinNative(pin, id) }
                // Don't log the raw OK result — it contains hostId and SPKI hash. Just log shape.
                val redacted = if (result.startsWith("OK:")) "OK:<hostId>|<spkiHash>" else result
                Log.d(TAG, "SubmitPairingPin ← native result: $redacted")
                Result.success(result)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "SubmitPairingPinNative not loaded", e)
                Result.failure(e)
            } catch (e: CancellationException) {
                // AHEAD OF RuntimeException, WHICH IT IS A SUBCLASS OF. Since RemEx-defb the caller's
                // cancellation resumes this coroutine from inside the try, so without this every
                // timeout and every user cancel would be logged as the native having crashed and
                // briefly become a Result.failure. The outer coroutine re-throws it either way, but
                // only because a cancelled job's cause outranks a returned value — an undocumented
                // dependency to be resting a log line on.
                throw e
            } catch (e: RuntimeException) {
                Log.e(TAG, "SubmitPairingPinNative crashed", e)
                Result.failure(e)
            }
        } else {
            Result.failure(IllegalStateException("Library not loaded."))
        }
    }

    @JvmStatic
    @JvmName("FetchPairingPinNative")
    private external fun FetchPairingPinNative(attemptId: Long): String

    /**
     * Fetches the host's currently-active pairing PIN over the already-open native pairing
     * WebSocket (ASI-compliant replacement for the old trust-all HTTP auto-fetch). Returns the
     * native result string: "OK:<pin>|<expiryUnixMs>" on success, "UNSUPPORTED" against an older
     * host that can't relay the PIN, or "ERROR: ..." on timeout / no-session / denied transport.
     * Never mutates pairing state, so manual PIN entry always remains available on any failure.
     *
     * Suspends and hops to [Dispatchers.IO] itself, like the other two pairing calls (RemEx-uach).
     * Its own budget is 5s, the shortest of the three, but it is still a blocking JNI frame.
     */
    @JvmStatic
    suspend fun FetchPairingPin(): Result<String> = withContext(Dispatchers.IO) {
        if (isLibraryLoaded) {
            try {
                Log.d(TAG, "FetchPairingPin → native")
                val result = abandonablePairing("fetch-pin") { id -> FetchPairingPinNative(id) }
                // Never log the raw PIN — log only the response shape.
                val redacted = if (result.startsWith("OK:")) "OK:<pin>|<expiry>" else result
                Log.d(TAG, "FetchPairingPin ← native result: $redacted")
                Result.success(result)
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "FetchPairingPinNative not loaded", e)
                Result.failure(e)
            } catch (e: CancellationException) {
                // AHEAD OF RuntimeException, WHICH IT IS A SUBCLASS OF. Since RemEx-defb the caller's
                // cancellation resumes this coroutine from inside the try, so without this every
                // timeout and every user cancel would be logged as the native having crashed and
                // briefly become a Result.failure. The outer coroutine re-throws it either way, but
                // only because a cancelled job's cause outranks a returned value — an undocumented
                // dependency to be resting a log line on.
                throw e
            } catch (e: RuntimeException) {
                Log.e(TAG, "FetchPairingPinNative crashed", e)
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
    @JvmName("ClearPinnedHostHashNative")
    private external fun ClearPinnedHostHashNative(hostId: String): String

    /** Drops the native in-memory pin for [hostId]. See ClearPinnedHostHashNative (RemEx-1phe). */
    @JvmStatic
    fun ClearPinnedHostHash(hostId: String): Result<String> {
        return if (isLibraryLoaded) {
            try {
                Result.success(ClearPinnedHostHashNative(hostId))
            } catch (e: UnsatisfiedLinkError) {
                Log.e(TAG, "ClearPinnedHostHashNative not loaded", e)
                Result.failure(e)
            } catch (e: RuntimeException) {
                Log.e(TAG, "ClearPinnedHostHashNative crashed", e)
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
