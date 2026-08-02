package com.clindsay94.remex.widget

import android.util.Log
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.json.JSONObject

private const val TAG = "WidgetCommand"

/**
 * Where a widget tap's command runs after the broadcast that carried it has finished (RemEx-66rf).
 *
 * A Glance `ActionCallback` is delivered through a `BroadcastReceiver` held open with `goAsync()`,
 * and a receiver that does not finish inside the system's window is killed — on a foreground
 * broadcast that window is about ten seconds, and an overrun is an ANR. `RemexCoreClient.SendCommand`
 * now waits for the PC's real answer and is budgeted at exactly ten seconds, so awaiting it inside
 * that window is a coin flip against the very limit it would breach.
 *
 * The receiver therefore hands the command over and returns. Deliberately a SEPARATE scope from the
 * tiles' one: the constraint here is a broadcast deadline, theirs is service teardown, and putting a
 * shared helper in one component's package would misfile the reason.
 */
private val WidgetCommandScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

/**
 * Sends a command from a widget tap without holding the broadcast open for the round trip.
 *
 * **THE TOAST STAYS OPTIMISTIC, AND THAT IS UNCHANGED RATHER THAN OVERLOOKED.** Both call sites toast
 * "command sent" / "launching", which is what actually happened here — the command left the phone.
 * They never claimed the PC had carried it out, so nothing about them was made honest by the PC's
 * answer becoming available, and nothing about them becomes a lie by not waiting for it. Reporting
 * the real outcome from a widget needs a place to put it that survives the broadcast; that is its own
 * piece of work (RemEx-mug0), not something to bolt on here.
 *
 * The failure IS logged, which is new: before this the outcome did not exist to log, and "the widget
 * did nothing" with an empty logcat is an unpleasant thing to be handed.
 */
internal fun sendWidgetCommand(action: String, parameters: JSONObject = JSONObject()) {
    val commandJson =
        JSONObject()
            .apply {
                put("action", action)
                put("parameters", parameters)
            }
            .toString()

    WidgetCommandScope.launch {
        val response = RemexCoreClient.SendCommand(commandJson).getOrNull()
        if (response == null || !JSONObject(response).optBoolean("success", false)) {
            Log.w(TAG, "Widget command $action did not succeed: $response")
        }
    }
}
