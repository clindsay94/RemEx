package com.clindsay94.remex.ui.components

import android.view.HapticFeedbackConstants

/**
 * Haptic feedback for command outcomes.
 *
 * **NO API-LEVEL FALLBACKS, AND THERE USED TO BE THREE (RemEx-bbfoy).** Each of the constants below
 * was guarded with `SDK_INT >= R` and a documented fallback for "API < 30". minSdk is 34, so those
 * branches could not run on any device this app installs on, and the KDoc described a second
 * behaviour that does not exist — a reader tuning haptics had two paths to reason about and one to
 * find.
 *
 * The system touch-feedback setting is respected for free: every call below uses the single-argument
 * `performHapticFeedback`, which honours it. Passing `FLAG_IGNORE_GLOBAL_SETTING` would buzz users
 * who turned haptics off, and nothing here does.
 */

/** Fired when a command is dispatched to the host (light "sent" tick). */
fun android.view.View.hapticCommandSent() =
    performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)

/** Fired when the host acknowledges a command successfully. */
fun android.view.View.hapticCommandAcknowledged() =
    performHapticFeedback(HapticFeedbackConstants.CONFIRM)

/** Fired when a command fails or times out. */
fun android.view.View.hapticCommandFailed() =
    performHapticFeedback(HapticFeedbackConstants.REJECT)

/** Fired when a 0.5s hold lifts a dashboard card into the selection state. */
fun android.view.View.hapticLift() =
    performHapticFeedback(HapticFeedbackConstants.GESTURE_START)

/** Fired when a tap adds/removes a card from an active multi-select. */
fun android.view.View.hapticSelectToggle() =
    performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
