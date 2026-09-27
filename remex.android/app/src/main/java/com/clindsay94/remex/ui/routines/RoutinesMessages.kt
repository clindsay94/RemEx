package com.clindsay94.remex.ui.routines

import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * A one-line message for the snackbar, optionally with an action. [id] is stamped by
 * [RoutinesMessageChannel.post], so two identical texts in a row are still two messages.
 */
data class RoutinesMessage(val text: String, val action: RoutinesMessageAction? = null, val id: Long = 0)

sealed interface RoutinesMessageAction {
    data class TestNow(val routineId: String) : RoutinesMessageAction

    data class OpenRun(val runId: String) : RoutinesMessageAction

    /** "Undo" after deleting a step in the editor (spec A4). */
    class Undo(val restore: () -> Unit) : RoutinesMessageAction

    /**
     * "Switch and run" (D8, §7.4.4): a PC routine for a PC RemEx is not set to. Opens Connection so
     * the person switches; the run starts once that PC is connected. Never switches by itself.
     */
    data class SwitchAndRun(val routineId: String, val hostIdentity: String, val testRun: Boolean) : RoutinesMessageAction
}

/**
 * The Routines snackbar's feed: LATEST WINS, never a queue (RemEx-pp0rt.6 review).
 *
 * The first version was a `SharedFlow` with 8 slots and `tryEmit`, drained by a collector that
 * suspended inside `showSnackbar`. A message with an action was shown `Indefinite`, so the
 * collector sat there until someone tapped; every later result (a skipped run, "Fix 2 things")
 * queued unseen behind it, and the ninth was dropped by `tryEmit` without a trace. Here a new
 * message REPLACES the pending one and the screen dismisses whatever is showing before it shows the
 * new one, so the newest result is always the one on screen and nothing waits behind a stale one.
 * [consumed] clears it, so a screen that comes back does not replay an old message.
 */
class RoutinesMessageChannel {
    private val sequence = AtomicLong()
    private val _current = MutableStateFlow<RoutinesMessage?>(null)

    /** The message to show now, or null when there is none. */
    val current: StateFlow<RoutinesMessage?> = _current.asStateFlow()

    /** Shows [message] next, replacing any message not yet shown or still on screen. */
    fun post(message: RoutinesMessage): RoutinesMessage {
        val stamped = message.copy(id = sequence.incrementAndGet())
        _current.value = stamped
        return stamped
    }

    /** [message] has been shown and dismissed; a newer message posted meanwhile is kept. */
    fun consumed(message: RoutinesMessage) {
        _current.compareAndSet(message, null)
    }
}
