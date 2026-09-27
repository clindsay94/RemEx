package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.Home
import com.clindsay94.remex.routines.home.HomeCodec
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineMigration
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunReportPayload
import com.clindsay94.remex.routines.model.RoutineRunSourceDetail
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineSchema
import com.clindsay94.remex.routines.model.RoutineSet
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineSyncResultPayload
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.routines.model.RoutineValidationContext
import com.clindsay94.remex.routines.model.RoutineValidator
import com.clindsay94.remex.routines.model.RoutineVerdict
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject

/** Whether the phone routine store could be read (routines spec §6.7, §6.8). */
enum class RoutineStoreHealth {
    /** Not read yet. */
    LOADING,

    OK,

    /**
     * Something is stored and it cannot be read. The routines screen shows "Routines could not be
     * read" with Export raw ([RoutineRepository.unreadableDocumentText]) and Reset
     * ([RoutineRepository.resetUnreadableStore]); nothing is ever written over it until then.
     */
    UNREADABLE,
}

/**
 * @property readOnly the stored document is from a newer RemEx: shown, never edited (§6.7).
 * @property resetAtUnixMs a keystore loss forced a reset (`store_reset`); show the banner until
 *   [RoutineRepository.dismissStoreReset].
 */
data class RoutineStoreStatus(
    val health: RoutineStoreHealth = RoutineStoreHealth.LOADING,
    val readOnly: Boolean = false,
    val resetAtUnixMs: Long? = null,
)

/** One routine as the list shows it, with its current validation verdict (§1.8). */
data class RoutineItem(val routine: Routine, val verdict: RoutineVerdict)

sealed interface RoutineSaveResult {
    /** Stored; [routine] carries the id, revision and timestamps the store assigned. */
    data class Saved(val routine: Routine) : RoutineSaveResult

    /** Refused by the validator: the first failing rule, with its field path in `detail`. */
    data class Invalid(val verdict: RoutineVerdict) : RoutineSaveResult

    data object NotFound : RoutineSaveResult

    /** The store holds a document from a newer RemEx. */
    data object ReadOnly : RoutineSaveResult

    /** The store is unreadable, or the write failed; nothing changed. */
    data object Unavailable : RoutineSaveResult
}

sealed interface RoutineRunStart {
    /** Enqueued; progress arrives on [RoutineRepository.activeRuns] and [RoutineRepository.history]. */
    data class Started(val runId: String) : RoutineRunStart

    /**
     * Not started, for [reasonCode] (`skipped_disabled`, `paused_on_phone`, `already_running`,
     * `cooldown`, `rate_limited`). [runId] names the history record, or is null when the skip was
     * folded into an identical recent one (§8.1 coalescing).
     */
    data class Skipped(val reasonCode: String, val runId: String?) : RoutineRunStart

    /** The routine is invalid and cannot run until it is fixed. */
    data class Invalid(val verdict: RoutineVerdict) : RoutineRunStart

    /** Android refused the work; recorded as a failed run with `internal_error`. */
    data class Failed(val reasonCode: String, val runId: String) : RoutineRunStart

    data object NotFound : RoutineRunStart

    /**
     * A `pc.*` routine: the PC runs its own stored copy, started from the phone with
     * `routine_run_request` (§7.3.8, the S4 sync client). Never run by the phone runner.
     */
    data object RunsOnPc : RoutineRunStart

    data object Unavailable : RoutineRunStart
}

/**
 * What [RoutineRepository.applyRunReport] stored: [stored] every record it took, and [finished] the
 * PC runs that went from running to a final outcome on this page (the phone's progress notification
 * for them ends).
 */
data class RoutineRunReportApplied(val stored: List<RoutineRun>, val finished: List<RoutineRun>)

/** One `routines_sync` worth of state for one PC (§7.3.1). */
data class RoutineSyncSnapshot(
    val hostIdentity: String,
    val revision: Long,
    val paused: Boolean,
    val routines: List<Routine>,
    val runCursor: Long,
)

/** WorkManager, behind a seam. */
interface RoutineRunScheduler {
    /** Enqueues the unique work for [ticket]'s routine; false when Android refused it. */
    fun enqueue(ticket: RoutineRunTicket): Boolean

    fun cancel(routineId: String)

    /** Whether the routine's unique work is queued or running. */
    suspend fun isActive(routineId: String): Boolean
}

/**
 * The phone's routines: the one place the S1d UI reads and edits them (routines spec §6.8, §8.2,
 * §8.7, §8.8; RemEx-pp0rt.5). Obtain it with `Routines.repository(context)`.
 *
 * **Reading.** Five StateFlows, all updated together under one lock:
 * [routines] (the user's order, each with its validation verdict), [pausedAll], [status],
 * [activeRuns] (routine id -> the phone run in progress) and [history] (every stored run, newest
 * first, phone and PC runs merged; [historyFor] narrows it to one routine). Call [load] once from
 * the screen; every other call loads on demand.
 *
 * **Editing.** [save] creates or updates (validated with the full validator; an invalid routine is
 * refused, never stored), [delete], [reorder], [setEnabled], [setPausedAll]. Every edit that touches
 * a PC-run routine or the pause flag bumps that PC's sync revision in the same write (§7.4.1).
 *
 * **Running.** [run] with a source (`manual.app` for in-app Run and Test) and `testRun`; [cancel] /
 * [cancelRun] stop a phone run (§8.2 Cancellation).
 */
class RoutineRepository(
    private val documents: RoutineDocumentStore,
    private val historyStore: RoutineHistoryStore,
    private val cipherSource: RoutineCipherSource,
    private val scheduler: RoutineRunScheduler,
    private val observer: RoutineRunObserver,
    private val clock: RoutineClock,
    internal val controls: RoutineRunControls,
    private val newId: () -> String = { UUID.randomUUID().toString() },
) : RoutineRunStore {

    private val mutex = Mutex()
    private var loaded = false

    /** Null while unreadable (or not loaded). */
    private var document: RoutineStoreDocument? = null
    private var unreadableText: String? = null
    private val runs = LinkedHashMap<String, List<RoutineRun>>()

    private val _routines = MutableStateFlow<List<RoutineItem>>(emptyList())
    val routines: StateFlow<List<RoutineItem>> = _routines.asStateFlow()

    private val _pausedAll = MutableStateFlow(false)
    val pausedAll: StateFlow<Boolean> = _pausedAll.asStateFlow()

    private val _status = MutableStateFlow(RoutineStoreStatus())
    val status: StateFlow<RoutineStoreStatus> = _status.asStateFlow()

    private val _activeRuns = MutableStateFlow<Map<String, RoutineRun>>(emptyMap())
    val activeRuns: StateFlow<Map<String, RoutineRun>> = _activeRuns.asStateFlow()

    private val _history = MutableStateFlow<List<RoutineRun>>(emptyList())
    val history: StateFlow<List<RoutineRun>> = _history.asStateFlow()

    /**
     * Routine id -> the PC run of it in progress, from live `routine_run_report` updates (§7.3.6).
     * Separate from [activeRuns], which holds only runs THIS phone executes: a PC run is stopped
     * with `routine_cancel`, never by the phone runner. A record whose final report never arrived
     * (the connection dropped) stops counting once the PC's run budget has certainly passed.
     */
    private val _pcActiveRuns = MutableStateFlow<Map<String, RoutineRun>>(emptyMap())
    val pcActiveRuns: StateFlow<Map<String, RoutineRun>> = _pcActiveRuns.asStateFlow()

    /** Per-PC sync bookkeeping (§7.4.1), for the sync states the screens show ([RoutineSyncStates]). */
    private val _hostSync = MutableStateFlow<Map<String, RoutineHostSync>>(emptyMap())
    val hostSync: StateFlow<Map<String, RoutineHostSync>> = _hostSync.asStateFlow()

    /** The phone's one home (§6.6, S3), or null when none is set or it cannot be read. */
    private val _home = MutableStateFlow<Home?>(null)
    val home: StateFlow<Home?> = _home.asStateFlow()

    fun historyFor(routineId: String): Flow<List<RoutineRun>> = history.map { all -> all.filter { it.routineId == routineId } }

    // ── Home (S3, §8.3.1) ──

    /**
     * Replaces the home with [transform]'s answer (null forgets it) in one write. Forgetting home
     * leaves every `home.*` routine in place, invalid with `home_not_set` until a home is set again
     * (spec 1.4: "needs attention", never deleted). The home never reaches a PC, so no sync revision
     * moves.
     */
    suspend fun updateHome(transform: (Home?) -> Home?): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            if (doc.isNewerThanReader) return false
            val next = transform(HomeCodec.decode(doc.homeJson))
            val json = next?.let(HomeCodec::encode)
            if (json == doc.homeJson) return true
            write(doc.copy(homeJson = json))
        }

    /**
     * The phone just authenticated to [hostIdentity] from a network that is not home (§8.3.1
     * "Reachable away"): it silences the leave-home authoring warning for that PC and makes an
     * unreachable PC fail `pc_unreachable` rather than `pc_unreachable_away`.
     */
    suspend fun markReachableAway(hostIdentity: String): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            if (doc.isNewerThanReader) return false
            val entry = doc.hostSync[hostIdentity] ?: RoutineHostSync()
            if (entry.reachableAwayAtUnixMs != null) return true
            write(doc.copy(hostSync = doc.hostSync + (hostIdentity to entry.copy(reachableAwayAtUnixMs = clock.nowUnixMs()))))
        }

    // ── Loading ──

    suspend fun load() {
        mutex.withLock { ensureLoadedLocked() }
    }

    private suspend fun ensureLoadedLocked() {
        if (loaded) return
        when (val result = readDocument()) {
            RoutineDocumentLoad.Empty -> document = RoutineStoreDocument()
            is RoutineDocumentLoad.Loaded -> document = migrate(result.document)
            is RoutineDocumentLoad.Unreadable -> {
                document = null
                unreadableText = result.decryptedText
                RoutineLog.e("The routine store could not be read; it is left untouched until the user resets it.")
            }
        }
        runs.clear()
        runs.putAll(readHistory())
        reportKeyLoss()
        sweepInterrupted()
        loaded = true
        publish()
    }

    private suspend fun readDocument(): RoutineDocumentLoad =
        try {
            documents.load()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Reading the routine store failed.", e)
            RoutineDocumentLoad.Unreadable(null)
        }

    private suspend fun readHistory(): Map<String, List<RoutineRun>> =
        try {
            historyStore.loadAll()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Reading the routine history failed.", e)
            emptyMap()
        }

    /** Stamps an older document up to the current schema in memory; it is written on the next edit. */
    private fun migrate(doc: RoutineStoreDocument): RoutineStoreDocument {
        val result = RoutineMigration.migrate(RoutineSet(schemaVersion = doc.schemaVersion, routines = doc.routines))
        return doc.copy(schemaVersion = result.set.schemaVersion, routines = result.set.routines.orEmpty())
    }

    /** Turns a pending keyset-loss marker into the banner and a history record (§6.8, never silent). */
    private suspend fun reportKeyLoss() {
        val lostAt = cipherSource.pendingKeyLossAtUnixMs() ?: return
        val doc = document ?: return
        val noticed = doc.copy(storeResetAtUnixMs = lostAt)
        if (!write(noticed)) return
        val record =
            RoutineRun(
                runId = newId(),
                origin = RoutineRunOrigins.PHONE,
                triggeredAtUnixMs = lostAt,
                startedAtUnixMs = lostAt,
                endedAtUnixMs = lostAt,
                outcome = RoutineRunOutcomes.FAILED,
                reasonCode = RoutineReasonCodes.STORE_RESET,
                steps = emptyList(),
                attributes = emptyList(),
            )
        putRunsLocked(RoutineHistoryStore.STORE_EVENTS_ID, runs[RoutineHistoryStore.STORE_EVENTS_ID].orEmpty() + record)
        cipherSource.clearKeyLossMarker()
    }

    /** Phone runs left `running` by a process that died, with no work left to finish them (§8.1). */
    private suspend fun sweepInterrupted() {
        for ((routineId, list) in runs.toMap()) {
            var changed = false
            val swept =
                list.map { run ->
                    val orphaned =
                        run.outcome == RoutineRunOutcomes.RUNNING &&
                            run.origin == RoutineRunOrigins.PHONE &&
                            controls.find(run.runId.orEmpty())?.attached != true &&
                            !scheduler.isActive(routineId)
                    if (!orphaned) {
                        run
                    } else {
                        changed = true
                        interrupted(run)
                    }
                }
            if (changed) putRunsLocked(routineId, swept)
        }
    }

    private fun interrupted(run: RoutineRun): RoutineRun {
        val now = clock.nowUnixMs()
        return run.copy(
            outcome = RoutineRunOutcomes.INTERRUPTED,
            reasonCode = RoutineReasonCodes.INTERRUPTED_PHONE,
            endedAtUnixMs = now,
            steps =
                run.steps.orEmpty().map {
                    when (it.status) {
                        RoutineStepStatuses.PENDING -> it.copy(status = RoutineStepStatuses.SKIPPED)
                        RoutineStepStatuses.RUNNING ->
                            it.copy(status = RoutineStepStatuses.FAILED, reasonCode = RoutineReasonCodes.INTERRUPTED_PHONE, endedAtUnixMs = now)
                        else -> it
                    }
                },
        )
    }

    // ── Editing ──

    /**
     * Creates [routine] (no id, or an id this store does not hold) or updates the stored routine
     * with its id. The store assigns `revision`, `createdAtUnixMs` and `updatedAtUnixMs`.
     */
    suspend fun save(routine: Routine): RoutineSaveResult =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return RoutineSaveResult.Unavailable
            if (doc.isNewerThanReader) return RoutineSaveResult.ReadOnly
            val now = clock.nowUnixMs()
            val index = routine.id?.let { id -> doc.routines.indexOfFirst { it.id == id } } ?: -1
            val existing = doc.routines.getOrNull(index)
            val prepared =
                if (existing == null) {
                    routine.copy(
                        id = routine.id ?: newId(),
                        revision = 1,
                        createdAtUnixMs = now,
                        updatedAtUnixMs = now,
                        isMalformed = false,
                        rawJson = null,
                    )
                } else {
                    routine.copy(
                        revision = existing.revision + 1,
                        createdAtUnixMs = existing.createdAtUnixMs,
                        updatedAtUnixMs = maxOf(now, existing.createdAtUnixMs),
                        isMalformed = false,
                        rawJson = null,
                    )
                }
            val list = doc.routines.toMutableList()
            val position = if (existing == null) list.size.also { list.add(prepared) } else index.also { list[it] = prepared }
            val verdict = RoutineValidator.validateRoutines(list, validationContext(doc))
            val own = verdict.routines[position]
            if (!own.isValid) return RoutineSaveResult.Invalid(own)
            if (verdict.reasonCode != RoutineReasonCodes.OK) return RoutineSaveResult.Invalid(RoutineVerdict(verdict.reasonCode, verdict.detail))
            val next =
                doc.copy(
                    schemaVersion = RoutineSchema.CURRENT_VERSION,
                    routines = list,
                    hostSync = bumpRevisions(doc.hostSync, pcHosts(existing, prepared)),
                )
            if (write(next)) RoutineSaveResult.Saved(prepared) else RoutineSaveResult.Unavailable
        }

    /** Deletes a routine and its history; a run in progress is cancelled first. */
    suspend fun delete(routineId: String): Boolean {
        activeRunFor(routineId)?.runId?.let { cancelRun(it, RoutineCancelKind.DELETED) }
        return mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            if (doc.isNewerThanReader) return false
            val existing = doc.routines.firstOrNull { it.id == routineId } ?: return false
            val next =
                doc.copy(
                    routines = doc.routines.filterNot { it.id == routineId },
                    hostSync = bumpRevisions(doc.hostSync, pcHosts(existing, null)),
                )
            if (!write(next)) return false
            runs.remove(routineId)
            runCatchingIo("Removing a deleted routine's history") { historyStore.remove(routineId) }
            publish()
            true
        }
    }

    /**
     * Puts the routines in [orderedIds] order. Ids it does not name keep their relative order after
     * the named ones, so a stale list from the UI can never drop a routine.
     */
    suspend fun reorder(orderedIds: List<String>): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            if (doc.isNewerThanReader) return false
            val byId = doc.routines.filter { it.id != null }.associateBy { it.id }
            val named = orderedIds.distinct().mapNotNull { byId[it] }
            val rest = doc.routines.filterNot { it in named }
            val next = named + rest
            // The PC shows a phone's routines in the phone's order (spec 1.6), so a reorder that
            // changes a PC's subset order is an edit of that PC's set.
            val moved = (doc.routines.mapNotNull { it.hostIdentity } + next.mapNotNull { it.hostIdentity }).toSet()
                .filter { host -> pcOrder(doc.routines, host) != pcOrder(next, host) }
            write(doc.copy(routines = next, hostSync = bumpRevisions(doc.hostSync, moved)))
        }

    /** The phone-side enable toggle (§6.2): a save, so it bumps the routine's revision. */
    suspend fun setEnabled(routineId: String, enabled: Boolean): RoutineSaveResult {
        val current = routine(routineId) ?: return RoutineSaveResult.NotFound
        if (current.enabled == enabled) return RoutineSaveResult.Saved(current)
        val result = save(current.copy(enabled = enabled))
        if (!enabled && result is RoutineSaveResult.Saved) {
            // An automatic run in progress stops now rather than at its next step.
            activeRunFor(routineId)
                ?.takeIf { RoutineTriggerTypes.isAutomatic(it.source) }
                ?.runId
                ?.let { cancelRun(it, RoutineCancelKind.DISABLED) }
        }
        return result
    }

    /**
     * Pause all on the phone (§8.7): automatic sources stop (`paused_on_phone`), and automatic runs
     * in progress are cancelled. Person-initiated runs are untouched. The flag reaches each PC with
     * the next `routines_sync` (S4); the revision bump here is what makes that sync happen.
     */
    suspend fun setPausedAll(paused: Boolean): Boolean {
        val written =
            mutex.withLock {
                ensureLoadedLocked()
                val doc = document ?: return false
                if (doc.isNewerThanReader) return false
                if (doc.pausedAll == paused) return true
                val hosts = doc.hostSync.keys + doc.routines.filter { RoutineTriggerTypes.isHostRun(it.trigger?.type) }.mapNotNull { it.hostIdentity }
                write(doc.copy(pausedAll = paused, hostSync = bumpRevisions(doc.hostSync, hosts)))
            }
        if (written && paused) {
            _activeRuns.value.values
                .filter { RoutineTriggerTypes.isAutomatic(it.source) }
                .mapNotNull { it.runId }
                .forEach { cancelRun(it, RoutineCancelKind.PAUSE) }
        }
        return written
    }

    // ── Running ──

    /**
     * Starts a phone run of [routineId] from [source] (a `RoutineRunSources` value). The record is
     * stored `running` BEFORE the work is enqueued (§8.1). Skips are recorded and coalesced.
     */
    suspend fun run(
        routineId: String,
        source: String,
        testRun: Boolean = false,
        sourceDetail: RoutineRunSourceDetail? = null,
    ): RoutineRunStart {
        var ticket: RoutineRunTicket? = null
        var skippedRecord: Pair<RoutineRun, Routine>? = null
        val start =
            mutex.withLock {
                ensureLoadedLocked()
                val doc = document ?: return RoutineRunStart.Unavailable
                val index = doc.routines.indexOfFirst { it.id == routineId }
                val routine = doc.routines.getOrNull(index) ?: return RoutineRunStart.NotFound
                if (RoutineTriggerTypes.isHostRun(routine.trigger?.type)) return RoutineRunStart.RunsOnPc
                val verdict = RoutineValidator.validateRoutines(doc.routines, validationContext(doc)).routines[index]
                if (!verdict.isValid) return RoutineRunStart.Invalid(verdict)

                val now = clock.nowUnixMs()
                val runId = newId()
                val queued = RoutineRunRecords.queued(runId, routine, source, testRun, now, sourceDetail)
                val past = runs[routineId].orEmpty()
                val skipCode =
                    when {
                        !routine.enabled && source != RoutineRunSources.MANUAL_APP -> RoutineReasonCodes.SKIPPED_DISABLED
                        RoutineTriggerTypes.isAutomatic(source) && doc.pausedAll -> RoutineReasonCodes.PAUSED_ON_PHONE
                        past.any { it.isActivePhoneRun() } -> RoutineReasonCodes.ALREADY_RUNNING
                        else -> RoutineStartLimits.check(past, runs.values.flatten(), source, now)
                    }
                if (skipCode != null) {
                    if (RoutineSkipCoalescing.isCoalesced(past, skipCode, now)) return RoutineRunStart.Skipped(skipCode, null)
                    val skipped = RoutineRunRecords.skipped(queued, skipCode, now)
                    putRunsLocked(routineId, past + skipped)
                    publish()
                    if (source != RoutineRunSources.MANUAL_APP) skippedRecord = skipped to routine
                    RoutineRunStart.Skipped(skipCode, runId)
                } else {
                    putRunsLocked(routineId, past + queued)
                    controls.obtain(runId, routineId)
                    publish()
                    ticket = RoutineRunTicket(runId, routineId, source, testRun, now, clock.elapsedRealtimeMs(), sourceDetail)
                    RoutineRunStart.Started(runId)
                }
            }
        skippedRecord?.let { (run, routine) -> observer.onFinished(run, routine) }
        val work = ticket ?: return start
        // No run of this routine is in progress (checked above), so any job WorkManager still holds
        // for it belongs to a run that already ended. ExistingWorkPolicy.KEEP would silently drop
        // this new request behind it and leave the new record `running` with nothing to run it.
        if (scheduler.isActive(routineId)) scheduler.cancel(routineId)
        if (scheduler.enqueue(work)) {
            RoutineLog.i("Run ${RoutineLog.id(work.runId)} queued from ${work.source}${if (work.testRun) " (test)" else ""}.")
            return start
        }
        RoutineLog.e("WorkManager refused run ${RoutineLog.id(work.runId)}.")
        controls.release(work.runId)
        val failed = findRun(work.runId)?.let { finalRecord(it, RoutineRunOutcomes.FAILED, RoutineReasonCodes.INTERNAL_ERROR, null) }
        if (failed != null) observer.onFinished(recordRun(failed), routine(routineId))
        return RoutineRunStart.Failed(RoutineReasonCodes.INTERNAL_ERROR, work.runId)
    }

    /** Cancels the phone run of [routineId] in progress, if any. */
    suspend fun cancel(routineId: String): Boolean = activeRunFor(routineId)?.runId?.let { cancelRun(it) } ?: false

    /**
     * Cancels one phone run. A run with its runner attached is ASKED to stop (so an in-flight host
     * step can send `routine_cancel` and record the PC's answer); a run still queued is cancelled at
     * the job level and recorded here.
     */
    suspend fun cancelRun(runId: String, kind: RoutineCancelKind = RoutineCancelKind.USER): Boolean {
        val run = findRun(runId)?.takeIf { it.isActivePhoneRun() } ?: return false
        val routineId = run.routineId ?: return false
        val control = controls.obtain(runId, routineId)
        control.requestCancel(kind)
        if (!control.attached) {
            scheduler.cancel(routineId)
            val stored = recordRun(finalRecord(run, RoutineRunOutcomes.CANCELLED, kind.reasonCode, kind.cancelledBy))
            controls.release(runId)
            observer.onFinished(stored, routine(routineId))
        }
        RoutineLog.i("Run ${RoutineLog.id(runId)} cancel requested (${kind.name}).")
        return true
    }

    private fun activeRunFor(routineId: String): RoutineRun? = _activeRuns.value[routineId]

    /**
     * Records a start that was refused before it could be a run: an NFC tap with a stale token
     * (`nfc_unknown_tag`) or on a locked phone (`nfc_device_locked`), spec §8.3.2. Stored as a
     * skipped run of [routineId] (coalesced like every skip) so History shows it; nothing is
     * enqueued and no notification is posted, because the tap already showed a toast.
     */
    suspend fun recordRefusal(routineId: String, source: String, reasonCode: String): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            val routine = doc.routines.firstOrNull { it.id == routineId } ?: return false
            val now = clock.nowUnixMs()
            val past = runs[routineId].orEmpty()
            if (RoutineSkipCoalescing.isCoalesced(past, reasonCode, now)) return true
            val queued = RoutineRunRecords.queued(newId(), routine, source, false, now, null)
            putRunsLocked(routineId, past + RoutineRunRecords.skipped(queued, reasonCode, now))
            publish()
            true
        }

    // ── PC run reports (§7.3.6) ──

    /**
     * Stores PC runs from a `routine_run_report` page, upserting by run id (a record that changed
     * after it ended is re-sent with a higher `seq`). For a non-live page the PC's run cursor
     * advances only AFTER the page is stored, so a crash in between re-fetches rather than skips.
     */
    suspend fun applyRunReport(report: RoutineRunReportPayload): RoutineRunReportApplied {
        mutex.withLock {
            ensureLoadedLocked()
            val incoming = report.runs.orEmpty().filter { it.runId != null && it.routineId != null }
            if (incoming.isEmpty()) return RoutineRunReportApplied(emptyList(), emptyList())
            val touched = LinkedHashMap<String, MutableList<RoutineRun>>()
            val stored = ArrayList<RoutineRun>()
            val finished = ArrayList<RoutineRun>()
            for (raw in incoming) {
                // A PC runs only PC-origin records; the phone never takes the PC's word for its own runs.
                val run = raw.copy(origin = RoutineRunOrigins.PC, ownerClientId = null)
                val routineId = checkNotNull(run.routineId)
                val list = touched.getOrPut(routineId) { runs[routineId].orEmpty().toMutableList() }
                val at = list.indexOfFirst { it.runId == run.runId }
                val previous = list.getOrNull(at)
                // Upsert by run id. A live update carries the whole current record and no newer seq,
                // so it replaces a record that is still running; a final record never goes back to running.
                val replace =
                    when {
                        previous == null -> true
                        previous.origin != RoutineRunOrigins.PC -> false
                        previous.outcome != RoutineRunOutcomes.RUNNING && run.outcome == RoutineRunOutcomes.RUNNING -> false
                        else -> (previous.seq ?: -1L) <= (run.seq ?: -1L) || previous.outcome == RoutineRunOutcomes.RUNNING
                    }
                if (!replace) continue
                if (at < 0) list += run else list[at] = run
                stored += run
                if (previous?.outcome == RoutineRunOutcomes.RUNNING && run.outcome != RoutineRunOutcomes.RUNNING) finished += run
            }
            for ((routineId, list) in touched) putRunsLocked(routineId, list)

            val doc = document
            if (!report.live && doc != null && !doc.isNewerThanReader) {
                val now = clock.nowUnixMs()
                val maxSeq = incoming.groupBy { it.hostIdentity }.mapValues { (_, runs) -> runs.maxOf { it.seq ?: 0L } }
                var sync = doc.hostSync
                for ((host, seq) in maxSeq) {
                    // Only a PC this phone keeps books for: a stray report must not resurrect a forgotten PC.
                    val entry = host?.let { sync[it] } ?: continue
                    sync = sync + (host to entry.copy(runCursor = maxOf(entry.runCursor, seq), historyAsOfUnixMs = now))
                }
                if (sync != doc.hostSync) write(doc.copy(hostSync = sync))
            }
            publish()
            return RoutineRunReportApplied(stored, finished)
        }
    }

    // ── Sync with each PC (§7.3.1, §7.4.1; RoutineSyncClient) ──

    /**
     * The `routines_sync` this phone owes [hostIdentity] right now: its PC-run routines in the user's
     * order, the revision, the pause flag and the run cursor. Null when the store cannot be read or
     * is from a newer RemEx (a set this build cannot read faithfully is never sent).
     */
    suspend fun syncSnapshot(hostIdentity: String): RoutineSyncSnapshot? =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document?.takeIf { !it.isNewerThanReader } ?: return null
            val entry = doc.hostSync[hostIdentity] ?: RoutineHostSync()
            RoutineSyncSnapshot(
                hostIdentity = hostIdentity,
                revision = entry.localRevision,
                paused = doc.pausedAll,
                routines = doc.routines.filter { it.hostIdentity == hostIdentity && RoutineTriggerTypes.isHostRun(it.trigger?.type) && !it.isMalformed },
                runCursor = entry.runCursor,
            )
        }

    /**
     * Stores [result] from [hostIdentity] and says what the sync client does next
     * ([RoutineSyncProtocol]). A revision change is written in the same write as the answer.
     */
    suspend fun applySyncResult(hostIdentity: String, result: RoutineSyncResultPayload): RoutineSyncFollowUp =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document?.takeIf { !it.isNewerThanReader } ?: return RoutineSyncFollowUp.IGNORED
            // A PC this phone has no books for (it was just forgotten) is not brought back by its answer.
            val entry = doc.hostSync[hostIdentity] ?: return RoutineSyncFollowUp.IGNORED
            val json = RoutineJson.write(result).toString()
            val (next, followUp) = RoutineSyncProtocol.apply(entry, result, json, clock.nowUnixMs())
            if (next != entry && !write(doc.copy(hostSync = doc.hostSync + (hostIdentity to next)))) return RoutineSyncFollowUp.RETRY_LATER
            followUp
        }

    /** Whether any stored routine is bound to [hostIdentity]. */
    suspend fun hasRoutinesFor(hostIdentity: String): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            document?.routines.orEmpty().any { it.hostIdentity == hostIdentity }
        }

    /**
     * The phone half of forgetting a PC (§7.4.5, T8): every routine bound to [hostIdentity], their
     * history, and the PC's sync entry. Runs in progress of those routines are cancelled first.
     */
    suspend fun forgetHost(hostIdentity: String): Boolean {
        val doomed = mutex.withLock {
            ensureLoadedLocked()
            document?.routines.orEmpty().filter { it.hostIdentity == hostIdentity }.mapNotNull { it.id }
        }
        doomed.forEach { id -> activeRunFor(id)?.runId?.let { cancelRun(it, RoutineCancelKind.DELETED) } }
        return mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            if (doc.isNewerThanReader) return false
            val next = doc.copy(routines = doc.routines.filterNot { it.hostIdentity == hostIdentity }, hostSync = doc.hostSync - hostIdentity)
            if (next != doc && !write(next)) return false
            // PC runs of routines the phone no longer has go too: they name a PC this phone forgot.
            val runIds = runs.keys.filter { id -> id in doomed || runs[id].orEmpty().all { it.hostIdentity == hostIdentity } }
            for (id in runIds) {
                runs.remove(id)
                runCatchingIo("Removing a forgotten PC's routine history") { historyStore.remove(id) }
            }
            publish()
            true
        }
    }

    // ── Store health ──

    suspend fun dismissStoreReset(): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            val doc = document ?: return false
            if (doc.storeResetAtUnixMs == null) return true
            write(doc.copy(storeResetAtUnixMs = null))
        }

    /** The unreadable document's text when it decrypted but did not parse, for "Export raw". */
    suspend fun unreadableDocumentText(): String? = mutex.withLock { unreadableText }

    /** The user's explicit Reset of an unreadable store: the only path that replaces it (§6.7). */
    suspend fun resetUnreadableStore(): Boolean =
        mutex.withLock {
            ensureLoadedLocked()
            if (document != null) return false
            val ok = runCatchingIo("Resetting the routine store") { documents.clear() }
            if (!ok) return false
            document = RoutineStoreDocument()
            unreadableText = null
            publish()
            true
        }

    // ── RoutineRunStore ──

    override suspend fun findRun(runId: String): RoutineRun? =
        mutex.withLock {
            ensureLoadedLocked()
            runs.values.firstNotNullOfOrNull { list -> list.firstOrNull { it.runId == runId } }
        }

    override suspend fun routine(routineId: String): Routine? =
        mutex.withLock {
            ensureLoadedLocked()
            document?.routines?.firstOrNull { it.id == routineId }
        }

    override suspend fun isPausedAll(): Boolean = mutex.withLock { ensureLoadedLocked(); document?.pausedAll ?: false }

    override suspend fun reachableAwayAtUnixMs(hostIdentity: String): Long? =
        mutex.withLock {
            ensureLoadedLocked()
            document?.hostSync?.get(hostIdentity)?.reachableAwayAtUnixMs
        }

    override suspend fun recordRun(run: RoutineRun): RoutineRun =
        mutex.withLock {
            ensureLoadedLocked()
            val routineId = run.routineId ?: return run
            val list = runs[routineId].orEmpty()
            val existing = list.firstOrNull { it.runId == run.runId }
            if (existing != null && existing.outcome != RoutineRunOutcomes.RUNNING) return existing
            // A routine deleted mid-run: its history went with it, and a late record must not bring it back.
            if (existing == null && document?.routines?.none { it.id == routineId } != false) return run
            putRunsLocked(routineId, if (existing == null) list + run else list.map { if (it.runId == run.runId) run else it })
            publish()
            run
        }

    // ── Internals ──

    /**
     * Replaces one routine's runs, applies retention to the whole history, and writes every routine
     * whose runs changed. A failed write is logged and the in-memory history kept: losing a history
     * row is recoverable, crashing the runner over it is not.
     */
    private suspend fun putRunsLocked(routineId: String, list: List<RoutineRun>) {
        val candidate = LinkedHashMap(runs).apply { put(routineId, list) }
        val retained = RoutineRetention.apply(candidate, clock.nowUnixMs())
        for ((id, kept) in retained) {
            if (id == routineId || kept.size != runs[id]?.size) {
                runCatchingIo("Writing routine history") { historyStore.write(id, kept) }
            }
        }
        runs.clear()
        retained.forEach { (id, kept) -> if (kept.isNotEmpty()) runs[id] = kept }
    }

    /** Persists [next] and makes it current; false (and nothing changed) when the write failed. */
    private suspend fun write(next: RoutineStoreDocument): Boolean {
        val ok = runCatchingIo("Writing the routine store") { documents.save(next) }
        if (ok) {
            document = next
            publish()
        }
        return ok
    }

    private suspend fun runCatchingIo(what: String, block: suspend () -> Unit): Boolean =
        try {
            block()
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("$what failed.", e)
            false
        }

    private fun publish() {
        val doc = document
        _status.value =
            RoutineStoreStatus(
                health = if (!loaded) RoutineStoreHealth.LOADING else if (doc == null) RoutineStoreHealth.UNREADABLE else RoutineStoreHealth.OK,
                readOnly = doc?.isNewerThanReader == true,
                resetAtUnixMs = doc?.storeResetAtUnixMs,
            )
        _pausedAll.value = doc?.pausedAll ?: false
        _routines.value =
            if (doc == null) {
                emptyList()
            } else {
                val verdicts = RoutineValidator.validateRoutines(doc.routines, validationContext(doc)).routines
                doc.routines.mapIndexed { i, routine -> RoutineItem(routine, verdicts[i]) }
            }
        val all = RoutineRetention.newestFirst(runs.values.flatten())
        _history.value = all
        _activeRuns.value =
            all.filter { it.isActivePhoneRun() }
                .groupBy { it.routineId.orEmpty() }
                .mapValues { (_, list) -> list.first() }
        val now = clock.nowUnixMs()
        _pcActiveRuns.value =
            all.filter { it.outcome == RoutineRunOutcomes.RUNNING && it.origin == RoutineRunOrigins.PC && now - it.startedAtUnixMs.coerceAtLeast(it.triggeredAtUnixMs) < PC_RUN_STALE_MS }
                .groupBy { it.routineId.orEmpty() }
                .mapValues { (_, list) -> list.first() }
        _hostSync.value = doc?.hostSync.orEmpty()
        _home.value = HomeCodec.decode(doc?.homeJson)
    }

    private fun validationContext(doc: RoutineStoreDocument): RoutineValidationContext =
        RoutineValidationContext(knownHomeIds = listOfNotNull(homeId(doc)))

    private fun homeId(doc: RoutineStoreDocument): String? =
        doc.homeJson?.let { runCatching { JSONObject(it).optString("id") }.getOrNull() }?.takeIf { it.isNotBlank() }

    private fun finalRecord(run: RoutineRun, outcome: String, code: String, cancelledBy: String?): RoutineRun {
        val now = clock.nowUnixMs()
        val cancelled = outcome == RoutineRunOutcomes.CANCELLED
        val pendingStatus = if (cancelled) RoutineStepStatuses.CANCELLED else RoutineStepStatuses.SKIPPED
        val runningStatus = if (cancelled) RoutineStepStatuses.CANCELLED else RoutineStepStatuses.FAILED
        return run.copy(
            outcome = outcome,
            reasonCode = code,
            cancelledBy = cancelledBy,
            endedAtUnixMs = now,
            steps =
                run.steps.orEmpty().map {
                    when (it.status) {
                        RoutineStepStatuses.PENDING -> it.copy(status = pendingStatus)
                        RoutineStepStatuses.RUNNING -> it.copy(status = runningStatus, reasonCode = code, endedAtUnixMs = now)
                        else -> it
                    }
                },
        )
    }

    /**
     * The worker's backstop: ends a phone run its runner could not finish (the runner's own finish
     * path failed) as `failed` with [reasonCode], so it leaves [activeRuns] and cannot block every
     * later start as `already_running`. A no-op for a run that is already final.
     */
    internal suspend fun abandonRun(runId: String, reasonCode: String): Boolean {
        val run = findRun(runId)?.takeIf { it.isActivePhoneRun() } ?: return false
        controls.release(runId)
        val stored = recordRun(finalRecord(run, RoutineRunOutcomes.FAILED, reasonCode, null))
        try {
            observer.onFinished(stored, run.routineId?.let { routine(it) })
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.w("The result notification for an abandoned run failed.", e)
        }
        return true
    }

    private fun RoutineRun.isActivePhoneRun(): Boolean = outcome == RoutineRunOutcomes.RUNNING && origin == RoutineRunOrigins.PHONE

    private companion object {
        /**
         * A PC run still `running` this long after it started lost its final report (the connection
         * dropped): the PC's hard budget (§6.5) plus the countdown and a margin.
         */
        const val PC_RUN_STALE_MS = (RoutineLimits.MAX_HOST_RUN_BUDGET_SECONDS + 5L * 60L) * 1000L

        /** The ids of [host]'s PC-run routines in list order. */
        fun pcOrder(routines: List<Routine>, host: String): List<String?> =
            routines.filter { it.hostIdentity == host && RoutineTriggerTypes.isHostRun(it.trigger?.type) }.map { it.id }

        /** The PCs whose PC-run subset an edit from [before] to [after] changes. */
        fun pcHosts(before: Routine?, after: Routine?): Set<String> =
            listOfNotNull(before, after)
                .filter { RoutineTriggerTypes.isHostRun(it.trigger?.type) }
                .mapNotNull { it.hostIdentity }
                .toSet()

        fun bumpRevisions(sync: Map<String, RoutineHostSync>, hosts: Collection<String>): Map<String, RoutineHostSync> {
            if (hosts.isEmpty()) return sync
            val next = LinkedHashMap(sync)
            for (host in hosts.toSet()) {
                val entry = next[host] ?: RoutineHostSync()
                next[host] = entry.copy(localRevision = entry.localRevision + 1)
            }
            return next
        }
    }
}
