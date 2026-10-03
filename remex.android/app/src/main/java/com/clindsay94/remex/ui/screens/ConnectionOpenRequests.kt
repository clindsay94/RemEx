package com.clindsay94.remex.ui.screens

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * "Open Connection with Add a PC showing" (sweep P3, RemEx-wqo7a.7): the Pair action on Home, Files
 * and the process list asks for it, then navigates to Connection, which opens the sheet once and
 * consumes the request.
 *
 * A StateFlow, not a one-shot event: the request is made before the Connection route exists, so it
 * has to wait for the screen to compose and read it. [consumeAddPc] clears it so coming back to
 * Connection later never reopens the sheet by itself.
 */
object ConnectionOpenRequests {
    private val _addPc = MutableStateFlow(false)
    val addPc: StateFlow<Boolean> = _addPc.asStateFlow()

    fun requestAddPc() {
        _addPc.value = true
    }

    /** True when there was a request to act on; the caller opens the sheet. */
    fun consumeAddPc(): Boolean {
        if (!_addPc.value) return false
        _addPc.value = false
        return true
    }
}
