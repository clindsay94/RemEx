package com.clindsay94.remex.ui.screens

import android.app.Application
import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.os.PersistableBundle
import android.os.SystemClock
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.ClipboardVerdict
import com.clindsay94.remex.service.clipboardVerdictOf
import com.clindsay94.remex.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.flow.mapNotNull
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

private const val TAG = "RemoteControlVM"

/**
 * How long to wait for the PC's clipboard answer (RemEx-ci98m).
 *
 * Generous for a LAN round trip and still short enough that a silent host - an older build with no
 * `clipboard_request` handler, which drops the message and answers nothing - reports a failure
 * rather than leaving a button that never settles.
 */
private const val CLIPBOARD_FETCH_TIMEOUT_MS = 5_000L

/**
 * The cap in KB, for the one message that names it.
 *
 * The host decides "too large" and answers with a token, not a number, so this is the phone's copy
 * of a limit the PC owns. It is only ever used to phrase a sentence - the actual enforcement is the
 * shared native validation - and the host refuses independently regardless of what this says.
 */
private const val ClipboardValidation_MAX_KB = 256

class RemoteControlViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val remoteControlCardShapePreset =
            settingsManager.remoteControlCardShapePresetFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    0f
            )

    val remoteMouseCardShapePreset =
            settingsManager.remoteMouseCardShapePresetFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    0f
            )

    val cardCornerRadius =
            settingsManager.cardCornerRadiusFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    20
            )

    val mouseFabX =
            settingsManager.mouseFabXFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    Float.NaN
            )

    val mouseFabY =
            settingsManager.mouseFabYFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    Float.NaN
            )

    val verticalScrollSensitivity =
            settingsManager.remoteDesktopPreferencesFlow
                    .map { it.verticalScrollSensitivity }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val horizontalScrollSensitivity =
            settingsManager.remoteDesktopPreferencesFlow
                    .map { it.horizontalScrollSensitivity }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    private val _commandStatus = MutableStateFlow<String?>(null)
    val commandStatus: StateFlow<String?> = _commandStatus.asStateFlow()

    fun wakePc() {
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                _commandStatus.value = getApplication<Application>().getString(R.string.status_native_lib_not_loaded)
                return@launch
            }

            try {
                val mac = settingsManager.macAddressFlow.first()
                val broadcast = settingsManager.broadcastIpFlow.first()
                if (mac.isNotBlank()) {
                    val responseJson = RemexCoreClient.WakePc(mac, broadcast, 9).getOrNull() ?: ""
                    val response = JSONObject(responseJson)
                    val success = response.optBoolean("success", false)
                    val message = response.optString("message", getApplication<Application>().getString(R.string.widget_toast_wol_sent))
                    _commandStatus.value = if (success) {
                        getApplication<Application>().getString(R.string.rc_success_format, message)
                    } else {
                        getApplication<Application>().getString(R.string.rc_failed_format, message)
                    }
                } else {
                    _commandStatus.value = getApplication<Application>().getString(R.string.rc_failed_mac_not_configured)
                }
            } catch (e: Exception) {
                Log.w(TAG, "Sending a power command failed", e)
                _commandStatus.value = getApplication<Application>().getString(R.string.rc_error_format)
            }
        }
    }

    private fun sendWholePixelMouseMove(deltaX: Int, deltaY: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseMove")
                    put("deltaX", deltaX)
                    put("deltaY", deltaY)
                }
        )
    }

    private val mouseMoveThrottle = MouseMoveThrottle()

    /**
     * Feeds one frame of trackpad movement.
     *
     * Takes floats on purpose. The caller used to truncate each frame's delta to an Int before it got
     * here, which silently discarded any drag slower than one pixel per frame — see
     * [MouseMoveThrottle] for why that is the common case rather than an edge one.
     */
    fun sendMouseMove(deltaX: Float, deltaY: Float) {
        mouseMoveThrottle.onDelta(deltaX, deltaY, SystemClock.uptimeMillis())?.let {
            sendWholePixelMouseMove(it.x, it.y)
        }
    }

    /**
     * Sends whatever movement is still accumulated when a drag ends, so a gesture does not stop
     * short of where the user left it.
     */
    fun flushPendingMouseMove() {
        mouseMoveThrottle.flush(SystemClock.uptimeMillis())?.let {
            sendWholePixelMouseMove(it.x, it.y)
        }
    }

    fun sendMouseClick(button: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseClick")
                    put("button", button)
                }
        )
    }

    fun sendScroll(deltaY: Int, deltaX: Int = 0) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseScroll")
                    put("deltaY", deltaY)
                    put("deltaX", deltaX)
                }
        )
    }

    fun sendText(text: String) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "typeText")
                    put("text", text)
                }
        )
    }

    fun sendKeyPress(keyCode: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "keyDown")
                    put("keyCode", keyCode)
                }
        )
        sendInput(
                JSONObject().apply {
                    put("eventType", "keyUp")
                    put("keyCode", keyCode)
                }
        )
    }

    fun clearCommandStatus() {
        _commandStatus.value = null
    }

    /**
     * Sends whatever is on THIS phone's clipboard to the PC's clipboard (RemEx-hgqs).
     *
     * **THE OUTCOME IS DECIDED HERE, NOT BY THE PC, AND THAT IS DELIBERATE.** The obvious design has
     * the host validate and answer, but the answer would never arrive: this goes out through
     * [RemexCoreClient.SendMessage], which is fire-and-forget, and only [RemexCoreClient.SendCommand]
     * blocks to correlate a `command_response`. A reply to a message sent this way reaches the
     * correlation layer with no pending command to match and is dropped as silently as an unrouted
     * type. Both refusals a person can act on — nothing copied, too large — are knowable before the
     * send anyway, so checking first is also the better interaction: an empty clipboard should not
     * need a round trip to the PC to say so.
     *
     * The rule itself is the PC's, reached through [RemexCoreClient.ValidateClipboard] and turned
     * into a verdict by [clipboardVerdictOf], which fails CLOSED. The host re-checks on arrival,
     * because a client is not a thing to trust about its own payload size.
     *
     * **THE CLIPBOARD READ STAYS ON THE MAIN THREAD AND EVERYTHING ELSE LEAVES IT.** Those are two
     * separate requirements pulling opposite ways, which is why the split is where it is.
     * `getPrimaryClip` is refused to an app without window focus since API 29, and this body runs
     * inline in the click handler on `Main.immediate`, so focus holds — but only until the first
     * suspension point, after which it is a race. Everything downstream is the opposite: a JNI
     * marshal and a JSON serialise of up to 256 KB, which [sendDispatcher] exists to keep off the
     * main thread. That rule is stated for pointer events further down this file, and this payload
     * is three orders of magnitude larger than one of those.
     */
    fun sendClipboardToPc() {
        viewModelScope.launch {
            val app = getApplication<Application>()
            if (!RemexCoreClient.isLibraryLoaded) {
                // NOT status_native_lib_not_loaded, which names an internal component the user has
                // no way to act on - the same call takeScreenshot makes a few lines below, and the
                // newer of the two competing precedents in this file.
                _commandStatus.value = app.getString(R.string.clipboard_send_failed)
                return@launch
            }

            // NOT GATED ON isConnected AS A STYLE CHOICE - the quick-actions bar deliberately hosts
            // Wake PC, which only makes sense while disconnected. But this one cannot work then, and
            // the send below CANNOT tell you so: the native queue is unbounded, so handing it a
            // message always succeeds and the real failure happens later, off in a native log the
            // phone never shows. Claiming "sent" on that would be the exact false-confirmation this
            // file already calls out for the launcher widget.
            if (!RemexClientManager.isConnected.value) {
                _commandStatus.value = app.getString(R.string.clipboard_send_not_connected)
                return@launch
            }

            // READING THE CLIPBOARD IS ITSELF VISIBLE TO THE USER on Android 12+ (API 31): the system
            // shows a "pasted from" toast when an app reads it, though it suppresses that when the
            // app is reading its own copy. That is left alone rather than worked around - the user
            // pressed a button that says it sends the clipboard, so a system confirmation that it
            // was read agrees with what they asked for.
            val clipboard = app.getSystemService(ClipboardManager::class.java)
            val clip = clipboard?.primaryClip
            if (clip == null || clip.itemCount == 0) {
                // DISTINCT FROM "empty", because these are different stories and only one of them is
                // about the clipboard's contents. A null manager or a refused read is a failure; an
                // empty clip is the user having nothing copied. Reporting the first as the second
                // sends someone off to copy something again when that was never the problem.
                _commandStatus.value = app.getString(R.string.clipboard_send_failed)
                return@launch
            }

            // `.text`, NOT `coerceToText`. v1 is text only, and coerceToText does not refuse a
            // non-text clip - it stringifies it. Copy an image and it yields
            // "content://media/external/images/media/1234"; an intent yields "intent:#Intent;...";
            // a stream that fails mid-read yields the EXCEPTION's toString. Each of those would
            // overwrite the PC's clipboard with something useless while reporting success, which is
            // the same harm the empty-clipboard rule exists to prevent, arriving by another door.
            // coerceToText would also open a ContentResolver stream - arbitrary-latency IPC to
            // whatever app owns the clip - which is not something to do on the main thread.
            val text = clip.getItemAt(0).text?.toString()
            if (text == null) {
                _commandStatus.value = app.getString(R.string.clipboard_send_unsupported)
                return@launch
            }

            // NOTHING BELOW LOGS `text`. It is whatever the user last copied.
            val outcome =
                    coroutineScope {
                        // Same UNDISPATCHED subscribe-before-send as the fetch, and for the same
                        // reason: the answer is a SharedFlow event with no replay, so a collector
                        // registered after the PC replies has missed it.
                        val waiting =
                                async(start = CoroutineStart.UNDISPATCHED) {
                                    withTimeoutOrNull(CLIPBOARD_FETCH_TIMEOUT_MS) {
                                        RemexClientManager.clipboardMessages
                                                .mapNotNull { json ->
                                                    withContext(Dispatchers.Default) {
                                                        runCatching { JSONObject(json) }
                                                                .getOrNull()
                                                                ?.takeIf {
                                                                    it.optString("type") ==
                                                                            "clipboard_push_result"
                                                                }
                                                                ?.optJSONObject("clipboardPushResult")
                                                                ?.optString("reason")
                                                    }
                                                }
                                                .first()
                                    }
                                }

                        val sent =
                                withContext(sendDispatcher) {
                                    val message =
                                            JSONObject().apply {
                                                put("type", "clipboard_push")
                                                put("protocolVersion", 2)
                                                put(
                                                        "clipboardPush",
                                                        JSONObject().apply { put("text", text) }
                                                )
                                            }
                                    RemexCoreClient.SendMessage(message.toString())
                                            .getOrNull()
                                            ?.let {
                                                runCatching { JSONObject(it) }
                                                        .getOrNull()
                                                        ?.optBoolean("success", false)
                                            } == true
                                }

                        if (!sent) {
                            waiting.cancel()
                            null
                        } else {
                            waiting.await()
                        }
                    }

            // **"SENT" NOW MEANS THE PC TOOK IT, NOT THAT THE MESSAGE LEFT.** Those were the same
            // sentence until a real emulator push was refused by the pairing gate and the phone
            // reported success anyway (RemEx-s1ay7). Deciding the predictable refusals here still
            // happens above and still saves a round trip; this covers only the outcomes the phone
            // cannot know. A null answer is a host too old to send one, or one that never replied -
            // both are "we cannot say it worked", which is not the same as saying it did.
            _commandStatus.value =
                    when (outcome) {
                        "none" -> app.getString(R.string.clipboard_send_ok)
                        "empty" -> app.getString(R.string.clipboard_send_empty)
                        "too_large" ->
                                app.getString(
                                        R.string.clipboard_send_too_large,
                                        ClipboardValidation_MAX_KB
                                )
                        "refused" -> app.getString(R.string.clipboard_send_refused)
                        // MAPPED EXPLICITLY rather than left to the else. It reaches the same
                        // string today, but the host's guard asserts "every token the host emits is
                        // one the phone recognises" and an arm that only exists by falling through
                        // does not make that true - the guard would have kept passing while the
                        // claim quietly stopped holding.
                        "unavailable" -> app.getString(R.string.clipboard_send_failed)
                        // FAIL CLOSED, including on no answer at all.
                        else -> app.getString(R.string.clipboard_send_failed)
                    }
        }
    }

    /**
     * Fetches the PC's clipboard onto this phone (RemEx-ci98m).
     *
     * **THE SUBSCRIPTION IS OPENED BEFORE THE REQUEST IS SENT, AND THAT ORDER IS THE WHOLE
     * CORRECTNESS ARGUMENT.** [RemexClientManager.clipboardMessages] is a SharedFlow with no replay,
     * so an answer that arrives before anyone is collecting is gone. On a fast local link the PC can
     * answer well inside the time it takes this coroutine to reach a collector, and the symptom would
     * be a fetch that works everywhere except on the network where it is quickest.
     *
     * **AND IT IS BOUNDED**, because unlike the push there is a reply to wait for and nothing else
     * will ever complete this. An older host has no `clipboard_request` handler at all, drops the
     * message in silence, and answers nothing — which is not an error state anywhere, just a wait
     * that never ends.
     */
    fun fetchClipboardFromPc() {
        viewModelScope.launch {
            val app = getApplication<Application>()
            if (!RemexCoreClient.isLibraryLoaded || !RemexClientManager.isConnected.value) {
                _commandStatus.value = app.getString(R.string.clipboard_send_not_connected)
                return@launch
            }

            val answer =
                    coroutineScope {
                        // UNDISPATCHED, not merely "started first". A plain `async` SCHEDULES the
                        // coroutine; whether its body runs before the next statement then depends on
                        // the dispatcher, and depending on that would make this correct by accident.
                        // UNDISPATCHED runs the body inline until its first suspension point - the
                        // flow collection - and SharedFlow allocates the subscriber slot before it
                        // suspends, so the collector is provably registered before the request goes
                        // out. The bug it forecloses only appears on a link fast enough for the PC to
                        // answer first: a feature failing on exactly the network it should suit best.
                        val waiting =
                                async(start = CoroutineStart.UNDISPATCHED) {
                                    withTimeoutOrNull(CLIPBOARD_FETCH_TIMEOUT_MS) {
                                        RemexClientManager.clipboardMessages
                                                .mapNotNull { json ->
                                                    // PARSED OFF THE MAIN THREAD. The envelope can
                                                    // exceed 256 KB once JSON escaping is counted,
                                                    // and this coroutine resumes on Main after each
                                                    // emission. Not `flowOn`: that would move slot
                                                    // allocation off the caller's thread and undo
                                                    // the UNDISPATCHED guarantee above.
                                                    withContext(Dispatchers.Default) {
                                                        runCatching { JSONObject(json) }
                                                                .getOrNull()
                                                                ?.takeIf {
                                                                    it.optString("type") == "clipboard_content"
                                                                }
                                                                ?.optJSONObject("clipboardContent")
                                                    }
                                                }
                                                .first()
                                    }
                                }

                        // The NATIVE answer, not merely "the call returned" - the same reading the
                        // send direction makes. A send that never left reports itself synchronously,
                        // and waiting out the full timeout for news we already have is five seconds
                        // of a button that looks broken.
                        val sent =
                                withContext(sendDispatcher) {
                                    RemexCoreClient.SendMessage(
                                                    JSONObject()
                                                            .apply {
                                                                put("type", "clipboard_request")
                                                                put("protocolVersion", 2)
                                                            }
                                                            .toString()
                                            )
                                            .getOrNull()
                                            ?.let {
                                                runCatching { JSONObject(it) }
                                                        .getOrNull()
                                                        ?.optBoolean("success", false)
                                            } == true
                                }

                        if (!sent) {
                            waiting.cancel()
                            null
                        } else {
                            waiting.await()
                        }
                    }

            if (answer == null) {
                _commandStatus.value = app.getString(R.string.clipboard_fetch_failed)
                return@launch
            }

            when (answer.optString("reason")) {
                "none" -> {
                    // `isNull` BEFORE `optString`, because optString renders a JSON null as the
                    // four-character string "null" on Android's org.json. Our own host never sends
                    // one - the serializer omits null properties - but the wire is exactly where
                    // values this app did not author arrive, and the C# side re-validates a pushed
                    // payload for the same reason. Without this, a host sending {"text":null} would
                    // overwrite the phone's clipboard with the word "null", destroying whatever the
                    // user had copied: the precise harm this guard exists to prevent.
                    val text =
                            answer.takeIf { !it.isNull("text") }
                                    ?.optString("text")
                                    ?.takeIf { it.isNotEmpty() }
                    if (text == null) {
                        _commandStatus.value = app.getString(R.string.clipboard_fetch_empty)
                        return@launch
                    }

                    val clipboard = app.getSystemService(ClipboardManager::class.java)
                    if (clipboard == null) {
                        _commandStatus.value = app.getString(R.string.clipboard_fetch_failed)
                        return@launch
                    }

                    // GUARDED, because setPrimaryClip is a synchronous binder round trip and this
                    // payload is the largest the protocol permits. A 256 KB clip parcels to roughly
                    // half a megabyte against a ~1 MB per-process binder buffer shared with every
                    // other transaction in flight, so TransactionTooLarge, DeadObject and Security
                    // are all reachable - and viewModelScope has no exception handler, so an escape
                    // kills the app rather than reporting a failure. The largest permitted payload
                    // failing OPEN is the worst possible place for that.
                    val copied =
                            runCatching {
                                        clipboard.setPrimaryClip(
                                                ClipData.newPlainText(
                                                                app.getString(R.string.clipboard_fetch_label),
                                                                text
                                                        )
                                                        .apply {
                                                            // MARKED SENSITIVE, ALWAYS. Android 13+
                                                            // shows a system confirmation containing
                                                            // a PREVIEW of what was copied. This
                                                            // feature is otherwise scrupulous that
                                                            // the text never appears anywhere - every
                                                            // log line on both sides carries a byte
                                                            // count instead - and that overlay is the
                                                            // one place the stance was not carried
                                                            // through. The phone cannot know whether
                                                            // a payload is a password, so the safe
                                                            // default is to assume every one is.
                                                            description.extras =
                                                                    PersistableBundle().apply {
                                                                        putBoolean(
                                                                                ClipDescription.EXTRA_IS_SENSITIVE,
                                                                                true
                                                                        )
                                                                    }
                                                        }
                                        )
                                    }
                                    .isSuccess

                    // KEPT DESPITE THE SYSTEM ALREADY CONFIRMING THE COPY on Android 13+, which
                    // is a decision rather than an oversight. The platform guidance is to drop your
                    // own copy toast because the system shows one - but the system's says only that
                    // something was copied, and having just marked the clip SENSITIVE we have also
                    // suppressed its preview. Provenance is then the only thing left that
                    // distinguishes this from any other copy on the device, and "from the PC" is
                    // exactly what the user tapped a button to cause.
                    _commandStatus.value =
                            app.getString(
                                    if (copied) R.string.clipboard_fetch_ok
                                    else R.string.clipboard_fetch_failed
                            )
                }
                "empty" -> _commandStatus.value = app.getString(R.string.clipboard_fetch_empty)
                "too_large" -> _commandStatus.value = app.getString(R.string.clipboard_fetch_too_large)
                // FAIL CLOSED on "unavailable" and on anything this build does not recognise - the
                // same rule, and the same reason, as clipboardVerdictOf on the send side.
                else -> _commandStatus.value = app.getString(R.string.clipboard_fetch_failed)
            }
        }
    }

    fun saveMouseFabPosition(x: Float, y: Float) {
        viewModelScope.launch { settingsManager.saveMouseFabPosition(x, y) }
    }

    fun updateScrollSensitivity(vertical: Float, horizontal: Float) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopScrollSensitivity(
                    vertical.coerceIn(0.1f, 5.0f),
                    horizontal.coerceIn(0.1f, 5.0f)
            )
        }
    }

    fun sendSystemCommand(action: String, delaySeconds: Int = 0) {
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                _commandStatus.value = getApplication<Application>().getString(R.string.status_native_lib_not_loaded)
                return@launch
            }

            try {
                val parameters = JSONObject()
                if (delaySeconds > 0) {
                    parameters.put("DelaySeconds", delaySeconds.toString())
                }

                val request =
                        JSONObject().apply {
                            put("action", action)
                            put("parameters", parameters)
                        }

                val responseJson = RemexCoreClient.SendCommand(request.toString()).getOrNull() ?: "{}"
                val response = JSONObject(responseJson)
                val message = response.optString("message", getApplication<Application>().getString(R.string.rc_command_sent))
                val success = response.optBoolean("success", false)
                _commandStatus.value =
                        if (success) {
                            getApplication<Application>().getString(R.string.rc_success_format, message)
                        } else {
                            getApplication<Application>().getString(R.string.rc_failed_format, message)
                        }
            } catch (e: Exception) {
                // rc_failed_format keeps its placeholder because its other two uses interpolate
                // the HOST's own message, which is worth showing. Only this site had an exception
                // in it, so it moves to the placeholder-free key instead (RemEx-poj6).
                Log.w(TAG, "Sending a remote-control command failed", e)
                _commandStatus.value = getApplication<Application>().getString(R.string.rc_error_format)
            }
        }
    }

    /**
     * Asks the PC to capture its screen (RemEx-byij).
     *
     * **DELIBERATELY NOT [sendSystemCommand], THOUGH IT WOULD HAVE COMPILED.** That path reports the
     * `message` field the response carries, and for a command dispatch that field is
     * `"Command dispatched."` — a developer-English sentence minted by the native layer, not by the
     * host and never translated. Routed through it, a Spanish user would read "Éxito: Command
     * dispatched." for a brand-new feature. RemEx-66rf replaced that placeholder with the host's real
     * message, but the host does not translate its text either, so routing through [sendSystemCommand]
     * would still put English in front of eight of the nine languages. The other commands on this
     * screen still do that; this declines to join them.
     *
     * Says "taken", not "arrived": the PC answers once the PNG is written, and the file only reaches
     * this phone if the user then accepts the offer (RemEx-y7my).
     */
    fun takeScreenshot() {
        // No isLibraryLoaded early return, unlike [sendSystemCommand]. That branch reports
        // status_native_lib_not_loaded, which names an internal component; SendCommand already fails
        // closed in exactly that case, so the plainer failure message below covers it and says
        // something the person holding the phone can act on. Deliberate, not an omission.
        //
        // No dispatcher: SendCommand does its own thread switch (RemEx-66rf).
        viewModelScope.launch {
            val request =
                    JSONObject().apply {
                        put("action", "SCREENSHOT")
                        put("parameters", JSONObject())
                    }

            val captured =
                    runCatching {
                                JSONObject(
                                                RemexCoreClient.SendCommand(request.toString())
                                                        .getOrThrow()
                                        )
                                        .optBoolean("success", false)
                            }
                            .onFailure { Log.w(TAG, "Sending the screenshot command failed", it) }
                            .getOrDefault(false)

            _commandStatus.value =
                    getApplication<Application>()
                            .getString(
                                    if (captured) R.string.screenshot_taken
                                    else R.string.screenshot_failed
                            )
        }
    }

    /**
     * Off the main thread, but STRICTLY ONE AT A TIME.
     *
     * Building two JSONObjects, serialising them and crossing the JNI boundary is not main-thread
     * work, and at trackpad rates it happened often enough to be felt. But plain `Dispatchers.IO`
     * would be a correctness bug, not just a dispatch change: [sendKeyPress] issues keyDown and
     * keyUp as two SEPARATE sends, and on a 64-thread dispatcher they would race through tens of
     * microseconds of JSON build and JNI marshalling. This is why it was safe before:
     * `viewModelScope.launch {}` defaults to `Dispatchers.Main.immediate`, and
     * `RemexCoreClient.SendMessage` does not suspend, so each body ran inline and in order.
     * `limitedParallelism(1)` keeps that submission order while still taking the work off the main
     * thread.
     *
     * THIS IS ONE HALF OF THE ORDERING GUARANTEE, and it works only because of the other half.
     * `desktop_input` does not go through the outbound queue — `AndroidNativeExports` used to hand it
     * to a fire-and-forget `Task.Run` that re-parallelised onto the .NET thread pool, so two sends
     * issued in order here could still reach the socket inverted, or overlap and have one dropped.
     * RemEx-krvz replaced that with a single-consumer queue, so the native side now preserves
     * whatever order it is handed. This dispatcher is what makes the order it is handed the order the
     * user actually pressed the keys in. Neither half is sufficient alone. (RemEx-3uhp)
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val sendDispatcher = Dispatchers.IO.limitedParallelism(1)

    /**
     * The host's advertised capabilities, latest wins.
     *
     * DECLARED AFTER [sendDispatcher] AND AS A PROPERTY RATHER THAN IN AN `init` BLOCK, both on
     * purpose. This class has no `init` block, and `SendDispatcherDeclarationOrderTest` exists
     * because one that reached the send path synchronously was a real defect in the SIBLING view
     * model, RemoteDesktopViewModel - this class has never had one. Adding one above the dispatcher
     * would import that hazard rather than reintroduce it. A property initializer sidesteps the
     * question entirely.
     *
     * `SharingStarted.Eagerly`, NOT the `WhileSubscribed` used by the settings flows above, and the
     * difference is the whole thing working. Nothing ever collects this - it is read as `.value` at
     * send time - so under `WhileSubscribed` the upstream would never be subscribed to, `.value`
     * would sit at the initial state forever, and the gate below would be permanently open while
     * looking correct. The settings flows can use `WhileSubscribed` because Compose collects them.
     *
     * On a parse failure the state falls back to refusing input rather than to the default. That is
     * the opposite of the ABSENT-key rule, and deliberately: an absent key means an older host that
     * should keep working, whereas an unparseable payload means we know nothing, and the sibling
     * view model treats it the same way (RemEx-i8ty).
     */
    private val capabilityState =
            RemexClientManager.hostCapabilities
                    .map { hostInfo ->
                        try {
                            parseHostCapabilities(hostInfo)
                        } catch (e: Exception) {
                            Log.w(TAG, "Failed to parse host capabilities", e)
                            RemoteDesktopCapabilityState(
                                    supportsRemoteDesktop = false,
                                    supportsInputSimulation = false
                            )
                        }
                    }
                    .stateIn(
                            viewModelScope,
                            SharingStarted.Eagerly,
                            RemoteDesktopCapabilityState()
                    )

    /**
     * Whether the host will act on key presses, for the UI to reflect (RemEx-hulc).
     *
     * Exposed because [sendInput] DROPS every event when this is false. A media button that looks
     * live but is silently discarded is worse than one that is visibly unavailable: the phone still
     * buzzes on tap, so the user gets positive feedback for a command the PC never receives, and
     * nothing anywhere reports an error.
     *
     * THE INITIAL VALUE IS READ FROM THE SAME DEFAULT THE GATE USES, never written as a literal.
     * `RemoteDesktopCapabilityState.supportsInputSimulation` defaults to TRUE on purpose — an absent
     * key means an older host that should keep working — and `hostCapabilities` does not emit until
     * the first `host_info` arrives. A hardcoded `false` here therefore did not merely flash: until
     * that first message the gate was open while the UI told the user their PC could not accept key
     * presses, which is exactly the drift this flow exists to prevent. Reachable on any cold launch
     * with the PC asleep — and that is precisely when this screen gets opened, because Wake PC lives
     * on it.
     */
    val supportsInputSimulation: StateFlow<Boolean> =
            capabilityState
                    .map { it.supportsInputSimulation }
                    .stateIn(
                            viewModelScope,
                            SharingStarted.Eagerly,
                            RemoteDesktopCapabilityState().supportsInputSimulation
                    )

    private fun sendInput(input: JSONObject) {
        // THE ONLY WAY INPUT LEAVES THIS CLASS, so gating here covers mouseMove, mouseClick,
        // mouseScroll, typeText, keyDown and keyUp at once. This screen needs no video stream, so it
        // is reachable on a host where remote desktop is off entirely - which made it a WIDER
        // exposure than the remote-desktop path it was omitted from, not a narrower one (RemEx-i8ty,
        // found by the review of RemEx-q9zw).
        //
        // SendControlInput, NOT SendMessage, AND THE DIFFERENCE IS THE WHOLE ROW WORKING
        // (RemEx-035d6). This used to hand-build a `desktop_input` envelope and pass it to
        // SendMessage, which routes BY TYPE: every desktop_input goes to RemexDesktopClient and out
        // over /ws/desktop. That is right for the Remote Desktop screen and wrong here, because this
        // screen has no stream - and RemexDesktopClient is a process singleton whose
        // stopped-by-request latch (RemEx-yzbb) is cleared only by starting one. Opening the Remote
        // Desktop screen and navigating away sets that latch via onCleared, so the media and volume
        // row went permanently dead for the rest of the process, with the phone still buzzing on
        // every tap. Before the latch is set it is no better: that path AUTO-STARTS a stream, so a
        // volume tap began a full capture session on the PC for a screen showing no video.
        //
        // The new export puts the same event on the control socket this screen is already talking
        // on, where the host has always handled it (PingPongHandler.DispatchInput), held keys and
        // all. No new message type, so nothing for the inbound router to drop (RemEx-y6x6).
        if (!capabilityState.value.supportsInputSimulation) return
        viewModelScope.launch(sendDispatcher) {
            if (RemexCoreClient.isLibraryLoaded) {
                RemexCoreClient.SendControlInput(input.toString()).getOrNull()
            }
        }
    }
}
