package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineCancelReasons
import com.clindsay94.remex.routines.model.RoutineCancelledBy
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Why a running phone run is being stopped, and how that is recorded (spec §8.1, §8.6, §8.7). */
enum class RoutineCancelKind(
    /** `RoutineRun.cancelledBy`. */
    val cancelledBy: String,
    /** The run's reason code. */
    val reasonCode: String,
    /** `routine_cancel.reason` when a host step is in flight. */
    val wireReason: String,
) {
    /** The notification's Cancel, or the in-app run sheet. */
    USER(RoutineCancelledBy.PHONE, RoutineReasonCodes.CANCELLED_ON_PHONE, RoutineCancelReasons.USER),

    /** Pause all on the phone stopping an automatic run: a cancel from the side that paused (§8.6). */
    PAUSE(RoutineCancelledBy.PAUSE, RoutineReasonCodes.CANCELLED_ON_PHONE, RoutineCancelReasons.PAUSE),

    /** The routine was switched off while an automatic run was in progress. */
    DISABLED(RoutineCancelledBy.PHONE, RoutineReasonCodes.SKIPPED_DISABLED, RoutineCancelReasons.USER),

    /** The routine was deleted while it ran. */
    DELETED(RoutineCancelledBy.PHONE, RoutineReasonCodes.CANCELLED_ON_PHONE, RoutineCancelReasons.USER),
}

/**
 * The in-process handle on one phone run.
 *
 * Cancelling a run is not the same as cancelling its WorkManager job. While a host step is in flight
 * the runner must SEND `routine_cancel` and keep listening, because the PC may be mid-countdown and
 * the honest record is whatever the PC answers (`cancelled` by the phone, or `succeeded` if the verb
 * was already issued). So a cancel is a request the attached runner acts on; only a run with no
 * runner attached ([attached] false: still queued, or its process died) is cancelled at the job level.
 */
class RoutineRunControl internal constructor(val runId: String, val routineId: String) {
    private val _cancelRequest = MutableStateFlow<RoutineCancelKind?>(null)
    val cancelRequest: StateFlow<RoutineCancelKind?> = _cancelRequest.asStateFlow()

    @Volatile var attached: Boolean = false
        internal set

    /** The first request wins; a later one (a pause after the user's cancel) changes nothing. */
    fun requestCancel(kind: RoutineCancelKind) {
        _cancelRequest.compareAndSet(null, kind)
    }
}

/** Every run control in this process, by run id. */
class RoutineRunControls {
    private val byRun = ConcurrentHashMap<String, RoutineRunControl>()

    fun obtain(runId: String, routineId: String): RoutineRunControl =
        byRun.computeIfAbsent(runId) { RoutineRunControl(runId, routineId) }

    fun find(runId: String): RoutineRunControl? = byRun[runId]

    fun release(runId: String) {
        byRun.remove(runId)
    }
}
