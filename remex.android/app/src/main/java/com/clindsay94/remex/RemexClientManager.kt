package com.clindsay94.remex

import android.content.Context
import android.util.Log
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.RemexConnectionService
import com.clindsay94.remex.ui.screens.PairingErrors
import com.clindsay94.remex.ui.screens.PairingSurface
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.json.JSONObject

object RemexClientManager : RemexCoreClient.RemexCallback {

    private val managerScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var settingsManager: SettingsManager? = null

    private val _isConnected = MutableStateFlow(false)
    val isConnected = _isConnected.asStateFlow()

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting = _isConnecting.asStateFlow()

    private val _telemetry = MutableSharedFlow<String>(replay = 1)
    val telemetry = _telemetry.asSharedFlow()

    private val _launcherEntries = MutableSharedFlow<String>(replay = 1)
    val launcherEntries = _launcherEntries.asSharedFlow()

    private val _processList = MutableSharedFlow<String>(replay = 1)
    val processList = _processList.asSharedFlow()

    private val _frames =
            MutableSharedFlow<ByteArray>(
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

    private val _desktopStreamDescriptor = MutableSharedFlow<String>(replay = 1)
    val desktopStreamDescriptor = _desktopStreamDescriptor.asSharedFlow()

    private val _desktopDisplayCatalog = MutableSharedFlow<String>(replay = 1)
    val desktopDisplayCatalog = _desktopDisplayCatalog.asSharedFlow()

    private val _desktopCursorState = MutableSharedFlow<String>(replay = 1)
    val desktopCursorState = _desktopCursorState.asSharedFlow()

    // RD-E: raw binary "RDXC" cursor-position packets. replay=1 so a late collector gets the last position.
    private val _desktopCursorBinary = MutableSharedFlow<ByteArray>(replay = 1)
    val desktopCursorBinary = _desktopCursorBinary.asSharedFlow()

    private val _desktopCursorShape = MutableSharedFlow<String>(replay = 1)
    val desktopCursorShape = _desktopCursorShape.asSharedFlow()

    private val _desktopWindowResults = MutableSharedFlow<String>(replay = 0, extraBufferCapacity = 8)
    val desktopWindowResults = _desktopWindowResults.asSharedFlow()

    init {
        RemexCoreClient.setCallback(this)
    }

    fun initialize(context: Context) {
        if (settingsManager != null) return

        settingsManager = SettingsManager(context)

        managerScope.launch {
            val host = settingsManager?.hostFlow?.first().orEmpty()
            if (host.isNotBlank()) {
                runCatching { RemexConnectionService.start(context) }
                        .onFailure { Log.w("RemexManager", "Failed to start keepalive service during initialize", it) }
            }
        }

        // Start Global Connection Heartbeat with exponential backoff.
        // Interval = min(BASE_DELAY_MS * 2^failures, MAX_DELAY_MS).
        // The failure counter resets to 0 whenever the device is connected.
        managerScope.launch(Dispatchers.IO) {
            var consecutiveFailures = 0
            val baseDelayMs = 5_000L
            val maxDelayMs = 300_000L // 5 minutes

            while (true) {
                if (isConnected.value) {
                    // Connected — reset backoff and poll at base rate
                    consecutiveFailures = 0
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
                    delay(baseDelayMs)
                    continue
                }

                // 2^20 * 5000ms ≈ 87 minutes, which already exceeds maxDelayMs (5 min),
                // so coerceAtMost(20) safely avoids Int overflow on the shift.
                val backoffMs =
                        minOf(
                                baseDelayMs * (1L shl consecutiveFailures.coerceAtMost(20)),
                                maxDelayMs
                        )

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

                                Log.i("RemexManager", "Discovered host is verified and trusted. Updating saved address to: ${discovered.host}:${discovered.port}")
                                settings.saveConnectionSettings(
                                    host = discovered.host,
                                    port = discovered.port,
                                    mac = currentMac,
                                    broadcast = currentBroadcast,
                                    subnetMask = currentSubnet
                                )
                                // Address updated; reset failures and proceed to connect immediately
                                consecutiveFailures = 0
                            } else {
                                Log.w("RemexManager", "Discovered host '${discovered.serviceName}' is not verified, skipping auto-update.")
                            }
                        } else {
                            Log.d("RemexManager", "Self-healing discovery found no hosts.")
                        }
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
                delay(backoffMs)
            }
        }
    }

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

    private val _connectionError =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 1,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val connectionError = _connectionError.asSharedFlow()

    private val _fileTransferMessages =
            MutableSharedFlow<String>(
                    extraBufferCapacity = 8,
                    onBufferOverflow = BufferOverflow.DROP_OLDEST
            )
    val fileTransferMessages = _fileTransferMessages.asSharedFlow()

    private suspend fun connect(pairingPin: String? = null, isAutoConnect: Boolean = false) {
        val settings = settingsManager ?: run {
            _isConnecting.value = false
            return
        }
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()
        val clientId = settings.getOrCreateClientId()

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
                                                "Android Client",
                                                "2.0.0",
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
        _isConnected.value = isConnected
        _isConnecting.value = false
        // _pairingRequired is a replay=1 SharedFlow, so a stale "pairing needed" event would
        // otherwise linger in the replay cache and re-fire every time a fresh collector
        // subscribes (e.g. MainActivity recreated by a widget tap), wrongly throwing the user
        // onto the PIN screen even while paired/connected. Clear it once we're connected.
        if (isConnected) {
            _pairingRequired.resetReplayCache()
        }
    }

    fun setConnecting(isConnecting: Boolean) {
        _isConnecting.value = isConnecting
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

    override fun onConnectionError(reason: String?) {
        _isConnected.value = false
        _isConnecting.value = false
        reason?.let { _connectionError.tryEmit(it) }
    }
}
