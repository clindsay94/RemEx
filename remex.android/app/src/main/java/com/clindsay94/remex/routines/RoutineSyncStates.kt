package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineSyncResultPayload
import com.clindsay94.remex.routines.model.RoutineSyncStatuses
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import org.json.JSONTokener

// What the phone makes of `routine_sync_result` (routines spec §7.4.1, UX need 5; RemEx-pp0rt.12).
// Pure JVM: the repository applies [RoutineSyncProtocol] under its lock, and the screens read
// [RoutineSyncStates] from the stored host sync entries.

/** What the sync client does after one `routine_sync_result` (§7.4.1 step 3). */
enum class RoutineSyncFollowUp {
    /** Stored; nothing else to do. */
    DONE,

    /** The revision moved (stale_revision, revision_conflict): send the full set once more. */
    RESEND,

    /** The PC is not ready yet (rate_limited while it starts) or failed: try again after a back-off. */
    RETRY_LATER,

    /** blocked_by_pc, schema_too_new, payload_too_large: stop and show it. */
    STOP,

    /** A status this build does not know (a newer PC); kept out of the store. */
    IGNORED,
}

internal object RoutineSyncProtocol {
    /**
     * The host sync entry after [result] arrived while [entry] was stored, and what to do next.
     *
     * The phone is authoritative (§7.4.1): a PC holding a HIGHER revision than this phone ever wrote
     * means the phone store was reset, so the phone jumps past it and resends the full set. Never
     * lowers [RoutineHostSync.localRevision]; an answer to an older request that the PC coalesced
     * away must not rewind a newer edit.
     */
    fun apply(
        entry: RoutineHostSync,
        result: RoutineSyncResultPayload,
        resultJson: String,
        nowUnixMs: Long,
    ): Pair<RoutineHostSync, RoutineSyncFollowUp> =
        when (result.status) {
            RoutineSyncStatuses.OK, RoutineSyncStatuses.PARTIAL ->
                if (result.storedRevision > entry.localRevision) {
                    entry.copy(localRevision = result.storedRevision + 1) to RoutineSyncFollowUp.RESEND
                } else {
                    val acked = maxOf(entry.ackedRevision, minOf(result.revision, entry.localRevision))
                    entry.copy(ackedRevision = acked, lastResultJson = resultJson, historyAsOfUnixMs = nowUnixMs) to RoutineSyncFollowUp.DONE
                }
            RoutineSyncStatuses.STALE_REVISION ->
                entry.copy(localRevision = maxOf(entry.localRevision, result.storedRevision + 1)) to RoutineSyncFollowUp.RESEND
            RoutineSyncStatuses.REVISION_CONFLICT -> entry.copy(localRevision = entry.localRevision + 1) to RoutineSyncFollowUp.RESEND
            RoutineSyncStatuses.BLOCKED_BY_PC, RoutineSyncStatuses.SCHEMA_TOO_NEW, RoutineSyncStatuses.PAYLOAD_TOO_LARGE ->
                entry.copy(lastResultJson = resultJson) to RoutineSyncFollowUp.STOP
            RoutineSyncStatuses.RATE_LIMITED, RoutineSyncStatuses.INTERNAL_ERROR -> entry to RoutineSyncFollowUp.RETRY_LATER
            else -> entry to RoutineSyncFollowUp.IGNORED
        }
}

/** One PC-run routine's sync state (§7.4.1 step 5). */
enum class RoutineSyncState {
    /** Not on the PC yet: never sent, or an edit the PC has not acknowledged ("Waiting to sync"). */
    PENDING,

    /** Accepted by the PC in its last answer. */
    SYNCED,

    /** Refused by the PC's validator; [RoutineSyncView.reasonCode] says why. */
    REJECTED,

    /** Switched off on the PC by the person at the PC ("Off on <PC>"); only the PC can clear it. */
    OFF_ON_PC,

    /** The PC refused this phone's whole set: blocked, schema too new, or too large. */
    REFUSED,
}

data class RoutineSyncView(
    val state: RoutineSyncState,
    /** The PC's per-routine reason (REJECTED) or the set-level status code (REFUSED). */
    val reasonCode: String? = null,
    val detail: String? = null,
)

/** The set-level state of one PC (§7.4.1 step 5, §8.7). */
data class RoutinePcSyncView(
    val hostIdentity: String,
    /** A `routine_sync_result` has been stored for this PC. */
    val hasResult: Boolean,
    /** Status of the last stored answer. */
    val status: String?,
    /** Edits the PC has not acknowledged yet. */
    val pending: Boolean,
    /** PC-side Pause all ("Paused on <PC>"). */
    val hostPaused: Boolean,
    /** This phone's Pause all as the PC applied it. */
    val ownerPaused: Boolean,
    val blocked: Boolean,
    /** The PC suspended this phone's set after 30 days away; the next sync lifts it. */
    val ownerSuspended: Boolean,
    val idleSource: String?,
    val sessionSource: String?,
    /** The PC runs a `pc.sensor` source (§7.3.2 `sensorTrigger`, routines S5). */
    val sensorTrigger: Boolean = false,
)

object RoutineSyncStates {
    /** The stored answer for [entry], or null when there is none or it cannot be read. Never throws. */
    fun lastResult(entry: RoutineHostSync?): RoutineSyncResultPayload? {
        val text = entry?.lastResultJson ?: return null
        return runCatching { RoutineJson.readSyncResult(JSONTokener(text).nextValue()) }.getOrNull()
    }

    fun pc(hostIdentity: String, entry: RoutineHostSync?): RoutinePcSyncView {
        val result = lastResult(entry)
        val status = result?.status
        return RoutinePcSyncView(
            hostIdentity = hostIdentity,
            hasResult = result != null,
            status = status,
            pending = entry == null || entry.ackedRevision < entry.localRevision,
            hostPaused = result?.hostPaused == true,
            ownerPaused = result?.ownerPaused == true,
            blocked = status == RoutineSyncStatuses.BLOCKED_BY_PC,
            ownerSuspended = result?.ownerSuspended == RoutineReasonCodes.OWNER_ABSENT,
            idleSource = result?.idleSource,
            sessionSource = result?.sessionSource,
            sensorTrigger = result?.sensorTrigger == true,
        )
    }

    /** The sync state of [routine], or null for a routine that runs on the phone. */
    fun routine(routine: Routine, entry: RoutineHostSync?): RoutineSyncView? {
        if (!RoutineTriggerTypes.isHostRun(routine.trigger?.type)) return null
        val id = routine.id ?: return RoutineSyncView(RoutineSyncState.PENDING)
        val result = lastResult(entry)
        when (result?.status) {
            RoutineSyncStatuses.BLOCKED_BY_PC, RoutineSyncStatuses.SCHEMA_TOO_NEW, RoutineSyncStatuses.PAYLOAD_TOO_LARGE ->
                return RoutineSyncView(RoutineSyncState.REFUSED, reasonCode = result.status)
        }
        // Independent of the phone's own switch and of pending edits: the PC's override stands until
        // the PC clears it (§7.4.1 step 6).
        if (result?.pcDisabled.orEmpty().contains(id)) return RoutineSyncView(RoutineSyncState.OFF_ON_PC)
        if (entry == null || entry.ackedRevision < entry.localRevision) return RoutineSyncView(RoutineSyncState.PENDING)
        val item = result?.results.orEmpty().firstOrNull { it.routineId == id } ?: return RoutineSyncView(RoutineSyncState.PENDING)
        return if (item.accepted) {
            RoutineSyncView(RoutineSyncState.SYNCED)
        } else {
            RoutineSyncView(RoutineSyncState.REJECTED, reasonCode = item.reasonCode ?: RoutineReasonCodes.REJECTED_BY_PC, detail = item.detail)
        }
    }

    /**
     * Whether the PC can offer [triggerType] per its last answer (§7.5 gating): null while unknown
     * (no answer yet), false when the PC reported no idle or session source, or no sensor source.
     */
    fun triggerAvailable(triggerType: String?, pc: RoutinePcSyncView?): Boolean? {
        if (pc == null || !pc.hasResult || pc.blocked) return null
        return when (triggerType) {
            RoutineTriggerTypes.PC_IDLE -> pc.idleSource != null
            RoutineTriggerTypes.PC_SESSION -> pc.sessionSource != null
            RoutineTriggerTypes.PC_SENSOR -> pc.sensorTrigger
            else -> null
        }
    }
}
