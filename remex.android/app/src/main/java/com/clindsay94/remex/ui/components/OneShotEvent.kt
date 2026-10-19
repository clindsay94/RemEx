package com.clindsay94.remex.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import kotlinx.coroutines.flow.Flow

/**
 * The latest of [events], held until the caller sets it back to null once shown. It is collected only
 * while STARTED and forgotten with the caller, so a snackbar cut short by leaving the page never comes
 * back on the next visit (AN-4.1).
 */
@Composable
fun <T : Any> rememberOneShotEvent(events: Flow<T>): MutableState<T?> {
    val latest = remember(events) { mutableStateOf<T?>(null) }
    val lifecycleOwner = LocalLifecycleOwner.current
    LaunchedEffect(events, lifecycleOwner) {
        lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
            events.collect { latest.value = it }
        }
    }
    return latest
}
