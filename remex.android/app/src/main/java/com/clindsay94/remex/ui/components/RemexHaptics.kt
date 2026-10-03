package com.clindsay94.remex.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.remember
import androidx.compose.ui.hapticfeedback.HapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.clindsay94.remex.service.FileTransferEngine
import com.clindsay94.remex.service.TransferState
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map

/**
 * What just happened, in the app's words. Call sites name the event; [RemexHaptics.typeFor] is the
 * one place that decides how it feels (3.0 motion + haptics pass, RemEx-wqo7a.8).
 *
 * Haptics mark meaning, not every touch: a switch flipping, a slider landing on a detent, a command
 * confirmed or refused, a long-press that lifts something. A plain [Press] is silent: tapping a button
 * that moves you somewhere or opens something is not news.
 */
enum class RemexHapticEvent {
    /** A plain button tap that moves you somewhere or opens something. Silent ([RemexHaptics.typeFor]). */
    Press,

    /** One option chosen out of several: a segment, a filter chip, a menu item, a sort order. */
    Select,

    /** A switch, checkbox or toggle button turned on. */
    ToggleOn,

    /** A switch, checkbox or toggle button turned off. */
    ToggleOff,

    /** A slider landed on a detent (the contrast slider's -1, 0 and 1), or a dragged row passed a slot. */
    Detent,

    /** A stepped slider moved one step. Lighter than [Detent], because steps come quickly. */
    Step,

    /** A finger lifted at the end of a drag: a slider let go, a dragged card or row dropped. */
    Release,

    /** A command was sent to the PC; its outcome is still to come. */
    CommandSent,

    /** Something the user asked for worked: a command confirmed, a pairing accepted, a batch of transfers done. */
    Confirm,

    /** Something failed or was refused: a pairing rejected, a transfer failed, a save with problems. */
    Reject,

    /** A long-press took effect: edit mode, selection mode, or a drag picked something up. */
    LongPress,

    /** Pull-to-refresh passed its threshold. */
    Refresh,
}

/** The haptic map, and the pure decisions about when an event fires. Plain JVM, so it is unit-tested. */
object RemexHaptics {
    /**
     * How each event feels. Every type here goes through `View.performHapticFeedback` (via
     * [LocalHapticFeedback]), which honours the system "Touch feedback" setting, so turning
     * haptics off in Android settings turns all of these off. Nothing in the app uses the vibrator.
     * Null means no haptic: plain taps stay silent so the ones that do buzz mean something.
     */
    fun typeFor(event: RemexHapticEvent): HapticFeedbackType? =
        when (event) {
            RemexHapticEvent.Press -> null
            RemexHapticEvent.Select -> HapticFeedbackType.SegmentTick
            RemexHapticEvent.ToggleOn -> HapticFeedbackType.ToggleOn
            RemexHapticEvent.ToggleOff -> HapticFeedbackType.ToggleOff
            RemexHapticEvent.Detent -> HapticFeedbackType.SegmentTick
            RemexHapticEvent.Step -> HapticFeedbackType.SegmentFrequentTick
            RemexHapticEvent.Release -> HapticFeedbackType.GestureEnd
            RemexHapticEvent.CommandSent -> HapticFeedbackType.ContextClick
            RemexHapticEvent.Confirm -> HapticFeedbackType.Confirm
            RemexHapticEvent.Reject -> HapticFeedbackType.Reject
            RemexHapticEvent.LongPress -> HapticFeedbackType.LongPress
            RemexHapticEvent.Refresh -> HapticFeedbackType.GestureThresholdActivate
        }

    /** The event for a toggle that is now [on]. */
    fun toggle(on: Boolean): RemexHapticEvent = if (on) RemexHapticEvent.ToggleOn else RemexHapticEvent.ToggleOff

    /**
     * True when a slider moving from [previous] to [next] has just landed on one of [detents]. Holding
     * still on a detent (the slider reports the same snapped value again) does not tick twice.
     */
    fun landedOnDetent(previous: Float, next: Float, detents: FloatArray): Boolean =
        next != previous && detents.any { it == next }

    /** True when a stepped slider's (already snapped) value has moved to a different step. */
    fun steppedTo(previous: Float, next: Float): Boolean = next != previous

    /**
     * True when an error shown on screen is new: there is one now, and it is not the one that
     * already buzzed. Clearing an error, or the same error coming back on recomposition or
     * rotation, stays silent.
     */
    fun failureAppeared(previous: String?, current: String?): Boolean = current != null && current != previous
}

/**
 * When a batch of transfers ends with a haptic, and which one (3.0 haptics pass).
 *
 * One buzz per batch, never one per file: a 50-photo upload confirms once when the last file lands.
 * The batch ends when nothing is waiting or moving any more; it confirms if anything finished and
 * nothing failed, and rejects if anything failed. A batch the user cancelled ends silently.
 */
object TransferOutcomeHaptics {
    /** What the tracker remembers between queue emissions. */
    data class Tracker(
        val states: Map<String, TransferState>,
        val inFlight: Boolean,
        val sawDone: Boolean = false,
        val sawFailure: Boolean = false,
    )

    /**
     * Still part of the batch. Paused counts: pausing the rest of a batch is not the batch ending,
     * so it confirms when the resumed files finish rather than at the pause.
     */
    private val IN_FLIGHT =
        setOf(
            TransferState.Queued,
            TransferState.Negotiating,
            TransferState.Active,
            TransferState.Paused,
            TransferState.Verifying,
        )

    private val TERMINAL = setOf(TransferState.Done, TransferState.Failed, TransferState.Cancelled)

    /**
     * The starting point: whatever the queue holds when the app comes to the foreground is the
     * baseline, so transfers that finished in the background do not buzz on return.
     */
    fun baseline(states: Map<String, TransferState>): Tracker =
        Tracker(states = states, inFlight = states.values.any { it in IN_FLIGHT })

    /** The next tracker for the queue's new [states], plus the event to fire now, if any. */
    fun next(tracker: Tracker, states: Map<String, TransferState>): Pair<Tracker, RemexHapticEvent?> {
        fun reached(target: TransferState): Boolean =
            states.any { (id, state) ->
                val before = tracker.states[id]
                state == target && before != null && before !in TERMINAL
            }
        val sawDone = tracker.sawDone || reached(TransferState.Done)
        val sawFailure = tracker.sawFailure || reached(TransferState.Failed)
        val inFlight = states.values.any { it in IN_FLIGHT }
        if (tracker.inFlight && !inFlight) {
            val event =
                when {
                    sawFailure -> RemexHapticEvent.Reject
                    sawDone -> RemexHapticEvent.Confirm
                    else -> null
                }
            return Tracker(states = states, inFlight = false) to event
        }
        return Tracker(states = states, inFlight = inFlight, sawDone = sawDone, sawFailure = sawFailure) to null
    }
}

/** Performs [RemexHapticEvent]s through Compose's [HapticFeedback]. Get one with [rememberRemexHaptics]. */
@Stable
class RemexHapticPerformer internal constructor(private val feedback: HapticFeedback) {
    fun perform(event: RemexHapticEvent) {
        RemexHaptics.typeFor(event)?.let { feedback.performHapticFeedback(it) }
    }
}

/** The app's haptics for this composition, through [LocalHapticFeedback]. */
@Composable
fun rememberRemexHaptics(): RemexHapticPerformer {
    val feedback = LocalHapticFeedback.current
    return remember(feedback) { RemexHapticPerformer(feedback) }
}

/**
 * For a stepped slider (one with `steps`): returns the function to call from `onValueChange` with
 * the snapped value, which ticks a [RemexHapticEvent.Step] each time the thumb reaches a new step.
 * Remembers the last value it saw itself rather than reading the slider's value back, because a
 * slider whose value is saved asynchronously reports the same new step more than once before the
 * saved value catches up, and that would tick twice.
 *
 * Until the user first moves the slider it follows [initial], so a saved value that loads after the
 * first composition does not make the first drag tick falsely.
 */
@Composable
fun rememberSliderStepHaptics(initial: Float): (Float) -> Unit {
    val haptics = rememberRemexHaptics()
    val last = remember { floatArrayOf(initial) }
    val touched = remember { booleanArrayOf(false) }
    SideEffect { if (!touched[0]) last[0] = initial }
    return remember(haptics, last) {
        { value ->
            touched[0] = true
            if (RemexHaptics.steppedTo(last[0], value)) {
                last[0] = value
                haptics.perform(RemexHapticEvent.Step)
            }
        }
    }
}

/**
 * Confirms or rejects a finished batch of file transfers while the app is in the foreground,
 * wherever the user is in it ([TransferOutcomeHaptics]). Collection restarts on every resume with a
 * fresh baseline, so whatever finished while the app was in the background stays silent; the
 * transfer notification already reported it.
 */
@Composable
fun TransferOutcomeHapticsEffect() {
    val haptics = rememberRemexHaptics()
    val lifecycleOwner = LocalLifecycleOwner.current
    LaunchedEffect(lifecycleOwner, haptics) {
        lifecycleOwner.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            var tracker: TransferOutcomeHaptics.Tracker? = null
            FileTransferEngine.queue
                .map { queue -> queue.associate { it.id to it.state } }
                .distinctUntilChanged()
                .collect { states ->
                    val current = tracker
                    if (current == null) {
                        tracker = TransferOutcomeHaptics.baseline(states)
                    } else {
                        val (next, event) = TransferOutcomeHaptics.next(current, states)
                        tracker = next
                        event?.let(haptics::perform)
                    }
                }
        }
    }
}
