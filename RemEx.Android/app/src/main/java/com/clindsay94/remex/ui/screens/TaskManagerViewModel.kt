package com.clindsay94.remex.ui.screens

import android.app.Application
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

data class ProcessInfo(val id: Int, val name: String, val cpu: Double, val ram: Double)

enum class ProcessSortField {
    NAME,
    CPU,
    RAM,
    PID
}

class TaskManagerViewModel(application: Application) : AndroidViewModel(application) {

    companion object {
        private val EXCLUDED_PROCESSES =
                setOf(
                        "svchost",
                        "system idle process",
                        "system",
                        "registry",
                        "smss",
                        "csrss",
                        "wininit",
                        "services",
                        "lsass",
                        "fontdrvhost",
                        "dwm",
                        "conhost",
                        "sihost",
                        "dashost",
                        "ctfmon",
                        "dllhost",
                        "wudfhost",
                        "searchindexer",
                        "securityhealthservice",
                        "sgrmbroker",
                        "spoolsv",
                        "lsaiso",
                        "memory compression"
                )
    }

    private val settingsManager = SettingsManager(application)

    val taskManagerCardShapePreset =
            settingsManager.taskManagerCardShapePresetFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    0f
            )

    val cardCornerRadius =
            settingsManager.cardCornerRadiusFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    20
            )

    val isConnected: StateFlow<Boolean> = RemexClientManager.isConnected

    private val _processes = MutableStateFlow<List<ProcessInfo>>(emptyList())
    private val _searchQuery = MutableStateFlow("")
    private val _sortField = MutableStateFlow(ProcessSortField.CPU)
    private val _sortDescending = MutableStateFlow(true)

    val searchQuery: StateFlow<String> = _searchQuery.asStateFlow()
    val sortField: StateFlow<ProcessSortField> = _sortField.asStateFlow()
    val sortDescending: StateFlow<Boolean> = _sortDescending.asStateFlow()

    val processes: StateFlow<List<ProcessInfo>> =
            combine(_processes, _searchQuery, _sortField, _sortDescending) {
                            processes,
                            search,
                            field,
                            descending ->
                        // Dedupe by PID: hosts can occasionally emit multiple entries with
                        // the same id (kernel threads, container/host views), and Compose's
                        // LazyColumn throws on duplicate keys.
                        val deduped = processes.distinctBy { it.id }

                        val excluded =
                                deduped.filter { proc ->
                                    !EXCLUDED_PROCESSES.contains(proc.name.lowercase())
                                }

                        val filtered =
                                if (search.isBlank()) {
                                    excluded
                                } else {
                                    val query = search.trim().lowercase()
                                    excluded.filter {
                                        it.name.lowercase().contains(query) ||
                                                it.id.toString().contains(query)
                                    }
                                }

                        val sorted =
                                when (field) {
                                    ProcessSortField.NAME ->
                                            filtered.sortedBy { it.name.lowercase() }
                                    ProcessSortField.CPU -> filtered.sortedBy { it.cpu }
                                    ProcessSortField.RAM -> filtered.sortedBy { it.ram }
                                    ProcessSortField.PID -> filtered.sortedBy { it.id }
                                }

                        if (descending) sorted.reversed() else sorted
                    }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

    init {
        viewModelScope.launch {
            RemexClientManager.processList.collect { data ->
                try {
                    val array = JSONArray(data)
                    val list = mutableListOf<ProcessInfo>()
                    for (i in 0 until array.length()) {
                        val obj = array.getJSONObject(i)
                        list.add(
                                ProcessInfo(
                                        id = obj.getInt("id"),
                                        name = obj.getString("name"),
                                        cpu = obj.optDouble("cpuUsage", 0.0),
                                        ram = obj.optDouble("memoryUsage", 0.0) / (1024 * 1024)
                                )
                        )
                    }
                    _processes.value = list
                } catch (e: Exception) {
                    Log.w("TaskManagerVM", "Failed to parse process list", e)
                } finally {
                    _isRefreshing.value = false
                }
            }
        }

        // Double-fetch on init: first primes CPU trackers, second shows real values
        viewModelScope.launch {
            delay(500) // Let connection stabilize
            refreshProcesses()
            delay(1500) // Wait for host to calculate CPU deltas
            refreshProcesses()
        }

        // Periodic auto-refresh every 4 seconds
        viewModelScope.launch {
            while (true) {
                delay(4000)
                refreshProcesses()
            }
        }
    }

    fun updateSearchQuery(query: String) {
        _searchQuery.value = query
    }

    fun updateSortField(field: ProcessSortField) {
        if (_sortField.value == field) {
            _sortDescending.value = !_sortDescending.value
            return
        }

        _sortField.value = field
        _sortDescending.value = field != ProcessSortField.NAME
    }

    private val _isRefreshing = MutableStateFlow(false)
    val isRefreshing: StateFlow<Boolean> = _isRefreshing.asStateFlow()

    private val _killError = MutableStateFlow<String?>(null)
    val killError: StateFlow<String?> = _killError.asStateFlow()

    fun clearKillError() { _killError.value = null }

    fun refreshProcesses() {
        viewModelScope.launch {
            if (RemexCoreClient.isLibraryLoaded) {
                _isRefreshing.value = true
                val request = JSONObject().apply { put("type", "process_list_request") }
                RemexCoreClient.SendMessage(request.toString())
                // Spinner cleared by processList collector when data arrives.
                // Safety net: clear after 5s in case host doesn't respond.
                delay(5000)
                _isRefreshing.value = false
            }
        }
    }

    fun killProcess(pid: Int) {
        viewModelScope.launch {
            if (RemexCoreClient.isLibraryLoaded) {
                val accessKey = settingsManager.accessKeyFlow.first()
                val request =
                        JSONObject().apply {
                            put("action", "KillProcess")
                            put(
                                    "parameters",
                                    JSONObject().apply {
                                        put("ProcessId", pid.toString())
                                        if (accessKey.isNotBlank()) put("AccessKey", accessKey)
                                    }
                            )
                        }
                val responseJson = RemexCoreClient.SendCommand(request.toString())
                val success = try {
                    responseJson.isNotBlank() &&
                        JSONObject(responseJson).optBoolean("commandSuccess", true)
                } catch (_: Exception) {
                    responseJson.isNotBlank()
                }
                if (!success) {
                    _killError.value = "Failed to kill process $pid"
                }
                // Request a fresh process list immediately; the host will respond
                // via the processList SharedFlow when it's ready.
                refreshProcesses()
            }
        }
    }
}
