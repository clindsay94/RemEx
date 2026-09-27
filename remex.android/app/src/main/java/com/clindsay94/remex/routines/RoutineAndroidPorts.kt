package com.clindsay94.remex.routines

import android.app.ActivityManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import androidx.core.content.ContextCompat
import com.clindsay94.remex.EstablishedConnection
import com.clindsay94.remex.HostInfoForConnection
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineStepResultPayload
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import com.clindsay94.remex.tile.sendWakePacketFromPhone
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject

// The Android side of the runner's ports (RoutineRunPorts.kt).

internal object SystemRoutineClock : RoutineClock {
    override fun nowUnixMs(): Long = System.currentTimeMillis()

    override fun elapsedRealtimeMs(): Long = SystemClock.elapsedRealtime()
}

/**
 * The runner's view of the control connection: [RemexClientManager] and [RemexCoreClient], plus the
 * pin store for "which PC is this". Uses the headless one-shot connect the widgets and tiles use
 * ([RemexClientManager.startOneShotConnect]): one attempt, never pairs, never reopens the heartbeat.
 */
internal class RemexRoutineHostLink(context: Context) : RoutineHostLink {
    private val appContext = context.applicationContext
    private val settings = SettingsManager(appContext)

    override val authenticated: StateFlow<Boolean> get() = RemexClientManager.isAuthenticated

    override val stepResults: Flow<RoutineStepResultPayload> get() = RemexClientManager.routineStepResults

    override suspend fun authenticatedHostIdentity(): String? {
        val host = RemexClientManager.authenticatedConnection.value?.host?.takeIf { it.isNotBlank() } ?: return null
        return HostIdentity.keyFor(PinnedHostStore.getPin(appContext, host))
    }

    override suspend fun selectedHostIdentity(): String? {
        val host = settings.hostFlow.first().takeIf { it.isNotBlank() } ?: return null
        return HostIdentity.keyFor(PinnedHostStore.getPin(appContext, host))
    }

    override suspend fun isPaired(hostIdentity: String): Boolean =
        PinnedHostStore.listPaired(appContext).values.any { HostIdentity.keyFor(it) == hostIdentity }

    override suspend fun displayName(hostIdentity: String): String? =
        settings.knownHostRecordsFlow.first()[hostIdentity]?.nickname?.takeIf { it.isNotBlank() }

    override suspend fun startOneShotConnect(): Boolean = RemexClientManager.startOneShotConnect(appContext)

    /**
     * `supportsRoutines` from a `host_info` that arrived on THIS authenticated connection to
     * [hostIdentity]. Waits briefly for one on a fresh connection; none means an older PC
     * (`pc_too_old`, §7.6). Never the replaying [RemexClientManager.hostCapabilities], which can
     * still hold the previous PC's answer.
     */
    override suspend fun supportsRoutines(hostIdentity: String): Boolean {
        val connection = RemexClientManager.authenticatedConnection.value ?: return false
        val identity = HostIdentity.keyFor(PinnedHostStore.getPin(appContext, connection.host))
        val info =
            withTimeoutOrNull(CAPABILITIES_WAIT_MS) {
                RemexClientManager.hostInfoForConnection.first {
                    RoutineHostCapabilities.supportsRoutines(it, connection, identity, hostIdentity) != null
                }
            }
        return RoutineHostCapabilities.supportsRoutines(info, connection, identity, hostIdentity) ?: false
    }

    override fun send(json: String): Boolean {
        val response = RemexCoreClient.SendMessage(json).getOrNull() ?: return false
        return runCatching { JSONObject(response).optBoolean("success", false) }.getOrDefault(false)
    }

    private companion object {
        const val CAPABILITIES_WAIT_MS = 5_000L
    }
}

/** The capability decision behind [RemexRoutineHostLink.supportsRoutines]. Pure JVM. */
internal object RoutineHostCapabilities {
    /**
     * `supportsRoutines` from [info], or null when [info] cannot answer for [target]: nothing has
     * arrived, it arrived on an earlier connection (another epoch), or the authenticated PC
     * ([authenticatedIdentity]) is not the one the run targets.
     */
    fun supportsRoutines(
        info: HostInfoForConnection?,
        authenticated: EstablishedConnection?,
        authenticatedIdentity: String?,
        target: String,
    ): Boolean? {
        if (info == null || authenticated == null) return null
        if (info.connection.epoch != authenticated.epoch || authenticatedIdentity != target) return null
        return runCatching { JSONObject(info.json).optBoolean("supportsRoutines", false) }.getOrDefault(false)
    }
}

internal class AndroidRoutinePhone(
    context: Context,
    private val presenter: RoutineNotificationPresenter,
) : RoutinePhone {
    private val appContext = context.applicationContext

    override fun hasLocalNetworkPermission(): Boolean =
        // Android 17 (API 37) gates LAN access behind ACCESS_LOCAL_NETWORK; before it there is nothing to deny.
        Build.VERSION.SDK_INT < 37 ||
            ContextCompat.checkSelfPermission(appContext, "android.permission.ACCESS_LOCAL_NETWORK") == PackageManager.PERMISSION_GRANTED

    override suspend fun sendWakePacket(mac: String, broadcastIp: String, port: Int): Boolean =
        sendWakePacketFromPhone(mac, broadcastIp, port)

    override fun postMessage(run: RoutineRun, stepIndex: Int, title: String, body: String): Boolean =
        presenter.postMessage(run, stepIndex, title, body)

    override fun isBackgroundRestricted(): Boolean =
        appContext.getSystemService(ActivityManager::class.java)?.isBackgroundRestricted ?: false

    /**
     * No presence state exists on the phone until a home is captured and matched (S3), and a phone
     * with no home cannot be away from it, so this is false and `pc_unreachable` is chosen (§10.1).
     */
    override suspend fun isAwayFromHome(): Boolean = false
}
