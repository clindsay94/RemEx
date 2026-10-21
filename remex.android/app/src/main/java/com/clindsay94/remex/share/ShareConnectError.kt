package com.clindsay94.remex.share

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * Why the share screen's last Connect did not get through, shown on CHOOSE_PC (RemEx-172ba). A failed
 * attempt used to drop the person back on the picker with nothing said.
 */
internal class ShareConnectError {
    private val _message = MutableStateFlow<String?>(null)
    val message: StateFlow<String?> = _message.asStateFlow()

    private val _opensAppSettings = MutableStateFlow(false)

    /** True when only the app's settings page can fix it: Android stopped asking for the permission. */
    val opensAppSettings: StateFlow<Boolean> = _opensAppSettings.asStateFlow()

    /** Follows the client's connection errors, and forgets the last one once a connection is up. */
    fun track(scope: CoroutineScope, errors: Flow<String>, connected: Flow<Boolean>) {
        scope.launch { errors.collect { show(it, opensAppSettings = false) } }
        scope.launch { connected.collect { if (it) clear() } }
    }

    fun attempting() = clear()

    fun localNetworkRefused(message: String, permanently: Boolean) = show(message, permanently)

    private fun show(message: String, opensAppSettings: Boolean) {
        _opensAppSettings.value = opensAppSettings
        _message.value = message
    }

    private fun clear() {
        _message.value = null
        _opensAppSettings.value = false
    }
}
