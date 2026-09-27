package com.clindsay94.remex.routines

import android.content.Context
import com.clindsay94.remex.routines.model.RoutineInboundMessage
import kotlinx.coroutines.CancellationException

/**
 * Where decoded host -> phone routine messages go (routines spec §7.7), fed by the eager collector
 * in `RemexClientManager.initialize`. `routine_step_result` never comes here: it has its own flow,
 * awaited by the runner (RemexClientManager.routineStepResults).
 *
 * **NEVER THROWS.** It runs inside the one long-lived collector; an exception escaping here would end
 * that collection for the life of the process, and every later routine message would vanish.
 */
internal object RoutineInboundHandler {
    suspend fun handle(context: Context, message: RoutineInboundMessage) {
        try {
            when (message) {
                // History (§7.3.6, §8.8): PC runs land in the same store as the phone's own.
                is RoutineInboundMessage.RunReport -> Routines.repository(context).applyRunReport(message.payload)
                // Consumed by the S4 sync client (RoutineSyncClient); nothing sends routines_sync before it.
                is RoutineInboundMessage.SyncResult -> RoutineLog.d("routine_sync_result received; the sync client is not in this build.")
                // Presented by the S5 queue slice (RoutineNotificationPresenter for PC-run notifies).
                is RoutineInboundMessage.Notify -> RoutineLog.d("routine_notify received; PC-run notifications are not in this build.")
                is RoutineInboundMessage.Malformed -> RoutineLog.w("Unreadable ${message.type} from the PC was ignored.")
                is RoutineInboundMessage.StepResult, is RoutineInboundMessage.Ignored -> Unit
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Handling a routine message from the PC failed.", e)
        }
    }
}
