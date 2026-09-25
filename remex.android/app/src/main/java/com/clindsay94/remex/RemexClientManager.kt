package com.clindsay94.remex

import android.appwidget.AppWidgetManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.ConnectivityManager
import android.net.Network
import android.os.PowerManager
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import com.clindsay94.remex.data.MediaArtworkCache
import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaSeekReconciler
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.data.ThemeSyncSeedResolver
import com.clindsay94.remex.data.ThemeSyncSender
import com.clindsay94.remex.data.toThemeSnapshot
import com.clindsay94.remex.service.FileTransferChannelClient
import com.clindsay94.remex.service.RemexConnectionService
import com.clindsay94.remex.ui.screens.PairingErrors
import com.clindsay94.remex.ui.screens.PairingSurface
import com.clindsay94.remex.widget.HardwareInfoWidgetReceiver
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.ProcessLifecycleOwner
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.atomic.AtomicLong
import org.json.JSONObject

/**
 * One connection the native client actually established, identified by the PC it reached.
 *
 * [epoch] counts established connections and exists only to make consecutive values unequal.
 * [RemexClientManager.connectedHost] is a StateFlow, so a collector that is not scheduled between
 * two updates sees only the later one and skips it entirely when it compares equal to the value it
 * last handled — which for a plain `Boolean` connected flag means a reconnect can pass unobserved.
 * Counting makes every establishment its own value, so a collector may fall behind but can never
 * mistake a new connection for the one it already handled. (RemEx-bz9t)
 */
data class EstablishedConnection(val host: String, val port: Int, val epoch: Long)

object RemexClientManager : RemexCoreClient.RemexCallback {

    /** Longest side an artwork bitmap is decoded to; see [decodeDownsampledArtwork]. */
    private const val MaxArtworkDimensionPx = 512

    /**
     * How long an optimistic seek is allowed to stand without a confirming `media_state` before
     * [seekMedia] reverts it. See [MediaSeekReconciler.shouldRevert].
     */
    private const val SeekConfirmWindowMs = 2_500L

    private val managerScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var settingsManager: SettingsManager? = null

    private val _isConnected = MutableStateFlow(false)
    val isConnected = _isConnected.asStateFlow()

    /**
     * The PC currently connected, or null while disconnected.
     *
     * Carries the host the native client was pointed at rather than whatever the settings say now:
     * the settings are written BEFORE the connection is attempted, so a settings read at
     * notification time names the PC the user asked for, not the one that answered. Anything that
     * records "this PC connected" must key off this, not off [isConnected] plus a settings lookup.
     * (RemEx-bz9t)
     */
    private val _connectedHost = MutableStateFlow<EstablishedConnection?>(null)
    val connectedHost = _connectedHost.asStateFlow()

    /**
     * The PC whose reconnect proof the host has ACCEPTED, or null until it does (and null again on
     * every disconnect). This is the "paired" edge, and it is later than [connectedHost]: the socket
     * opens, the kickoff ping triggers the host's challenge, the native client answers it, and only
     * the host's `reconnect_result` ack lands here. Anything the host gates on pairing - theme_sync
     * is the one that bit (RemEx-0vpw5) - must key off this, not off [connectedHost], or it is sent
     * into the gap and rejected with a command_response nobody is waiting for.
     *
     * Same shape as [connectedHost] rather than a bare Boolean, for the same reason (RemEx-bz9t): a
     * host switch drives disconnect-then-connect-then-authenticated, and a collector that missed the
     * intervening null would see no change on a Boolean that went true, true.
     */
    private val _authenticatedConnection = MutableStateFlow<EstablishedConnection?>(null)
    val authenticatedConnection = _authenticatedConnection.asStateFlow()

    /**
     * Whether any of the app's UI is started — the ONE app-level foreground flag (perf audit P0-5),
     * fed by the ProcessLifecycleOwner observer in [startTelemetryBackgroundPause]. Exposed so the
     * other background gates (P0-7's idle socket closes) read this rather than registering a second
     * observer that could disagree with it. False until the observer reports ON_START: [initialize]
     * can run from a widget callback with no UI started.
     */
    private val _appForeground = MutableStateFlow(false)
    val appForeground = _appForeground.asStateFlow()

    /**
     * True once the host has acked this connection's reconnect proof; see [authenticatedConnection].
     * Kept as its own flow, set in the same call, rather than derived with `map().stateIn()`: a derived
     * flow updates on [managerScope] (Main) a hop later, and the JVM tests read this synchronously
     * after the callback, exactly as they do [isConnected].
     */
    private val _isAuthenticated = MutableStateFlow(false)
    val isAuthenticated: StateFlow<Boolean> = _isAuthenticated.asStateFlow()

    /** Host/port of the connect attempt in flight, so a success can be attributed to a PC. */
    @Volatile private var pendingTarget: Pair<String, Int>? = null

    /** Monotonic counter behind [EstablishedConnection.epoch]. Written from the JNI callback thread. */
    private val connectionEpoch = AtomicLong(0L)

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting = _isConnecting.asStateFlow()

    /**
     * Newest full snapshot from the PC. Latest-wins (RemEx-e7npu).
     *
     * **`DROP_OLDEST` IS DECLARED, NOT INHERITED, AND THAT IS THE POINT OF THIS AND THE FLOWS BELOW
     * IT.** `MutableSharedFlow(replay = 1)` alone means `extraBufferCapacity = 0` and
     * `onBufferOverflow = SUSPEND`, so total capacity is one slot shared with the replay cache. Every
     * emitter here is `tryEmit`, which on a SUSPEND flow with no room RETURNS FALSE AND DISCARDS THE
     * VALUE — no exception, no log, and a Boolean nobody reads. The collectors are view models parsing
     * JSON on the main thread, so being busy is the ordinary case, not a rare one.
     *
     * Precisely: a collector that has taken a value and is still in its lambda leaves room for the
     * NEXT emit, which displaces the replayed one; it is the one after that which is refused. So the
     * cost of a busy collector was every second value, not every value.
     *
     * For a full snapshot, dropping the older value is CORRECT: the newer one supersedes it entirely.
     * The bug was never that a value was dropped, it was that the behaviour depended on buffer
     * occupancy rather than on anything anyone chose. Saying `DROP_OLDEST` makes `tryEmit` always
     * succeed and the newest reading always win.
     */
    private val _telemetry =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val telemetry = _telemetry.asSharedFlow()

    /** The full launcher list; a newer one replaces the last. Latest-wins (RemEx-e7npu). */
    private val _launcherEntries =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val launcherEntries = _launcherEntries.asSharedFlow()

    /**
     * The pairing phase currently running, or null between attempts (RemEx-g87x).
     *
     * replay = 1 so a screen that starts collecting mid-attempt still learns where things are,
     * rather than showing nothing until the next transition — which on the last phase could be
     * sixty seconds away.
     */
    private val _pairingProgress =
            MutableSharedFlow<String?>(
                    replay = 1,
                    extraBufferCapacity = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST,
            )
    val pairingProgress = _pairingProgress.asSharedFlow()

    /**
     * Forgets the phase of a finished attempt, so the next one does not start by describing it.
     *
     * **THE REPLAY IS WHY THIS HAS TO EXIST.** replay = 1 is what lets a screen that starts
     * collecting mid-attempt learn where things are instead of waiting up to sixty seconds for the
     * next transition — but it also means a new collector is handed the PREVIOUS attempt's last
     * token, immediately, before any native work has happened. Clearing the screen's own state
     * cannot help: the replayed value arrives afterwards and overwrites it. The cache itself has to
     * be cleared, and only the owner of the flow can do that.
     */
    fun clearPairingProgress() {
        _pairingProgress.tryEmit(null)
    }

    /** The full process table; a newer scan replaces the last. Latest-wins (RemEx-e7npu). */
    private val _processList =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val processList = _processList.asSharedFlow()

    private val _frames =
            MutableSharedFlow<ByteArray>(
                    replay = 0,
                    extraBufferCapacity = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val frames = _frames.asSharedFlow()

    /** The full capability set; a newer one replaces the last. Latest-wins (RemEx-e7npu). */
    private val _hostCapabilities =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val hostCapabilities = _hostCapabilities.asSharedFlow()

    /**
     * What the PC is playing (RemEx-xx6xf).
     *
     * A `StateFlow` rather than a `replay = 1` `SharedFlow`, AND IT IS RESET ON DISCONNECT — which is
     * the difference that matters, not the type. [hostCapabilities] above is a replaying flow that is
     * never cleared, so its last value outlives the connection that produced it; RemEx-hulc had to
     * gate the media row on `connected` SEPARATELY for exactly that reason. A playback reading is far
     * more perishable than a capability set: keeping "playing" across a disconnect would leave a pause
     * icon on screen describing a PC the phone can no longer see, and the user's next press would go
     * nowhere while the icon still claimed to know.
     */
    private val _mediaState = MutableStateFlow(MediaPlaybackSnapshot.Unknown)
    val mediaState: StateFlow<MediaPlaybackSnapshot> = _mediaState.asStateFlow()

    /**
     * Cache of decoded artwork, content-addressed by `artworkId` (RemEx-vtorl.4).
     *
     * **NOT RESET ON DISCONNECT, UNLIKE [_mediaArtwork] BELOW.** An artwork id is a content hash, so
     * the bitmap it names is still correct after a reconnect to the same PC — the asymmetry with
     * [_mediaArtwork], which IS cleared alongside [_mediaState] in [onConnectionStateChanged], is
     * deliberate: that flow describes what belongs on screen for the CURRENT connection, while this
     * cache describes bytes that do not stop being valid just because the socket briefly closed.
     */
    private val artworkCache = MediaArtworkCache<Bitmap>()

    /** The bitmap for [mediaState]'s current `artworkId`, or null. Reset to null with [_mediaState]. */
    private val _mediaArtwork = MutableStateFlow<Bitmap?>(null)
    val mediaArtwork: StateFlow<Bitmap?> = _mediaArtwork.asStateFlow()

    /**
     * The last snapshot [onMediaState] actually delivered from the host — as opposed to [_mediaState],
     * which [seekMedia] may briefly hold at an OPTIMISTIC value the host has not confirmed. This is
     * what a seek reverts to when that confirmation never arrives. Reset to [MediaPlaybackSnapshot.Unknown]
     * on the same disconnect that resets [_mediaState].
     */
    // @Volatile on these three (RemEx-vtorl review): onMediaState runs on the JNI dispatcher
    // thread (attached via AttachCurrentThreadAsDaemon), while seekMedia and the revert coroutine
    // run on managerScope (Main). Without a happens-before edge, the revert coroutine could read a
    // pre-seek lastHostArrivalElapsedMs racing a confirming media_state, decide shouldRevert==true,
    // and stomp the freshly-arrived host truth with a stale lastHostSnapshot.
    @Volatile private var lastHostSnapshot: MediaPlaybackSnapshot = MediaPlaybackSnapshot.Unknown

    /** [android.os.SystemClock.elapsedRealtime] at [lastHostSnapshot]; see [MediaSeekReconciler.shouldRevert]. */
    @Volatile private var lastHostArrivalElapsedMs: Long = 0L

    /** Pending revert timer from the most recent [seekMedia] call; cancelled by the next host arrival. */
    @Volatile private var seekRevertJob: Job? = null

    /**
     * Why the desktop stream failed. **MUST-DELIVER, and the most consequential flow here
     * (RemEx-e7npu).**
     *
     * Every other flow on this object carries a snapshot, where losing an older value is harmless
     * because a newer one says everything it said. These are DISTINCT EVENTS: each one is the only
     * account of a separate failure, so a dropped value is not a stale reading, it is the explanation
     * never arriving. RemEx-iaxc put the "the PC is discarding your input" advisory on this flow
     * precisely to break a silence — dropping it would restore the silence that bead removed.
     *
     * Buffered rather than latest-wins for that reason. `DROP_OLDEST` remains as a bounded backstop
     * so `tryEmit` still cannot fail silently, but it now takes nine unread errors to lose one rather
     * than a collector being briefly busy.
     */
    private val _desktopErrors =
            MutableSharedFlow<String>(
                    replay = 1,
                    extraBufferCapacity = 8,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST,
            )
    val desktopErrors = _desktopErrors.asSharedFlow()

    /**
     * Stream geometry and codec details. **MUST-DELIVER (RemEx-e7npu).**
     *
     * Not a snapshot that supersedes: each one describes a stream the client then has to decode
     * against — its collector sets the active codec and the stream pixel size, which key the decoder
     * view. Losing one leaves the decoder configured for the previous stream, which presents as a
     * corrupt or frozen picture rather than as an error.
     *
     * **BUFFERED ONLY BECAUSE THIS CLIENT KEEPS desktop_meta OFF THE HIGH-RATE PATH.** The host has a
     * second send site for it: a legacy cursor-position path taken when the client advertises neither
     * `supportsCursorState` nor `supportsBinaryCursor`, which fires on the ~10 Hz cursor tick. We
     * advertise both unconditionally, so that path is never taken. Make either capability conditional
     * — as `supportsDisplaySelection` already is — and this becomes a 10 Hz feed whose buffer means up
     * to five stale JSON parses per busy window instead of one. Revisit the size if that changes.
     */
    private val _desktopMeta =
            MutableSharedFlow<String>(
                    replay = 1,
                    extraBufferCapacity = 4,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST,
            )
    val desktopMeta = _desktopMeta.asSharedFlow()

    /**
     * Stream descriptor from the host. **NOTHING COLLECTS THIS TODAY**, so it is latest-wins.
     *
     * An earlier draft of this comment called it must-deliver and said the decoder is built from it.
     * Neither is true: the only references anywhere in the app are this declaration, its `tryEmit`,
     * and the callback interface. With no subscriber `tryEmit` cannot fail at all, so this flow never
     * had the bug the rest of this change fixes, and buffering it would have been provisioning for a
     * consumer that does not exist. `DROP_OLDEST` is declared anyway so it behaves like its
     * neighbours if one ever appears; add a buffer at that point, with a reason.
     */
    private val _desktopStreamDescriptor =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val desktopStreamDescriptor = _desktopStreamDescriptor.asSharedFlow()

    /** The full display catalog; a newer one replaces the last. Latest-wins (RemEx-e7npu). */
    private val _desktopDisplayCatalog =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val desktopDisplayCatalog = _desktopDisplayCatalog.asSharedFlow()

    /** Latest cursor state. High-rate and latest-wins (RemEx-e7npu). */
    private val _desktopCursorState =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val desktopCursorState = _desktopCursorState.asSharedFlow()

    // RD-E: raw binary "RDXC" cursor-position packets. replay=1 so a late collector gets the last
    // position; DROP_OLDEST because only the newest position is worth drawing (RemEx-e7npu), and
    // these arrive faster than anything else on this object.
    private val _desktopCursorBinary =
            MutableSharedFlow<ByteArray>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val desktopCursorBinary = _desktopCursorBinary.asSharedFlow()

    /**
     * Latest cursor bitmap. Latest-wins (RemEx-e7npu).
     *
     * Shapes are cached by serial, but the cache is only ever read at the serial named by the newest
     * cursor packet — so an intermediate shape that is dropped is never referenced again. Note the
     * direction of the fix here: under the old SUSPEND default the value refused was the NEWEST, the
     * one about to become active. `DROP_OLDEST` drops an intermediate instead and the newest always
     * lands. This collector also suspends across an off-main decode, so it holds its slot longer than
     * any other on this object, which made it one of the worst affected.
     */
    private val _desktopCursorShape =
            MutableSharedFlow<String>(replay = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val desktopCursorShape = _desktopCursorShape.asSharedFlow()

    private val _desktopWindowResults = MutableSharedFlow<String>(replay = 0, extraBufferCapacity = 8)
    val desktopWindowResults = _desktopWindowResults.asSharedFlow()

    init {
        RemexCoreClient.setCallback(this)
    }

    fun initialize(context: Context) {
        if (settingsManager != null) return

        val settings = SettingsManager(context)
        settingsManager = settings

        managerScope.launch {
            val host = settingsManager?.hostFlow?.first().orEmpty()
            if (host.isNotBlank()) {
                runCatching { RemexConnectionService.start(context) }
                        .onFailure { Log.w("RemexManager", "Failed to start keepalive service during initialize", it) }
            }
        }

        // theme_sync (RemEx-y06a0.1, blocker of RemEx-sudp8): announce the phone's palette once
        // the connection is established, and again on every theme-flow change, debounced inside
        // ThemeSyncSender. applicationContext because the seed resolver reads dynamic color and
        // configuration state that must outlive whichever Activity happened to call initialize().
        //
        // Dispatchers.IO throughout, like the heartbeat loop below: RemexCoreClient.SendMessage is
        // a blocking JNI call (RemEx-66rf's reasoning applies here too), and managerScope itself is
        // Dispatchers.Main, so leaving these on the default dispatcher would run every native send
        // on the UI thread (review finding on RemEx-y06a0.1).
        val appContext = context.applicationContext
        val sender =
                ThemeSyncSender(
                        scope = CoroutineScope(managerScope.coroutineContext + Dispatchers.IO),
                        isAuthenticated = { _isAuthenticated.value },
                        resolveSeed = { snapshot -> ThemeSyncSeedResolver.resolve(appContext, snapshot) },
                        send = { json -> RemexCoreClient.SendMessage(json) },
                )

        managerScope.launch(Dispatchers.IO) {
            settings.personalizationPreferencesFlow
                    .map { it.toThemeSnapshot() }
                    .distinctUntilChanged()
                    .collect { snapshot -> sender.onThemeChanged(snapshot) }
        }

        // AUTHENTICATED, NOT CONNECTED (RemEx-0vpw5). theme_sync is pairing-gated host-side, and the
        // socket opening is before the host has even issued its reconnect challenge - so a send
        // keyed on [connectedHost] arrived unpaired, was rejected, and the palette only ever reached
        // the PC on the next phone-side theme change. [authenticatedConnection] is the host's ack.
        managerScope.launch(Dispatchers.IO) {
            authenticatedConnection.filterNotNull().collect {
                sender.onConnected(settings.personalizationPreferencesFlow.first().toThemeSnapshot())
            }
        }

        startTelemetryBackgroundPause(appContext)
        val reconnectAllowed = startReconnectGateSignals(appContext)

        // Start Global Connection Heartbeat with exponential backoff.
        // Interval = min(BASE_DELAY_MS * 2^failures, MAX_DELAY_MS) - see [ReconnectGate.backoffDelayMs].
        // The failure counter resets to 0 whenever the device is connected, and whenever
        // [ReconnectGate] reopens after pausing the loop (perf audit P0-11).
        managerScope.launch(Dispatchers.IO) {
            var consecutiveFailures = 0
            val baseDelayMs = ReconnectGate.BASE_DELAY_MS

            while (true) {
                if (isConnected.value) {
                    // Connected — reset backoff. Perf audit P0-11: suspend until the connection drops
                    // rather than waking every 5 s to look, then give the same one-interval grace the
                    // poll used to before the first reconnect attempt (so a host switch's own
                    // disconnect-then-connect is not raced; RemEx-bz9t).
                    consecutiveFailures = 0
                    isConnected.first { !it }
                    delay(baseDelayMs)
                    continue
                }

                if (isConnecting.value) {
                    delay(baseDelayMs)
                    continue
                }

                val settings =
                        settingsManager
                                ?: run {
                                    delay(baseDelayMs)
                                    continue
                                }
                val host = settings.hostFlow.first()

                if (host.isBlank()) {
                    // Perf audit P0-11: nothing to reconnect to until a PC is saved, so wait for one
                    // instead of polling; the delay keeps the old grace before racing the pairing
                    // flow's own connect.
                    settings.hostFlow.first { it.isNotBlank() }
                    delay(baseDelayMs)
                    continue
                }

                // Perf audit P0-11: no network, or backgrounded with nothing on screen that needs the
                // connection - park until that changes rather than retrying into a void. Reopening
                // resets the backoff so the attempt happens at once (the app came back, or the
                // network did), then re-checks everything from the top.
                if (!reconnectAllowed.first()) {
                    Log.d("RemexManager", "Heartbeat paused: no network or app backgrounded")
                    reconnectAllowed.first { it }
                    Log.d("RemexManager", "Heartbeat resumed")
                    consecutiveFailures = 0
                    continue
                }

                val backoffMs = ReconnectGate.backoffDelayMs(consecutiveFailures)

                // mDNS Self-Healing: If we fail consecutively, try to discover the host automatically in
                // the background — but ONLY when the saved host is on a network where local multicast
                // can actually reach it. Over a VPN like Tailscale (CGNAT 100.64.0.0/10) or a public
                // address there is no local multicast, so discovery is pointless and merely re-triggers
                // Android's "RemEx wants to connect to a device on your local network" system prompt on
                // a loop. (RemEx-fkz)
                if (consecutiveFailures >= 3 && isMulticastReachableHost(host)) {
                    Log.i("RemexManager", "Heartbeat consecutive failures >= 3 ($consecutiveFailures). Triggering background mDNS self-healing discovery...")
                    try {
                        val discoveryManager = com.clindsay94.remex.data.NsdDiscoveryManager(settings.context)
                        val discovered = discoveryManager.discoverHost(3000) // 3 seconds timeout
                        if (discovered != null) {
                            Log.i("RemexManager", "Self-healing discovered host: ${discovered.serviceName} at ${discovered.host}:${discovered.port}")
                            val context = settings.context
                            val hasPin = com.clindsay94.remex.security.PinnedHostStore.getPin(context, discovered.serviceName)?.isNotBlank() == true ||
                                         com.clindsay94.remex.security.PinnedHostStore.getPin(context, discovered.host)?.isNotBlank() == true
                            
                            if (hasPin) {
                                val currentPreferences = settings.connectionPreferencesFlow.first()
                                val currentMac = currentPreferences.macAddress
                                val currentBroadcast = currentPreferences.broadcastIp
                                val currentSubnet = currentPreferences.subnetMask
                                // Read before the save below overwrites it (perf audit P1-1).
                                val savedPort = settings.portFlow.first()

                                Log.i("RemexManager", "Discovered host is verified and trusted. Updating saved address to: ${discovered.host}:${discovered.port}")
                                settings.saveConnectionSettings(
                                    host = discovered.host,
                                    port = discovered.port,
                                    mac = currentMac,
                                    broadcast = currentBroadcast,
                                    subnetMask = currentSubnet
                                )
                                // Perf audit P1-1: reset failures and connect immediately only when the
                                // address actually changed. The same address means the PC answers mDNS
                                // but refuses the connection, so the backoff keeps climbing instead.
                                if (ReconnectGate.selfHealFoundNewAddress(host, savedPort, discovered.host, discovered.port)) {
                                    consecutiveFailures = 0
                                } else {
                                    Log.d("RemexManager", "Self-healing found the already-saved address; keeping backoff.")
                                }
                            } else {
                                Log.w("RemexManager", "Discovered host '${discovered.serviceName}' is not verified, skipping auto-update.")
                            }
                        } else {
                            Log.d("RemexManager", "Self-healing discovery found no hosts.")
                        }
                    } catch (e: CancellationException) {
                        // Rethrow: a cancelled heartbeat/connect job must unwind here, not get
                        // logged as a discovery error and fall through to the connect attempt below.
                        throw e
                    } catch (e: Exception) {
                        Log.e("RemexManager", "Self-healing mDNS error", e)
                    }
                }

                val currentHost = settings.hostFlow.first()
                Log.i(
                        "RemexManager",
                        "Heartbeat auto-connect to $currentHost (attempt #${consecutiveFailures + 1}, backoff ${backoffMs}ms)"
                )
                connect(null, true)
                consecutiveFailures++
                // Sleep out the backoff, but cut it short if the gate closes meanwhile, so the loop
                // parks at the check above and the gate reopening - not the rest of a backoff of up
                // to 5 minutes - decides when the next attempt happens (perf audit P0-11).
                withTimeoutOrNull(backoffMs) { reconnectAllowed.first { !it } }
            }
        }
    }

    /**
     * Wires the signals [ReconnectGate] decides on and returns whether the heartbeat may attempt a
     * reconnect now, as a flow that re-emits whenever any of them changes (perf audit P0-11).
     *
     * Foreground reuses [appForeground] (P0-5's one app-level flag). Network and screen state are new:
     * nothing else in the app tracked either. Both fail OPEN - if the system service is missing or the
     * registration throws, the flag stays true and the heartbeat behaves as it did before the gate,
     * because a battery cost is recoverable and a connection that never comes back is not.
     *
     * Called once, from [initialize], which its early return keeps single-shot; the callback and the
     * receiver are registered on the application context and live as long as the process.
     */
    private fun startReconnectGateSignals(appContext: Context): Flow<Boolean> {
        val networkAvailable = MutableStateFlow(true)
        runCatching {
            val cm = appContext.getSystemService(ConnectivityManager::class.java) ?: return@runCatching
            networkAvailable.value = cm.activeNetwork != null
            cm.registerDefaultNetworkCallback(
                    object : ConnectivityManager.NetworkCallback() {
                        // Tracked by identity: on a switch (Wi-Fi to cellular) the new default's
                        // onAvailable can arrive before the old one's onLost.
                        @Volatile private var current: Network? = null

                        override fun onAvailable(network: Network) {
                            current = network
                            networkAvailable.value = true
                        }

                        override fun onLost(network: Network) {
                            if (current == null || current == network) {
                                current = null
                                networkAvailable.value = false
                            }
                        }
                    }
            )
        }.onFailure {
            // Review fix (perf audit P0-11): the initial `networkAvailable.value = cm.activeNetwork
            // != null` line above can run (and see no network) BEFORE registerDefaultNetworkCallback
            // throws - leaving the flag stuck at false for the process lifetime instead of the
            // fail-open true this gate promises everywhere else. Reset it here so a broken callback
            // registration degrades to "always allow" rather than "never reconnect again".
            networkAvailable.value = true
            Log.w("RemexManager", "Network callback unavailable; reconnect gate ignores network", it)
        }

        val screenInteractive = MutableStateFlow(true)
        runCatching {
            screenInteractive.value =
                    appContext.getSystemService(PowerManager::class.java)?.isInteractive ?: true
            // SCREEN_ON/OFF are only delivered to receivers registered at runtime. They are protected
            // system broadcasts, so NOT_EXPORTED still receives them.
            appContext.registerReceiver(
                    object : BroadcastReceiver() {
                        override fun onReceive(context: Context?, intent: Intent?) {
                            when (intent?.action) {
                                Intent.ACTION_SCREEN_ON -> screenInteractive.value = true
                                Intent.ACTION_SCREEN_OFF -> screenInteractive.value = false
                            }
                        }
                    },
                    IntentFilter().apply {
                        addAction(Intent.ACTION_SCREEN_ON)
                        addAction(Intent.ACTION_SCREEN_OFF)
                    },
                    Context.RECEIVER_NOT_EXPORTED,
            )
        }.onFailure {
            // Same shape as the network flag's fix above: registerReceiver throwing after the
            // initial isInteractive read left screenInteractive potentially stuck at a stale false.
            screenInteractive.value = true
            Log.w("RemexManager", "Screen-state receiver unavailable; reconnect gate assumes screen on", it)
        }

        return combine(networkAvailable, appForeground, screenInteractive) { network, foreground, screenOn ->
            ReconnectGate.allows(
                    networkAvailable = network,
                    foreground = foreground,
                    screenInteractive = screenOn,
                    // Only consulted when the answer can hinge on it, since it is a binder call.
                    widgetPlaced = network && !foreground && screenOn && isHardwareWidgetPlaced(appContext),
            )
        }.distinctUntilChanged()
    }

    /**
     * Pauses the host's 1 Hz telemetry push while the app is in the background and resumes it on the
     * way back (perf audit P0-5). See [TelemetryBackgroundGate] for the decision table.
     *
     * APP-LEVEL, NOT SCREEN-LEVEL: telemetry feeds the dashboard and the widget cache, not one screen,
     * so this keys off ProcessLifecycleOwner (which also debounces ON_STOP, so a rotation does not
     * pause and resume). AUTHENTICATED, NOT CONNECTED, for the same reason as theme_sync above: the
     * messages are pairing-gated host-side, and a fresh socket starts unpaused, so every new
     * authenticated connection is re-reconciled while the app is still backgrounded.
     *
     * Called once, from [initialize], which its early return keeps single-shot.
     */
    private fun startTelemetryBackgroundPause(appContext: Context) {
        val gate = TelemetryBackgroundGate()
        // [_appForeground] is false until the observer below reports ON_START: initialize() can run
        // from a widget callback with no UI started, and the registration replays the current state
        // at once.

        // managerScope is Dispatchers.Main, which addObserver requires.
        managerScope.launch {
            ProcessLifecycleOwner.get().lifecycle.addObserver(
                    LifecycleEventObserver { _, event ->
                        when (event) {
                            Lifecycle.Event.ON_START -> _appForeground.value = true
                            Lifecycle.Event.ON_STOP -> _appForeground.value = false
                            else -> Unit
                        }
                    }
            )
        }

        // Perf audit P0-7: the binary /ws/files channel closes on the same signal once it is idle.
        managerScope.launch {
            appForeground.collect { FileTransferChannelClient.setAppForeground(it) }
        }

        managerScope.launch {
            combine(appForeground, authenticatedConnection) { foreground, connection ->
                foreground to connection
            }.collect { (foreground, connection) ->
                val action = gate.reconcile(foreground, connection, isHardwareWidgetPlaced(appContext))
                val message =
                        when (action) {
                            TelemetryBackgroundGate.Action.PAUSE ->
                                    JSONObject().apply { put("type", "telemetry_pause") }
                            TelemetryBackgroundGate.Action.RESUME ->
                                    JSONObject().apply { put("type", "telemetry_resume") }
                            TelemetryBackgroundGate.Action.NONE -> return@collect
                        }
                // Blocking JNI send, so off the main thread (RemEx-66rf). On failure, roll the gate's
                // state back so it no longer believes the host is synced - the next reconcile (a
                // foreground or connection change) then sees a mismatch again and retries, instead of
                // leaving a stale-paused or stale-streaming host with nothing left to prompt a fix.
                withContext(Dispatchers.IO) {
                    RemexCoreClient.SendMessage(message.toString()).onFailure {
                        Log.w("RemexManager", "Failed to send ${message.optString("type")}", it)
                        gate.revertFailedSend(connection, action)
                    }
                }
            }
        }
    }

    /**
     * Whether a hardware widget is on the home screen. It renders from the live telemetry stream
     * while the app is backgrounded, so its presence keeps the stream running. Errs towards true:
     * streaming needlessly costs battery, a frozen widget is a visible bug.
     */
    private fun isHardwareWidgetPlaced(context: Context): Boolean =
            runCatching {
                AppWidgetManager.getInstance(context)
                        .getAppWidgetIds(ComponentName(context, HardwareInfoWidgetReceiver::class.java))
                        .isNotEmpty()
            }.getOrDefault(true)

    /**
     * True when [host] is on a network where mDNS/local multicast discovery could plausibly reach the
     * PC: a private LAN IPv4 range, link-local, or a non-IP hostname (e.g. a `.local` name). Returns
     * false for Tailscale/CGNAT (100.64.0.0/10) and ordinary public addresses, where running discovery
     * is pointless and only spams Android's local-network permission prompt. (RemEx-fkz)
     */
    private fun isMulticastReachableHost(host: String): Boolean {
        val octets = host.trim().split(".")
        if (octets.size != 4) {
            // Not a dotted-quad IPv4 (hostname / IPv6) — allow discovery rather than over-suppress.
            return true
        }
        val b = octets.map { it.toIntOrNull() ?: return true }
        if (b.any { it !in 0..255 }) return true
        val (a, c) = b[0] to b[1]
        return when {
            a == 10 -> true                          // 10.0.0.0/8
            a == 172 && c in 16..31 -> true          // 172.16.0.0/12
            a == 192 && c == 168 -> true             // 192.168.0.0/16
            a == 169 && c == 254 -> true             // 169.254.0.0/16 link-local
            a == 100 && c in 64..127 -> false        // 100.64.0.0/10 CGNAT (Tailscale) — no multicast
            else -> false                            // public / other — no local multicast
        }
    }

    fun toggleConnection(pairingPin: String? = null) {
        if (_isConnecting.value) return
        _isConnecting.value = true
        managerScope.launch { connect(pairingPin) }
    }

    private val _pairingRequired =
            MutableSharedFlow<Pair<String, Int>>(
                    replay = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val pairingRequired = _pairingRequired.asSharedFlow()

    /**
     * Drops the retained pairing request after it has been acted on.
     *
     * [pairingRequired] has `replay = 1` so a subscriber that arrives late — the composition being
     * rebuilt after a widget tap re-creates MainActivity — still learns that pairing is needed. The
     * cost is that the SAME request is redelivered to every future subscriber, forever, which is why
     * the navigation site needed a guard against acting on a stale one. Consuming it here makes the
     * event one-shot in fact rather than by convention: it is delivered until somebody handles it,
     * then it is gone. Losing it is safe — a connect attempt that still needs pairing re-emits.
     *
     * This COMPLEMENTS the clear in [onConnectionStateChanged], which fires when a connection
     * succeeds. That one covers the case where nobody was looking; this one covers the case where
     * somebody was, and acted. Neither subsumes the other: a user who reaches the PIN screen and
     * backs out never connects, so only this call stops the request being redelivered on every
     * subsequent resume.
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    fun consumePairingRequest() {
        _pairingRequired.resetReplayCache()
    }

    /**
     * Clears the replay caches of the flows the overflow tests emit into (RemEx-e7npu).
     *
     * **A TEST SEAM, AND `internal` SO IT CANNOT BE MISTAKEN FOR PRODUCTION RECOVERY.** This object is
     * a process-wide singleton and `replay = 1` means a value emitted by one test is delivered to the
     * next collector in the same JVM run. Without this, a test that pushes a fake stream error leaves
     * it in `desktopErrors` for every later test — and the first thing a RemoteDesktopViewModel test
     * would see is an error it never sent, clearing `isStreaming` with nothing pointing back at the
     * cause. Same technique as the internal input seam on the host side (RemEx-73dc).
     *
     * Placed AFTER consumePairingRequest deliberately: an earlier draft sat between that function and
     * its KDoc, which silently rebound the pairing rationale to this seam and left the function it
     * belongs to undocumented.
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    internal fun resetStreamReplayCachesForTests() {
        _desktopErrors.resetReplayCache()
        _telemetry.resetReplayCache()
    }

    private val _connectionError =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val connectionError = _connectionError.asSharedFlow()

    // Perf audit P3-6: capacity 8 was an unexamined default, not a deliberate choice like the
    // latest-value flows above. Unlike cursor position or connection status, a file-control message
    // (offer/accept/progress/complete) is a discrete event that isn't safe to conflate - dropping one
    // can hang a transfer with no error, since the four collectors below (AndroidFileTransferHost,
    // FileTransferEngine, FileTransferViewModel, ShareToPcViewModel) all read the same broadcast and a
    // burst from concurrent transfers could outrun a slow one before all four drain it. This callback
    // is invoked from JNI (RemexCallback), so tryEmit is the only option - raised to 64 instead of
    // switching to SUSPEND, which a native callback thread cannot honor anyway.
    private val _fileTransferMessages =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 64,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val fileTransferMessages = _fileTransferMessages.asSharedFlow()

    /**
     * Whole `clipboard_*` envelopes from the PC (RemEx-ci98m).
     *
     * A SharedFlow rather than a StateFlow on purpose: a fetch is an EVENT, and two identical
     * clipboard answers in a row are two answers, so conflating them would drop the second.
     *
     * That fixes this hop only. The status the user finally sees still travels through
     * `_commandStatus`, which IS a StateFlow, so two identical outcomes in a row still produce one
     * snackbar - tap Download twice on an unchanged PC clipboard and the second tap looks like it
     * did nothing. That is pre-existing and shared with every other action on the screen, and it is
     * recorded here rather than fixed because the fix is a screen-wide change to how status is
     * delivered, not a clipboard one.
     */
    private val _clipboardMessages =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 4,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val clipboardMessages = _clipboardMessages.asSharedFlow()

    /**
     * Smoothed round-trip time to the PC in milliseconds, or null before the first pong (RemEx-93n2).
     *
     * A StateFlow, unlike [clipboardMessages]: this is a CURRENT VALUE, not an event. A consumer
     * joining late wants the latest reading rather than nothing, and two identical readings in a row
     * genuinely are the same state - conflating them is correct here and would have been wrong there.
     */
    private val _roundTripMs = MutableStateFlow<Double?>(null)
    val roundTripMs = _roundTripMs.asStateFlow()

    private suspend fun connect(pairingPin: String? = null, isAutoConnect: Boolean = false) {
        val settings = settingsManager ?: run {
            _isConnecting.value = false
            return
        }
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()
        val clientId = settings.getOrCreateClientId()

        // Every connect — manual, auto-reconnect, heartbeat self-heal — funnels through here, so
        // this is the one place that knows which PC the native client is about to be pointed at.
        // Recorded now and read back when the native side reports success (RemEx-bz9t).
        setPendingTarget(host, port)

        _isConnecting.value = true
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                // If not pinned, emit event to UI and abort auto-connect
                val context = settings.context
                var spkiHash =
                        com.clindsay94.remex.security.PinnedHostStore.getPin(context, host)
                                ?.takeIf { it.isNotBlank() }

                if (spkiHash.isNullOrBlank()) {
                    val cachedHashResult = RemexCoreClient.GetPinnedHostHash(host)
                    val cachedHash =
                            cachedHashResult.getOrNull()?.takeIf {
                                it.isNotBlank() && !it.startsWith("{\"success\":false")
                            }
                    if (cachedHash != null) {
                        spkiHash = cachedHash
                        Log.i("RemexManager", "Using native cached SPKI hash for $host")
                    }
                }

                // If the user manually provided a PIN, they explicitly want to pair.
                // This clears any stale SPKI hashes and forces a re-pair.
                if (!pairingPin.isNullOrBlank() && pairingPin.length == 6) {
                    spkiHash = null
                }

                if (spkiHash.isNullOrBlank()) {
                    if (pairingPin != null && pairingPin.length == 6) {
                        // Attempt automatic pairing with the provided PIN
                        Log.i(
                                "RemexManager",
                                "Attempting automatic pairing for $host with provided PIN"
                        )
                        val pairResult =
                                RemexCoreClient.StartPairing(
                                                "wss://$host:$port/ws",
                                                // The same real device name the pairing screen
                                                // sends (RemEx-8m3r). This path pairs without ever
                                                // showing that screen, so leaving it on the old
                                                // constant would have made the name depend on which
                                                // route the user happened to take.
                                                //
                                                // applicationContext for the reason stated further
                                                // down this function: settings.context is whichever
                                                // context won the early return in initialize(), and
                                                // may be a long-destroyed Activity.
                                                DeviceName.forPairing(
                                                        settings.context.applicationContext
                                                ),
                                                // The real version, as on the pairing screen
                                                // (RemEx-2zng). Both paths must be TRUTHFUL, which
                                                // is the actual requirement — two call sites that
                                                // agreed on the old literal would have "agreed" and
                                                // still both been wrong.
                                                BuildConfig.VERSION_NAME,
                                                clientId
                                        )
                                        .getOrNull()

                        if (pairResult == "OK") {
                            val submitResult =
                                    RemexCoreClient.SubmitPairingPin(pairingPin).getOrNull() ?: ""
                            if (submitResult.startsWith("OK:")) {
                                val parts = submitResult.substring(3).split("|")
                                if (parts.size >= 2) {
                                    val hostId = parts[0]
                                    val newHash = parts[1]
                                    // Pin by both Unique ID (for discovery) and IP (for manual
                                    // connection)
                                    RemexCoreClient.SetPinnedHostHash(hostId, newHash)
                                    RemexCoreClient.SetPinnedHostHash(host, newHash)
                                    com.clindsay94.remex.security.PinnedHostStore.setPin(
                                            context,
                                            hostId,
                                            newHash
                                    )
                                    com.clindsay94.remex.security.PinnedHostStore.setPin(
                                            context,
                                            host,
                                            newHash
                                    )
                                    spkiHash = newHash
                                    // Link the keys so a later forget can clear all of them
                                    // (RemEx-uxem).
                                    com.clindsay94.remex.security.PinnedHostStore.recordAliases(
                                            context,
                                            listOf(hostId, host, newHash)
                                    )
                                    // PAIR-1: persist the reconnect secret from the pairing result
                                    // (OK:hostId|spki|reconnectSecret) so later reconnects can answer
                                    // the host's proof-of-possession challenge. Stored per-host (by id
                                    // and ip), mirroring the SPKI pin above. (RemEx-xuo)
                                    if (parts.size >= 3 && parts[2].isNotBlank()) {
                                        com.clindsay94.remex.security.PinnedHostStore
                                                .setReconnectSecret(context, hostId, parts[2])
                                        com.clindsay94.remex.security.PinnedHostStore
                                                .setReconnectSecret(context, host, parts[2])
                                        // Also key by the SPKI hash — the one host identity that is
                                        // stable across EVERY address (LAN, Tailscale, …). connect()
                                        // retrieves by SPKI so a stale per-IP secret can never be used
                                        // after a transport switch. (RemEx-060g)
                                        com.clindsay94.remex.security.PinnedHostStore
                                                .setReconnectSecret(context, newHash, parts[2])
                                    }
                                    Log.i("RemexManager", "Automatic pairing successful for $host")
                                }
                            } else {
                                // The native reason is a DIAGNOSTIC, not a message: it is always
                                // English (Remex.Core is NativeAOT and cannot reach Android
                                // resources) and it names ports, paths and cert internals. It goes
                                // to the log; the cause code picks a translated sentence for the
                                // user. (RemEx-6gkr)
                                //
                                // InlineConnect, NOT the pairing screen's wording: this failure
                                // renders on ConnectionScreen, which has no Cancel button, and
                                // connect() restarts pairing on every tap — so "retype the PIN and
                                // tap Connect" is the recovery that works here.
                                //
                                // applicationContext because `settings.context` is whichever
                                // context won the early-return in initialize() — usually
                                // MainActivity, sometimes a widget's app context — and an Activity
                                // there is retained for the process lifetime and may be long
                                // destroyed. The app context is the one guaranteed to resolve the
                                // current per-app locale, and is a no-op when it already is one.
                                val failure = PairingErrors.parse(submitResult)
                                Log.e(
                                        "RemexManager",
                                        "Automatic pairing PIN submission failed [${failure.code ?: "no code"}]: ${failure.detail}"
                                )
                                _connectionError.tryEmit(
                                        context.applicationContext.getString(
                                                PairingErrors.messageRes(
                                                        failure.code,
                                                        PairingSurface.InlineConnect
                                                )
                                        )
                                )
                                _isConnecting.value = false
                                return
                            }
                        } else {
                            val failure = PairingErrors.parse(pairResult)
                            Log.e(
                                    "RemexManager",
                                    "Automatic pairing start failed [${failure.code ?: "no code"}]: ${failure.detail}"
                            )
                            _connectionError.tryEmit(
                                    context.applicationContext.getString(
                                            PairingErrors.messageRes(
                                                    failure.code,
                                                    PairingSurface.InlineConnect
                                            )
                                    )
                            )
                            _isConnecting.value = false
                            return
                        }
                    } else {
                        _isConnecting.value = false
                        if (!isAutoConnect) {
                            _pairingRequired.tryEmit(Pair(host, port))
                        }
                        return
                    }
                }

                // PAIR-1: supply the stored reconnect secret so the native client can answer the
                // host's proof-of-possession challenge on reconnect; without it the host rejects every
                // request from the connection as unpaired. Null/blank before the first paired
                // reconnect-secret is stored (then the host challenges and a re-pair is required). (RemEx-xuo)
                // Prefer the SPKI-keyed secret (stable across addresses) so a stale per-IP secret is
                // never used after a LAN <-> Tailscale switch. Fall back to the legacy per-address key
                // for pairings made before this fix — those need one re-pair to become address-proof.
                // (RemEx-060g)
                val reconnectSecret =
                        (spkiHash?.let {
                            com.clindsay94.remex.security.PinnedHostStore.getReconnectSecret(context, it)
                        })
                                ?: com.clindsay94.remex.security.PinnedHostStore.getReconnectSecret(context, host)
                val initRequest =
                        JSONObject().apply {
                            put("host", host)
                            put("port", port)
                            put("spkiHash", spkiHash)
                            put("clientId", clientId)
                            put("startTelemetryPolling", true)
                            if (!reconnectSecret.isNullOrBlank()) {
                                put("reconnectSecret", reconnectSecret)
                            }
                        }
                val initResult = RemexCoreClient.InitRemex(initRequest.toString())
                val result = initResult.getOrNull() ?: ""
                if (result.isBlank()) {
                    Log.w(
                            "RemexManager",
                            "InitRemex returned blank — possible native-side failure for $host:$port"
                    )
                    _connectionError.tryEmit(
                            context.applicationContext.getString(
                                    R.string.connection_error_no_response
                            )
                    )
                    _isConnecting.value = false
                } else {
                    val json = JSONObject(result)
                    if (!json.optBoolean("success", false)) {
                        // Surface the native failure reason instead of silently stopping the
                        // spinner with no error shown to the user. The native reason (like the
                        // pairing diagnostics above) is always English and untranslatable at the
                        // source, so only the fallback for a blank reason is localized here.
                        _connectionError.tryEmit(
                                json.optString("reason").ifBlank {
                                    context.applicationContext.getString(
                                            R.string.connection_error_generic
                                    )
                                }
                        )
                        _isConnecting.value = false
                    }
                }
            } else {
                _isConnecting.value = false
            }
        } catch (e: UnsatisfiedLinkError) {
            // "Native library not linked" is developer-grade diagnostic text; it must never reach
            // the user card verbatim in any language, so the technical detail stays in the log and
            // the user sees a plain, actionable message instead. (RemEx-hn05)
            Log.e("RemexManager", "JNI link failure during connect", e)
            _connectionError.tryEmit(
                    settings.context.applicationContext.getString(
                            R.string.connection_error_native_missing
                    )
            )
            _isConnecting.value = false
        } catch (e: Exception) {
            Log.e("RemexManager", "Connect failed", e)
            _isConnecting.value = false
        }
    }

    override fun onTelemetryUpdate(telemetryData: String?) {
        telemetryData?.let { _telemetry.tryEmit(it) }
    }

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    override fun onConnectionStateChanged(isConnected: Boolean) {
        // Cleared in BOTH directions, and BEFORE [_isConnected] moves: a fresh socket is not paired
        // until the host acks its proof, on a host switch the previous PC's ack must not survive into
        // the new connection, and no reader may ever observe connected=false with authenticated=true.
        _isAuthenticated.value = false
        _authenticatedConnection.value = null
        _isConnected.value = isConnected
        _isConnecting.value = false
        // Which PC this is, not merely that there is one. A host switch drives this callback false
        // then true — the native ConnectAsync closes the existing socket before opening the new one
        // — but [isConnected] alone cannot express the difference between "still A" and "now B", so
        // a collector that missed the intervening false would see no change at all. (RemEx-bz9t)
        _connectedHost.value =
                if (isConnected) {
                    pendingTarget?.let { (host, port) ->
                        EstablishedConnection(host, port, connectionEpoch.incrementAndGet())
                    }
                } else {
                    null
                }
        // _pairingRequired is a replay=1 SharedFlow, so a stale "pairing needed" event would
        // otherwise linger in the replay cache and re-fire every time a fresh collector
        // subscribes (e.g. MainActivity recreated by a widget tap), wrongly throwing the user
        // onto the PIN screen even while paired/connected. Clear it once we're connected.
        if (isConnected) {
            _pairingRequired.resetReplayCache()
        }
        // THE READING DIES WITH THE CONNECTION (RemEx-xx6xf). Cleared on connect as well as on
        // disconnect, and both directions are deliberate: on disconnect because a pause icon
        // describing a PC the phone can no longer reach is a claim it cannot back, and on connect
        // because a host switch drives this callback false-then-true, so the state of the PREVIOUS PC
        // would otherwise be shown against the new one until it happened to report. A fresh host sends
        // its current reading as soon as its stream starts, so the blank is brief and honest.
        _mediaState.value = MediaPlaybackSnapshot.Unknown
        // A pending revert timer belongs to the connection that issued the seek; letting it fire
        // later against a since-reset/replaced state would be reverting to the wrong PC's truth.
        seekRevertJob?.cancel()
        lastHostSnapshot = MediaPlaybackSnapshot.Unknown
        lastHostArrivalElapsedMs = 0L
        // [_mediaArtwork] resets here for the same reason as [_mediaState] above; [artworkCache] does
        // NOT — see the KDoc on that field.
        _mediaArtwork.value = null
        // In-flight requests are connection-scoped: a media_artwork_request sent just before the
        // socket dropped never gets a reply, so forget it here rather than letting it block a
        // re-request until its TTL expires. The cached bitmaps themselves are not connection-scoped
        // (see [artworkCache]'s KDoc), so only the in-flight bookkeeping is cleared.
        artworkCache.clearAllInFlight()
    }

    override fun onAuthenticated() {
        // Stamped with the connection that is up NOW, so a collector can tell "A authenticated" from
        // "B authenticated" across a host switch (the epoch is what differs). Native posts this on
        // the same thread as, and strictly after, onConnectionStateChanged(true), so [_connectedHost]
        // is already the right PC. An ack with no socket up is stale and says nothing.
        if (!_isConnected.value) return
        // Flag BEFORE trigger: [_authenticatedConnection] is what the theme collector wakes on, and
        // the sender it then calls reads [_isAuthenticated] as its predicate. Written the other way
        // round, a collector that wins the race sees false and silently drops the one-shot palette
        // send this signal exists to deliver.
        _isAuthenticated.value = true
        _authenticatedConnection.value = _connectedHost.value
    }

    fun setConnecting(isConnecting: Boolean) {
        _isConnecting.value = isConnecting
    }

    /**
     * Names the PC a connect attempt is aimed at, so a subsequent success can be attributed to it.
     *
     * Called by [connect] for every real attempt; exposed because the connect path itself needs a
     * loaded native library and a SettingsManager, which a JVM unit test has neither of.
     */
    internal fun setPendingTarget(host: String, port: Int) {
        pendingTarget = host to port
    }

    override fun onLauncherSync(launcherData: String?) {
        launcherData?.let { _launcherEntries.tryEmit(it) }
    }

    override fun onProcessListSync(processData: String?) {
        processData?.let { _processList.tryEmit(it) }
    }

    override fun onFrameReceived(frame: ByteArray?) {
        if (frame == null) return

        // Log frame arrival (keep it compact to avoid logcat flooding)
        if (System.currentTimeMillis() % 1000 < 50) {
            Log.d("RemexManager", "onFrameReceived: ${frame.size} bytes")
        }

        // JNI delivers a freshly allocated array per frame; no defensive copy needed.
        _frames.tryEmit(frame)
    }

    override fun onHostInfoUpdate(hostInfoData: String?) {
        hostInfoData?.let {
            _hostCapabilities.tryEmit(it)
            captureHostReportedMac(it)
        }
    }

    override fun onMediaState(mediaStateJson: String?) {
        // A null payload is not a reading. The native router only forwards a media_state whose
        // payload survived deserialization, so this should not arrive - and if it does, holding the
        // previous state is better than blanking a good icon on a message that said nothing.
        val snapshot =
                mediaStateJson?.let {
                    MediaPlaybackSnapshot.parse(it, SystemClock.elapsedRealtime())
                }
                        ?: return
        // One line per envelope, on purpose: the host gate is meant to make these rare (a seek, a
        // pause, a track change), and a stream of them once a second is the per-second broadcast
        // spec 1.3 guards against. Counting this tag in logcat is how that is checked on a device -
        // Log.i, not Log.d/v (perf audit P3-9's release strip rule strips those two, and this is
        // checked against a release install, not a debug one).
        Log.i(
                "RemexManager",
                "media_state: status=${snapshot.status} title=${snapshot.title} pos=${snapshot.positionMs} dur=${snapshot.durationMs} art=${snapshot.artworkId}"
        )
        _mediaState.value = snapshot
        // This IS the host's own truth arriving - it settles any optimistic seek outright, whether
        // it agrees with the guess or not (spec 1.3: never left standing as an unconfirmed claim).
        lastHostSnapshot = snapshot
        lastHostArrivalElapsedMs = snapshot.receivedAtElapsedMs
        seekRevertJob?.cancel()
        reconcileArtwork(snapshot)
    }

    /**
     * Drags the now-playing sheet's slider to [positionMs] (RemEx-vtorl). Fires the JNI seek
     * export (result ignored — [PingPongHandler]'s reply IS the next `media_state`, not a direct
     * ack) and immediately shows an OPTIMISTIC snapshot so the bar and sheet jump at once, rather
     * than waiting out a round trip. If [onMediaState] has not delivered a fresher host reading
     * within [SeekConfirmWindowMs], [MediaSeekReconciler.shouldRevert] says the host ignored the
     * seek (some sessions on Windows do), and the guess is snapped back to [lastHostSnapshot]
     * rather than left standing as a claim the phone cannot back.
     *
     * A session that reports [MediaPlaybackSnapshot.canSeek] false is refused HERE as well as being
     * disabled in the UI. The revert above is a backstop for a session that lies; this is the case
     * where the host has already said it will not move, and sending anyway would buy a guaranteed
     * 2.5 s of bouncing bar. The sheet disables the slider on the same flag, so reaching this line
     * means something else called it, not that the user got past a disabled control.
     */
    fun seekMedia(positionMs: Long) {
        val current = _mediaState.value
        if (!current.canSeek) {
            Log.d("RemexManager", "media_seek ignored: session cannot seek")
            return
        }
        val issuedAtElapsedMs = SystemClock.elapsedRealtime()
        // MediaSeekReconciler.optimistic owns the one clamp to [0, durationMs]; read the clamped
        // value back off its result rather than clamping a second time here (RemEx-vtorl review).
        val optimistic = MediaSeekReconciler.optimistic(current, positionMs, issuedAtElapsedMs)
        val clampedPositionMs = optimistic.positionMs ?: positionMs
        Log.d("RemexManager", "media_seek: $clampedPositionMs")
        RemexCoreClient.SeekMedia(clampedPositionMs)

        _mediaState.value = optimistic

        seekRevertJob?.cancel()
        seekRevertJob =
                managerScope.launch {
                    delay(SeekConfirmWindowMs)
                    if (MediaSeekReconciler.shouldRevert(issuedAtElapsedMs, lastHostArrivalElapsedMs)) {
                        // compareAndSet, not a plain write: a confirming media_state can land on the
                        // JNI thread between the shouldRevert read and this line, and a plain write
                        // would overwrite that fresh host reading with the pre-seek one. If the flow
                        // no longer holds our optimistic instance, someone newer won; leave it.
                        _mediaState.compareAndSet(optimistic, lastHostSnapshot)
                    }
                }
    }

    /**
     * Keeps [mediaArtwork] pointed at the bitmap for [snapshot]'s `artworkId`: served from
     * [artworkCache] when present, requested through the narrow JNI export
     * ([RemexCoreClient.RequestMediaArtwork]) when it is worth asking for, and null in every other
     * case — including while a request is in flight, so the UI never shows a stale bitmap under a
     * new id.
     */
    private fun reconcileArtwork(snapshot: MediaPlaybackSnapshot) {
        val id = snapshot.artworkId
        if (id == null) {
            _mediaArtwork.value = null
            return
        }
        val cached = artworkCache.get(id)
        if (cached != null) {
            _mediaArtwork.value = cached
            return
        }
        _mediaArtwork.value = null
        if (artworkCache.tryBeginRequest(id, SystemClock.elapsedRealtime())) {
            Log.d("RemexManager", "media_artwork_request: $id")
            RemexCoreClient.RequestMediaArtwork(id)
        }
    }

    /**
     * Delivers the answer to [RemexCoreClient.RequestMediaArtwork]: one JSON string
     * `{artworkId, pngBase64}`, never pushed unsolicited. Dispatches to [Dispatchers.Default] (perf
     * audit P0-13, so a multi-MB reply's JSON parse and decode never run synchronously on the JNI
     * delivery thread), so every failure here is silent - malformed JSON, a blank id, bad base64, or
     * a decode that returns null all land on doing nothing rather than throwing off that thread.
     *
     * A missing `pngBase64` means the host has evicted the id; that is remembered via
     * [MediaArtworkCache.markEvicted] so [reconcileArtwork] does not keep re-requesting it. A bitmap
     * that fails to decode is treated the same way, per contract.
     */
    override fun onMediaArtwork(mediaArtworkJson: String?) {
        // Perf audit P0-13: JSONObject(...) scans the whole string, including the embedded base64
        // payload, so parsing a multi-MB artwork reply here would run on the JNI data-dispatcher
        // thread the same way the base64 decode used to - moved inside the coroutine, off that
        // thread, alongside the decode it was already deferring.
        managerScope.launch(Dispatchers.Default) {
            val json = mediaArtworkJson?.let { runCatching { JSONObject(it) }.getOrNull() } ?: return@launch
            val id = json.optString("artworkId").ifBlank { null } ?: return@launch
            val base64 =
                    if (json.has("pngBase64") && !json.isNull("pngBase64")) {
                        json.optString("pngBase64").ifBlank { null }
                    } else {
                        null
                    }
            Log.d("RemexManager", "media_artwork: $id bytes=${base64?.length ?: 0}")
            if (base64 == null) {
                artworkCache.markEvicted(id)
                return@launch
            }
            val bitmap = decodeDownsampledArtwork(base64)
            if (bitmap == null) {
                artworkCache.markEvicted(id)
                return@launch
            }
            artworkCache.put(id, bitmap)
            // The reply may answer a track that has since changed; only publish it if it is still
            // the one the current snapshot wants.
            if (_mediaState.value.artworkId == id) {
                _mediaArtwork.value = bitmap
            }
        }
    }

    /**
     * Decodes base64 artwork (PNG or JPEG - `BitmapFactory` is format-agnostic, no host transcoding
     * per contract) down to at most [MaxArtworkDimensionPx] on its longest side, so that up to four
     * cached bitmaps ([artworkCache]'s default capacity) stay a bounded amount of memory even against
     * a full-resolution album cover. Follows [com.clindsay94.remex.ui.screens.rememberAppIconBitmap]'s
     * decode shape (`Base64.decode` + `BitmapFactory`, null on any failure) with sampling added.
     */
    private fun decodeDownsampledArtwork(base64: String): Bitmap? =
            try {
                val bytes = Base64.decode(base64, Base64.DEFAULT)
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
                var sampleSize = 1
                val longestSide = maxOf(bounds.outWidth, bounds.outHeight)
                while (longestSide / sampleSize > MaxArtworkDimensionPx) {
                    sampleSize *= 2
                }
                val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
            } catch (_: Exception) {
                null
            }

    /**
     * Remembers the MAC the host just reported, so Wake-on-LAN needs no setup step (RemEx-izuj).
     *
     * Stored under its own key: [SettingsManager.saveHostReportedMacAddress] never touches a MAC the
     * user typed, and [SettingsManager.macAddressFlow] prefers the manual one when there is one. A
     * blank is ignored rather than stored - that is how a host with no suitable adapter says "ask
     * the user", and overwriting a good value with it would be worse than not listening at all.
     *
     * Deliberately quiet on malformed input: this runs on a JNI callback for every host_info, and a
     * capability payload that cannot be parsed must not take the connection down over an optional
     * convenience field.
     */
    private fun captureHostReportedMac(hostInfoJson: String) {
        val manager = settingsManager ?: return
        val mac = runCatching { org.json.JSONObject(hostInfoJson).optString("macAddress") }
                .getOrNull()
                .orEmpty()
        if (mac.isBlank()) return
        managerScope.launch { runCatching { manager.saveHostReportedMacAddress(mac) } }
    }

    override fun onDesktopError(errorText: String?) {
        errorText?.let { _desktopErrors.tryEmit(it) }
    }

    override fun onDesktopMeta(metaData: String?) {
        metaData?.let { _desktopMeta.tryEmit(it) }
    }

    override fun onDesktopWindowResult(resultData: String?) {
        resultData?.let { _desktopWindowResults.tryEmit(it) }
    }

    override fun onDesktopStreamDescriptor(descriptor: String?) {
        descriptor?.let { _desktopStreamDescriptor.tryEmit(it) }
    }

    override fun onDesktopDisplayCatalog(catalogJson: String?) {
        catalogJson?.let { _desktopDisplayCatalog.tryEmit(it) }
    }

    override fun onDesktopCursorState(stateJson: String?) {
        stateJson?.let { _desktopCursorState.tryEmit(it) }
    }

    override fun onDesktopCursorBinary(packet: ByteArray?) {
        packet?.let { _desktopCursorBinary.tryEmit(it) }
    }

    override fun onDesktopCursorShape(shapeJson: String?) {
        shapeJson?.let { _desktopCursorShape.tryEmit(it) }
    }

    override fun onFileTransferMessage(json: String?) {
        json?.let { _fileTransferMessages.tryEmit(it) }
    }

    override fun onClipboardMessage(json: String?) {
        json?.let { _clipboardMessages.tryEmit(it) }
    }

    override fun onLinkQuality(json: String?) {
        // Parsed defensively and dropped on anything unexpected. This is a telemetry reading, not a
        // decision input - a malformed one is worth ignoring, never worth throwing out of a JNI
        // callback where nothing can catch it.
        val ms =
                json?.let { runCatching { JSONObject(it) }.getOrNull() }
                        ?.takeIf { it.has("roundTripMs") }
                        ?.optDouble("roundTripMs", Double.NaN)
                        ?.takeIf { !it.isNaN() && it >= 0.0 }
        if (ms != null) _roundTripMs.value = ms
    }

    override fun onConnectionError(reason: String?) {
        // Auth pair first, for the same reason as onConnectionStateChanged: never connected=false
        // with authenticated=true.
        _isAuthenticated.value = false
        _authenticatedConnection.value = null
        _isConnected.value = false
        _isConnecting.value = false
        _connectedHost.value = null
        reason?.let { _connectionError.tryEmit(it) }
    }

    override fun onPairingProgress(phase: String?) {
        // Relayed rather than interpreted. Only ONE callback is registered natively, and this object
        // holds it — so the pairing screen cannot receive these directly and reads them off this
        // flow instead. Mapping a token to a sentence is the screen's job, not this one's.
        _pairingProgress.tryEmit(phase)
    }
}
