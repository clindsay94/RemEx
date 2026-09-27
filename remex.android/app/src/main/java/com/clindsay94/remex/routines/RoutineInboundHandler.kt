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
                // History (§7.3.6, §8.8): PC runs land in the same store as the phone's own, then the
                // sync client answers a waiting Run on PC and updates the progress notification.
                is RoutineInboundMessage.RunReport -> {
                    val applied = Routines.repository(context).applyRunReport(message.payload)
                    Routines.syncClient(context).onRunReport(message.payload, applied)
                }
                // Solicited or not (§7.3.2): stored per PC and turned into sync states (RemEx-pp0rt.12).
                is RoutineInboundMessage.SyncResult -> Routines.syncClient(context).onSyncResult(message.payload)
                // PC messages and the PC-run countdown mirror (routines S5, §7.3.5, §8.6), then the ack.
                is RoutineInboundMessage.Notify -> Routines.syncClient(context).onNotify(message.payload)
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
