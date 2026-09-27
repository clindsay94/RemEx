package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineCancelPayload
import com.clindsay94.remex.routines.model.RoutineCancelReasons
import com.clindsay94.remex.routines.model.RoutineOutbound
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunReportPayload
import com.clindsay94.remex.routines.model.RoutineRunRequestPayload
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineSchema
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineSyncResultPayload
import com.clindsay94.remex.routines.model.RoutineSyncStatuses
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.routines.model.RoutinesSyncPayload
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** How a phone Run or Test of a PC-run routine went (§7.3.8, D8). */
sealed interface RoutinePcRunStart {
    /** The PC started it; progress arrives as live run reports. */
    data class Started(val runId: String) : RoutinePcRunStart

    /** The PC answered with a skipped record ([reasonCode]: `routine_not_found`, `disabled_on_pc`, ...). */
    data class Skipped(val reasonCode: String, val runId: String) : RoutinePcRunStart

    /**
     * The routine controls [hostIdentity], and RemEx is set to another PC. Never switched
     * automatically (D8): the person chooses "Switch and run".
     */
    data class NotSelected(val hostIdentity: String) : RoutinePcRunStart

    /** Nothing was started: `pc_unreachable`, `pc_too_old`, `pc_not_paired`, `transport_lost`. */
    data class Failed(val reasonCode: String) : RoutinePcRunStart

    data object NotFound : RoutinePcRunStart
}

/** A "Switch and run" that did not start, for the Routines screen to say why (RemEx-pp0rt.12 review). */
data class RoutineSwitchRunOutcome(val routineId: String, val testRun: Boolean, val start: RoutinePcRunStart)

/** What forgetting a PC did about its routines (§7.4.5, T8). */
enum class RoutinePcForget {
    /** No routine was bound to that PC. */
    NOTHING,

    /** The PC confirmed it deleted this phone's routines and history before the unpair. */
    FLUSHED,

    /**
     * Removed from this phone only: the PC was not connected (or did not answer in time), so it keeps
     * its copy until the 30-day owner-absent suspension or a revoke on the PC. The forget flow says so.
     */
    PHONE_ONLY,
}

/**
 * The phone's sync client for PC-run routines (routines spec §7.3.1, §7.3.8, §7.4; RemEx-pp0rt.12).
 *
 * **One session per authenticated connection**, started by [run] from `RemexClientManager.initialize`.
 * A session sends `routines_sync` once on connect (when the PC advertises `supportsRoutines`) and
 * again after every edit of that PC's set or the pause flag, coalesced to one per 2 s. A new
 * connection cancels the old session, so nothing is ever sent for a PC the phone is no longer
 * talking to.
 *
 * **Answers** ([onSyncResult]) are stored by the repository ([RoutineSyncProtocol]); this class only
 * decides when to send again: once after `stale_revision` / `revision_conflict`, with a back-off after
 * the PC's not-ready answer (`rate_limited` while it starts; never a hot loop), and not at all after
 * `blocked_by_pc` and friends until the PC says otherwise.
 *
 * **Never throws** out of [onSyncResult] / [onRunReport]: they run inside the one routine message
 * collector, which must outlive every bad message.
 */
internal class RoutineSyncClient(
    private val store: RoutineRepository,
    private val link: RoutineHostLink,
    /** The authenticated connection's epoch, or null while there is none (`authenticatedConnection`). */
    private val connections: Flow<Long?>,
    private val observer: RoutineRunObserver,
    private val clock: RoutineClock,
    private val newId: () -> String = { UUID.randomUUID().toString() },
    /** Where the blocking JNI sends run; tests pass their own scheduler's dispatcher. */
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private class Session(val hostIdentity: String, val scope: CoroutineScope) {
        val wake = Channel<Unit>(Channel.CONFLATED)

        @Volatile var lastSentRevision = 0L

        @Volatile var forceResend = false

        @Volatile var resendsLeft = 1

        @Volatile var retryAttempt = 0

        /** No more sends on this connection (a set the PC refuses, or a revision fight that did not settle). */
        @Volatile var stopped = false

        fun kick() {
            wake.trySend(Unit)
        }
    }

    private class ForgetWaiter(val hostIdentity: String, val revision: Long, val answer: CompletableDeferred<RoutineSyncResultPayload>)

    private data class PendingPcRun(val routineId: String, val hostIdentity: String, val testRun: Boolean, val atElapsedMs: Long)

    @Volatile private var session: Session? = null

    @Volatile private var forgetWaiter: ForgetWaiter? = null

    private val forgetting: MutableSet<String> = ConcurrentHashMap.newKeySet()
    private val runWaiters = ConcurrentHashMap<String, CompletableDeferred<RoutineRun>>()

    @Volatile private var pendingSwitchRun: PendingPcRun? = null

    /** Runs for the life of the process: one sync session per authenticated connection. */
    suspend fun run() {
        connections.distinctUntilChanged().collectLatest { epoch ->
            if (epoch == null) return@collectLatest
            try {
                runSession()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // A failed session must not end the collection: the next connection gets a new one.
                RoutineLog.e("The routine sync session failed.", e)
            }
        }
    }

    private suspend fun runSession() = coroutineScope {
        // A pending "Switch and run" belongs to the FIRST authenticated connection after it was
        // armed, whichever PC that is, and is gone after it. Left pending past a connection to
        // another PC, a later reconnect to the target for any reason would start a shut down nobody
        // asked for again (RemEx-pp0rt.12 review).
        val pending = consumePendingSwitchRun()
        val host = link.authenticatedHostIdentity() ?: return@coroutineScope
        if (pending != null && pending.hostIdentity == host) {
            launch {
                val start = runOnPc(pending.routineId, pending.testRun)
                if (start !is RoutinePcRunStart.Started) _switchRunOutcomes.value = RoutineSwitchRunOutcome(pending.routineId, pending.testRun, start)
            }
        }
        // An older PC never hears a routine message (§7.6): it would answer with a command_response
        // nobody reads, and "Waiting to sync" is the honest state for it.
        if (!link.supportsRoutines(host)) return@coroutineScope
        val s = Session(host, this)
        session = s
        try {
            launch {
                store.hostSync.map { it[host]?.localRevision ?: 0L }.distinctUntilChanged().collect { s.kick() }
            }
            var lastSentAt: Long? = null
            for (signal in s.wake) {
                lastSentAt?.let { at ->
                    val wait = MIN_SEND_INTERVAL_MS - (clock.elapsedRealtimeMs() - at)
                    if (wait > 0) delay(wait)
                }
                if (s.stopped || host in forgetting) continue
                val snapshot = store.syncSnapshot(host) ?: continue
                // Revision 0 is "no books for this PC": it never had a PC-run routine from this phone
                // (the first such edit makes revision 1, §7.3.1), or it was just forgotten, when
                // sending would recreate this phone's set on the PC. A phone with no PC routines
                // therefore never sends anything.
                if (snapshot.revision <= 0) continue
                if (snapshot.revision == s.lastSentRevision && !s.forceResend) continue
                s.forceResend = false
                if (send(snapshot)) {
                    s.lastSentRevision = snapshot.revision
                    lastSentAt = clock.elapsedRealtimeMs()
                    RoutineLog.i("routines_sync r${snapshot.revision} sent (${snapshot.routines.size} routines${if (snapshot.paused) ", paused" else ""}).")
                }
            }
        } finally {
            if (session === s) session = null
        }
    }

    private suspend fun send(snapshot: RoutineSyncSnapshot): Boolean =
        sendIo(
            RoutineOutbound.routinesSync(
                RoutinesSyncPayload(
                    schemaVersion = RoutineSchema.CURRENT_VERSION,
                    revision = snapshot.revision,
                    paused = snapshot.paused,
                    routines = snapshot.routines,
                    runCursor = snapshot.runCursor,
                    forget = false,
                    sentAtUnixMs = clock.nowUnixMs(),
                ),
            ),
        )

    /** A `routine_sync_result` from the connected PC, solicited or not (§7.3.2). Never throws. */
    suspend fun onSyncResult(result: RoutineSyncResultPayload) {
        try {
            val host = link.authenticatedHostIdentity() ?: return
            forgetWaiter?.let { waiter ->
                if (waiter.hostIdentity == host && result.revision == waiter.revision) {
                    waiter.answer.complete(result)
                    return
                }
            }
            if (host in forgetting) return
            val followUp = store.applySyncResult(host, result)
            val s = session?.takeIf { it.hostIdentity == host } ?: return
            when (followUp) {
                RoutineSyncFollowUp.DONE -> {
                    s.resendsLeft = 1
                    s.retryAttempt = 0
                    val wasStopped = s.stopped
                    s.stopped = false
                    // A PC that just unblocked (or changed its mind unasked) holds an older set than
                    // this phone: send the current one.
                    if (result.unsolicited || wasStopped) {
                        val entry = store.hostSync.value[host]
                        if (entry != null && entry.ackedRevision < entry.localRevision) {
                            s.forceResend = true
                            s.kick()
                        }
                    }
                }
                RoutineSyncFollowUp.RESEND ->
                    if (s.resendsLeft > 0) {
                        s.resendsLeft--
                        s.forceResend = true
                        s.kick()
                    } else {
                        s.stopped = true
                        RoutineLog.w("The PC kept refusing this phone's revision (${result.status}); sync waits for the next connection.")
                    }
                RoutineSyncFollowUp.RETRY_LATER -> scheduleRetry(s, result.status)
                RoutineSyncFollowUp.STOP -> {
                    s.stopped = true
                    RoutineLog.i("The PC refused this phone's routines (${result.status}).")
                }
                RoutineSyncFollowUp.IGNORED -> Unit
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Handling a routine_sync_result failed.", e)
        }
    }

    private fun scheduleRetry(s: Session, status: String?) {
        val attempt = s.retryAttempt
        if (attempt >= MAX_RETRIES) {
            s.stopped = true
            RoutineLog.w("The PC is still not ready for routines ($status); sync waits for the next connection.")
            return
        }
        s.retryAttempt = attempt + 1
        val backoff = RoutineSyncBackoff.delayMs(attempt)
        s.scope.launch {
            delay(backoff)
            s.forceResend = true
            s.kick()
        }
    }

    /**
     * PC runs from a `routine_run_report`, after the repository stored them (§7.3.6). Answers a
     * waiting [runOnPc], and drives the phone's progress notification for PC runs (spec 1.7).
     */
    suspend fun onRunReport(report: RoutineRunReportPayload, applied: RoutineRunReportApplied) {
        try {
            for (run in applied.stored) run.runId?.let { runWaiters[it]?.complete(run) }
            if (report.live) {
                for (run in applied.stored.filter { it.outcome == RoutineRunOutcomes.RUNNING }) {
                    val routine = run.routineId?.let { store.routine(it) } ?: continue
                    val step = run.steps.orEmpty().firstOrNull { it.status == RoutineStepStatuses.RUNNING }?.index
                    observer.onProgress(run, routine, step)
                }
            }
            for (run in applied.finished) observer.onFinished(run, run.routineId?.let { store.routine(it) })
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Handling a routine_run_report failed.", e)
        }
    }

    /**
     * In-app Run and Test of a PC-run routine (§7.3.8): the PC runs its OWN stored copy (T24), never
     * a definition sent now. Only the selected PC is ever asked (D8).
     */
    suspend fun runOnPc(routineId: String, testRun: Boolean): RoutinePcRunStart {
        val routine = store.routine(routineId) ?: return RoutinePcRunStart.NotFound
        if (!RoutineTriggerTypes.isHostRun(routine.trigger?.type)) return RoutinePcRunStart.NotFound
        val host = routine.hostIdentity ?: return RoutinePcRunStart.Failed(RoutineReasonCodes.PC_NOT_PAIRED)
        if (!link.isPaired(host)) return RoutinePcRunStart.Failed(RoutineReasonCodes.PC_NOT_PAIRED)
        if (link.selectedHostIdentity() != host) return RoutinePcRunStart.NotSelected(host)
        if (link.authenticatedHostIdentity() != host) {
            link.startOneShotConnect()
            withTimeoutOrNull(CONNECT_WAIT_MS) { link.authenticated.first { it } }
            if (link.authenticatedHostIdentity() != host) return RoutinePcRunStart.Failed(RoutineReasonCodes.PC_UNREACHABLE)
        }
        if (!link.supportsRoutines(host)) return RoutinePcRunStart.Failed(RoutineReasonCodes.PC_TOO_OLD)
        awaitSynced(host)

        val runId = newId()
        val request = RoutineOutbound.runRequest(RoutineRunRequestPayload(runId = runId, routineId = routineId, testRun = testRun, source = RoutineRunSources.MANUAL_APP))
        var attempt = 0
        while (true) {
            val answer = CompletableDeferred<RoutineRun>()
            runWaiters[runId] = answer
            try {
                if (!sendIo(request)) return RoutinePcRunStart.Failed(RoutineReasonCodes.TRANSPORT_LOST)
                RoutineLog.i("routine_run_request ${RoutineLog.id(runId)} sent${if (testRun) " (test)" else ""}.")
                val first = withTimeoutOrNull(RUN_ANSWER_WAIT_MS) { answer.await() } ?: return RoutinePcRunStart.Failed(RoutineReasonCodes.PC_UNREACHABLE)
                if (first.outcome != RoutineRunOutcomes.SKIPPED) return RoutinePcRunStart.Started(runId)
                val code = first.reasonCode ?: RoutineReasonCodes.INTERNAL_ERROR
                // The PC's "not ready yet" answer while it starts: the same run id again after a
                // back-off (the PC deduplicates it), a bounded number of times.
                if (code != RoutineReasonCodes.RATE_LIMITED || attempt >= MAX_RUN_REQUEST_RETRIES) return RoutinePcRunStart.Skipped(code, runId)
            } finally {
                runWaiters.remove(runId)
            }
            delay(RoutineSyncBackoff.delayMs(attempt))
            attempt++
        }
    }

    /** Gives an unsent edit a moment to reach the PC first, so a Run right after Save runs the new copy. */
    private suspend fun awaitSynced(host: String) {
        val entry = store.hostSync.value[host] ?: return
        if (entry.ackedRevision >= entry.localRevision) return
        session?.takeIf { it.hostIdentity == host }?.kick()
        withTimeoutOrNull(SYNC_BEFORE_RUN_WAIT_MS) {
            store.hostSync.first { (it[host]?.ackedRevision ?: 0) >= (it[host]?.localRevision ?: 0) }
        }
    }

    /**
     * "Switch and run" (D8): remembers the run for the NEXT authenticated connection only. If that
     * connection is to [hostIdentity] within [SWITCH_RUN_WINDOW_MS], the run starts; a connection to
     * any other PC drops it, as do [cancelPendingSwitchRun] (leaving Connection) and the window
     * running out. Nothing switches here.
     */
    fun runAfterSwitch(routineId: String, hostIdentity: String, testRun: Boolean) {
        pendingSwitchRun = PendingPcRun(routineId, hostIdentity, testRun, clock.elapsedRealtimeMs())
    }

    /** Drops a pending "Switch and run": the person left the Connection flow without switching. */
    fun cancelPendingSwitchRun() {
        if (pendingSwitchRun != null) RoutineLog.i("A pending Switch and run was dropped.")
        pendingSwitchRun = null
    }

    /** Takes the pending switch-run, whatever PC it is for; null when there is none or it expired. */
    private fun consumePendingSwitchRun(): PendingPcRun? {
        val pending = pendingSwitchRun ?: return null
        pendingSwitchRun = null
        return pending.takeIf { clock.elapsedRealtimeMs() - it.atElapsedMs <= SWITCH_RUN_WINDOW_MS }
    }

    /**
     * A switched run that did not start (skipped by the PC, or failed), for the Routines screen's
     * message path. Latest wins; the screen clears it with [consumeSwitchRunOutcome] once shown. A
     * StateFlow, because the screen is usually not open when the switch completes.
     */
    private val _switchRunOutcomes = MutableStateFlow<RoutineSwitchRunOutcome?>(null)
    val switchRunOutcomes: StateFlow<RoutineSwitchRunOutcome?> = _switchRunOutcomes.asStateFlow()

    fun consumeSwitchRunOutcome(outcome: RoutineSwitchRunOutcome) {
        _switchRunOutcomes.compareAndSet(outcome, null)
    }

    /** Stop for a PC run in progress (§7.3.7): only the sender's own runs are cancellable on the PC. */
    suspend fun cancelPcRun(runId: String): Boolean {
        val run = store.findRun(runId) ?: return false
        if (run.origin != RoutineRunOrigins.PC || run.outcome != RoutineRunOutcomes.RUNNING) return false
        if (link.authenticatedHostIdentity() != run.hostIdentity) return false
        return sendIo(RoutineOutbound.cancel(RoutineCancelPayload(runId = runId, reason = RoutineCancelReasons.USER)))
    }

    /** `RemexCoreClient.SendMessage` is a blocking JNI call: never on the caller's (often Main) thread. */
    private suspend fun sendIo(json: String): Boolean = withContext(ioDispatcher) { link.send(json) }

    /**
     * The forget-PC flush (§7.4.5, T8), called BEFORE the pins are cleared. When the phone is
     * authenticated to [hostIdentity], sends `routines_sync{forget: true}` and waits up to 3 s for
     * the PC's answer; then deletes that PC's routines, history and sync entry here whatever the PC
     * said. Never throws.
     */
    suspend fun forgetPc(hostIdentity: String): RoutinePcForget {
        forgetting += hostIdentity
        session?.takeIf { it.hostIdentity == hostIdentity }?.stopped = true
        try {
            val hadRoutines = store.hasRoutinesFor(hostIdentity)
            var flushed = false
            try {
                flushed = withTimeoutOrNull(FORGET_TOTAL_WAIT_MS) { flush(hostIdentity) } == true
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                RoutineLog.e("The forget flush to the PC failed.", e)
            }
            if (!store.forgetHost(hostIdentity)) RoutineLog.e("Removing a forgotten PC's routines failed.")
            return when {
                !hadRoutines -> RoutinePcForget.NOTHING
                flushed -> RoutinePcForget.FLUSHED
                else -> RoutinePcForget.PHONE_ONLY
            }
        } finally {
            forgetting -= hostIdentity
            forgetWaiter = null
        }
    }

    private suspend fun flush(host: String): Boolean {
        if (link.authenticatedHostIdentity() != host || !link.supportsRoutines(host)) return false
        val revision = (store.syncSnapshot(host)?.revision ?: 0L) + 1
        val answer = CompletableDeferred<RoutineSyncResultPayload>()
        forgetWaiter = ForgetWaiter(host, revision, answer)
        val sent =
            sendIo(
                RoutineOutbound.routinesSync(
                    RoutinesSyncPayload(
                        schemaVersion = RoutineSchema.CURRENT_VERSION,
                        revision = revision,
                        paused = false,
                        routines = emptyList(),
                        runCursor = 0,
                        forget = true,
                        sentAtUnixMs = clock.nowUnixMs(),
                    ),
                ),
            )
        if (!sent) return false
        // Bounded by the caller's FORGET_TOTAL_WAIT_MS, which also covers the capability wait above.
        val result = withTimeoutOrNull(FORGET_TOTAL_WAIT_MS) { answer.await() }
        RoutineLog.i("Forget flush ${if (result == null) "got no answer" else "answered ${result.status}"}.")
        return result?.status == RoutineSyncStatuses.OK
    }

    internal companion object {
        const val MIN_SEND_INTERVAL_MS = 2_000L
        const val MAX_RETRIES = 8
        const val MAX_RUN_REQUEST_RETRIES = 2
        const val CONNECT_WAIT_MS = 10_000L
        const val RUN_ANSWER_WAIT_MS = 10_000L
        const val SYNC_BEFORE_RUN_WAIT_MS = 3_000L
        /** The whole flush, capability wait included, is bounded by 3 s (§7.4.5 "wait ≤ 3 s"). */
        const val FORGET_TOTAL_WAIT_MS = 3_000L
        const val SWITCH_RUN_WINDOW_MS = 2L * 60L * 1000L
    }
}

/** Retry spacing after the PC's not-ready answer: 2 s doubling to a 60 s ceiling. Pure. */
internal object RoutineSyncBackoff {
    private const val BASE_MS = 2_000L
    private const val MAX_MS = 60_000L

    fun delayMs(attempt: Int): Long = (BASE_MS shl attempt.coerceIn(0, 5)).coerceAtMost(MAX_MS)
}
