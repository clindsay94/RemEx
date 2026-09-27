package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunSourceDetail
import com.clindsay94.remex.routines.model.RoutineStepResultPayload
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow

// The seams between the phone runner and Android (routines spec §8.2, RemEx-pp0rt.5). The runner and
// the repository are pure Kotlin against these, so RoutineRunnerTest drives the whole step sequence
// on virtual time with fakes; the Android implementations live in RoutineAndroidPorts.kt.

/** Wall time for display and history; elapsed realtime for every duration and timeout (§8.1). */
interface RoutineClock {
    fun nowUnixMs(): Long

    fun elapsedRealtimeMs(): Long
}

/**
 * What the worker's input data carries (§8.2 "Input data"). [attempt] is WorkManager's run attempt
 * count: a retry of a run whose record already shows a started step is `interrupted_phone`.
 */
data class RoutineRunTicket(
    val runId: String,
    val routineId: String,
    val source: String,
    val testRun: Boolean,
    val triggeredAtUnixMs: Long,
    val triggeredAtElapsedMs: Long,
    val sourceDetail: RoutineRunSourceDetail? = null,
    val attempt: Int = 0,
)

/** The runner's view of the phone routine store. */
interface RoutineRunStore {
    suspend fun findRun(runId: String): RoutineRun?

    suspend fun routine(routineId: String): Routine?

    suspend fun isPausedAll(): Boolean

    /** `hostSync[host].reachableAwayAtUnixMs` (§8.3.1), used to pick `pc_unreachable_away`. */
    suspend fun reachableAwayAtUnixMs(hostIdentity: String): Long?

    /**
     * Stores [run], replacing the record with the same id. A stored record that is already FINAL
     * (anything but `running`) is never overwritten, and is returned instead: whoever finalised a run
     * first (the runner, or a cancel of a run that never started) owns its outcome.
     */
    suspend fun recordRun(run: RoutineRun): RoutineRun
}

/** The control connection to the PC, as the runner needs it. */
interface RoutineHostLink {
    /** True once the PC has acked this connection's reconnect proof (`isAuthenticated`). */
    val authenticated: StateFlow<Boolean>

    /** Every `routine_step_result`, on its own flow so run-report bursts cannot evict one. */
    val stepResults: Flow<RoutineStepResultPayload>

    /** The host identity of the PC this phone is authenticated to right now, else null. */
    suspend fun authenticatedHostIdentity(): String?

    /** The host identity of the PC RemEx is set to (the only PC the native client connects to). */
    suspend fun selectedHostIdentity(): String?

    /** Whether a pairing pin still exists for any address of [hostIdentity]. */
    suspend fun isPaired(hostIdentity: String): Boolean

    /** The user's nickname for [hostIdentity], or null (never a hostname; T10). */
    suspend fun displayName(hostIdentity: String): String?

    /** One connect attempt to the selected PC; false when there is no PC to connect to. */
    suspend fun startOneShotConnect(): Boolean

    /**
     * Whether [hostIdentity] advertises `supportsRoutines` (§7.5), read ONLY from a `host_info` that
     * arrived on the current connection to that very PC. A cached one from an earlier connection or
     * another PC is ignored: trusting it reported `pc_too_old` or sent steps to a PC that never
     * answers them.
     */
    suspend fun supportsRoutines(hostIdentity: String): Boolean

    /** Queues [json] on the control socket; false when it could not be queued. */
    fun send(json: String): Boolean
}

/** What the runner does on the phone itself. */
interface RoutinePhone {
    /** False when Android's local-network permission is denied (`permission_local_network`). */
    fun hasLocalNetworkPermission(): Boolean

    /** Broadcasts one magic packet; true when it left the phone (not that the PC woke). */
    suspend fun sendWakePacket(mac: String, broadcastIp: String, port: Int): Boolean

    /** Posts a `notify(phone)` message; false when notifications are off for RemEx. */
    fun postMessage(run: RoutineRun, stepIndex: Int, title: String, body: String): Boolean

    fun isBackgroundRestricted(): Boolean

    /**
     * Whether the phone is away from its saved home network right now (§8.3.1). With no home saved
     * the phone is never "away", so the answer is false; the S3 presence slice supplies the match.
     */
    suspend fun isAwayFromHome(): Boolean
}

/** Progress and results, for the notifications (§1.7) and the in-app run sheet. */
interface RoutineRunObserver {
    /** A run started ([stepIndex] null) or moved on to [stepIndex]. */
    fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?)

    /** A destructive host step was sent: the PC counts down until [endsAtUnixMs] (§8.6 phone mirror). */
    fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long)

    /** A run reached a final outcome, including a start that was skipped. */
    fun onFinished(run: RoutineRun, routine: Routine?)
}
