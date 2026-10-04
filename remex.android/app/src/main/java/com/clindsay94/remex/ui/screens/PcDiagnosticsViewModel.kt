package com.clindsay94.remex.ui.screens

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.withContext
import org.json.JSONObject

/**
 * The PC logs and diagnostics screen's view-model (RemEx-pp4cm.13). All of the behaviour is in
 * [PcLogsController]; this only wires it to the live connection: authenticated state in, the native
 * send out, and the PC's `diagnostic_*` messages from [RemexClientManager.diagnosticMessages].
 */
class PcDiagnosticsViewModel : ViewModel() {

    /** Connected AND authenticated: the PC refuses these requests before the host's reconnect ack. */
    private val ready: StateFlow<Boolean> =
            RemexClientManager.isAuthenticated
                    .map { it && RemexCoreClient.isLibraryLoaded }
                    .stateIn(viewModelScope, SharingStarted.Eagerly, false)

    val controller =
            PcLogsController(
                    scope = viewModelScope,
                    send = { envelope ->
                        // The native send marshals the envelope and blocks briefly: off the main thread.
                        withContext(Dispatchers.IO) {
                            RemexCoreClient.SendMessage(envelope)
                                    .getOrNull()
                                    ?.let { runCatching { JSONObject(it).optBoolean("success", false) }.getOrNull() } ==
                                    true
                        }
                    },
                    inbound = RemexClientManager.diagnosticMessages,
                    ready = ready,
            )

    override fun onCleared() {
        // The scope is cancelled with the view-model, which ends the poll; this makes the stop explicit
        // for anything that reads the flag in between.
        controller.setVisible(false)
        super.onCleared()
    }
}
