package com.clindsay94.remex.ui.files

import com.clindsay94.remex.ui.screens.RemoteFileEntry
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * The folder being shown on the "This phone" side (file browser redesign, 2026-10-08). Entries come back as
 * [RemoteFileEntry] so the same list and grid rows, sorting and hidden-item rules serve both devices.
 */
class PhoneBrowserModel(private val scope: CoroutineScope, private val source: PhoneFileSource) {
    data class State(
        val rootId: String? = null,
        val path: String = "",
        val entries: List<RemoteFileEntry> = emptyList(),
        val loading: Boolean = false,
        val failed: Boolean = false,
    )

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()
    private var job: Job? = null

    /** Shows `path` under the phone folder [rootId]. A newer call replaces an older one still loading. */
    fun open(rootId: String, path: String) {
        val normalized = FileTreeModel.normalize(path)
        job?.cancel()
        val keep = _state.value.rootId == rootId && _state.value.path == normalized
        _state.value = State(rootId, normalized, if (keep) _state.value.entries else emptyList(), loading = true)
        job = scope.launch {
            try {
                val entries = source.list(rootId, normalized).map {
                    RemoteFileEntry(it.name, it.isDirectory, maxOf(0L, it.sizeBytes), it.modifiedMs, id = it.documentId)
                }
                _state.value = State(rootId, normalized, entries)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                _state.value = State(rootId, normalized, failed = true)
            }
        }
    }

    fun refresh() {
        val s = _state.value
        if (s.rootId != null) open(s.rootId, s.path)
    }

    /** Nothing open (the folder was removed from the list). */
    fun clear() {
        job?.cancel()
        _state.value = State()
    }
}
