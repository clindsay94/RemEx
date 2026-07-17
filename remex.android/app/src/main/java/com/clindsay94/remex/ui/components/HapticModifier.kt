package com.clindsay94.remex.ui.components

import android.view.HapticFeedbackConstants
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role

/**
 * A modifier that provides consistent haptic feedback on click.
 */
fun Modifier.hapticClickable(
    enabled: Boolean = true,
    onClickLabel: String? = null,
    role: Role? = null,
    hapticType: Int = HapticFeedbackConstants.KEYBOARD_TAP,
    onClick: () -> Unit
): Modifier = composed {
    val view = LocalView.current
    this.clickable(
        enabled = enabled,
        onClickLabel = onClickLabel,
        role = role,
        interactionSource = remember { MutableInteractionSource() },
        indication = null // Let the component handle indication (ripple) or add it if needed
    ) {
        if (enabled) {
            view.performHapticFeedback(hapticType)
            onClick()
        }
    }
}

/**
 * Fired when a command is dispatched to the host (light "sent" tick).
 */
fun android.view.View.hapticCommandSent() =
    performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)

/**
 * Fired when the host acknowledges a command successfully.
 * Falls back to VIRTUAL_KEY on API < 30.
 */
fun android.view.View.hapticCommandAcknowledged() {
    if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R)
        performHapticFeedback(HapticFeedbackConstants.CONFIRM)
    else
        performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
}

/**
 * Fired when a command fails or times out.
 * Falls back to LONG_PRESS on API < 30.
 */
fun android.view.View.hapticCommandFailed() {
    if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R)
        performHapticFeedback(HapticFeedbackConstants.REJECT)
    else
        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
}

/**
 * Fired when a 0.5s hold lifts a dashboard card into the selection state.
 * Falls back to LONG_PRESS on API < 30.
 */
fun android.view.View.hapticLift() {
    if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R)
        performHapticFeedback(HapticFeedbackConstants.GESTURE_START)
    else
        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
}

/** Fired when a tap adds/removes a card from an active multi-select. */
fun android.view.View.hapticSelectToggle() =
    performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
