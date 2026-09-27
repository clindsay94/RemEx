package com.clindsay94.remex.ui.splash

import androidx.compose.ui.geometry.Rect
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The seam between the system splash (MainActivity's SplashScreen exit phase) and the Live
 * Handshake overlay (RemEx-8g6n0).
 *
 * The overlay composes under the system splash, so it must know two things to continue it rather
 * than restart it: WHEN the system splash came off ([done]; the overlay's clock starts there, not
 * at composition, or its whole intro would play hidden), and WHERE the mark was last drawn
 * ([markWindowRect], the brand window's bounds in window pixels; the overlay's first frame draws
 * its mark there and eases it into its own layout). Both are flows so the overlay redraws its
 * held first frame the moment the rect is known, before the system splash comes off.
 *
 * Process-scoped on purpose: MainActivity writes it before any composition exists. [reset] at
 * every Activity creation so a warm start never reuses a stale rect.
 */
object SystemSplashHandoff {
    private val _done = MutableStateFlow(false)
    val done: StateFlow<Boolean> = _done.asStateFlow()

    private val _markWindowRect = MutableStateFlow<Rect?>(null)
    val markWindowRect: StateFlow<Rect?> = _markWindowRect.asStateFlow()

    fun reset() {
        _markWindowRect.value = null
        _done.value = false
    }

    fun recordMark(rect: Rect?) {
        _markWindowRect.value = rect
    }

    fun markDone() {
        _done.value = true
    }
}
