package com.clindsay94.remex.ui.splash

import android.content.Context
import android.graphics.RenderEffect
import android.graphics.RuntimeShader
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asComposeRenderEffect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn

/**
 * Where the portal lens sits this frame (RemEx-8g6n0). Written by the splash's frame loop, read
 * inside the content's graphicsLayer block, so a change re-renders the layer only — the app
 * underneath is never recomposed by the splash.
 */
@Stable
class LiveHandshakeLensState {
    var active by mutableStateOf(false)
        private set
    var centerX by mutableFloatStateOf(0f)
        private set
    var centerY by mutableFloatStateOf(0f)
        private set
    var radius by mutableFloatStateOf(0f)
        private set
    var scale by mutableFloatStateOf(1f)
        private set

    fun set(cx: Float, cy: Float, r: Float, s: Float) {
        centerX = cx
        centerY = cy
        radius = r
        scale = s
        active = true
    }

    fun clear() {
        active = false
    }
}

/**
 * The portal lens on the app content: while [state] is active the content refracts through the
 * portal's rim and settles from 1.12x to 1x (`res/raw/live_handshake_lens.agsl`). A fresh
 * [RenderEffect] per frame (the effect snapshots the shader's uniforms when it is created); none
 * at all once the splash is done, so the content layer is left exactly as it was.
 */
@Composable
fun rememberLiveHandshakeLensModifier(state: LiveHandshakeLensState): Modifier {
    val context = LocalContext.current
    val px = LocalDensity.current.density
    // Compiled on first use, not at app start: most launches never open a portal.
    val shader = remember { lazy { RuntimeShader(readRawText(context, R.raw.live_handshake_lens)) } }
    return Modifier.graphicsLayer {
        if (state.active) {
            val s = shader.value
            s.setFloatUniform("uCenter", state.centerX, state.centerY)
            s.setFloatUniform("uRadius", state.radius)
            s.setFloatUniform("uScale", state.scale)
            s.setFloatUniform("uPx", px)
            renderEffect = RenderEffect.createRuntimeShaderEffect(s, "content").asComposeRenderEffect()
        } else {
            renderEffect = null
        }
    }
}

internal fun readRawText(context: Context, resId: Int): String =
    context.resources.openRawResource(resId).bufferedReader(Charsets.UTF_8).use { it.readText() }

/**
 * One Live Handshake run over the real app: the signals it observes, the lens it drives, and a
 * scope that lives exactly as long as the splash does.
 */
class LiveHandshakeSession(context: Context, val lens: LiveHandshakeLensState) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    val signals: LiveHandshakeSignals = androidLiveHandshakeSignals(context.applicationContext, scope)

    fun markReady() = signals.markReady()

    fun close() {
        lens.clear()
        scope.cancel()
    }
}

/**
 * Owns the Live Handshake overlay for the app shell (RemEx-8g6n0): the Splash route [start]s it
 * on a cold start, the shell marks it ready when the destination underneath has composed, and the
 * overlay [finish]es it.
 */
@Stable
class LiveHandshakeController(private val appContext: Context) {
    val lens = LiveHandshakeLensState()

    var session by mutableStateOf<LiveHandshakeSession?>(null)
        private set

    fun start() {
        if (session == null) session = LiveHandshakeSession(appContext, lens)
    }

    fun finish() {
        session?.close()
        session = null
        lens.clear()
    }
}

/** Signals wired to this app: the paired-PC store, the TCP probe, and the client's own flows. */
internal fun androidLiveHandshakeSignals(context: Context, scope: CoroutineScope): LiveHandshakeSignals {
    val authenticatedHost = RemexClientManager.authenticatedConnection
        .map { it?.host }
        .stateIn(scope, SharingStarted.Eagerly, RemexClientManager.authenticatedConnection.value?.host)
    return LiveHandshakeSignals(
        scope = scope,
        loadPeers = { loadAndroidPeers(context) },
        probe = TcpPeerReachabilityProbe(),
        authenticatedHost = authenticatedHost,
        isConnecting = RemexClientManager.isConnecting,
        isConnected = RemexClientManager.isConnected,
    )
}

/**
 * The paired PCs, one per machine, most recently used first, named by [HandshakePeer.displayNameFor]
 * (nickname, then machine name, as the Known PCs rows do). The identity key is an opaque hash and never shown.
 */
private suspend fun loadAndroidPeers(context: Context): PeerSetup {
    val settings = SettingsManager(context)
    val paired = PinnedHostStore.listPaired(context)
    val records = settings.knownHostRecordsFlow.first()
    val saved = settings.hostFlow.first().trim().takeIf { it.isNotEmpty() }
    val peers = KnownHosts.build(paired, records).map { host ->
        HandshakePeer(
            id = host.identity,
            name = HandshakePeer.displayNameFor(host.nickname, host.addresses, host.machineName),
            addresses = host.addresses,
            port = host.port,
        )
    }
    return PeerSetup(peers, saved)
}
