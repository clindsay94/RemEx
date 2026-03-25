package com.clindsay94.remex.ui.screens

import android.app.Application
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
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

data class ProcessInfo(
    val id: Int,
    val name: String,
    val cpu: Double,
    val ram: Double
)

enum class ProcessSortField {
    NAME,
    CPU,
    RAM,
    PID
}

class TaskManagerViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val taskManagerCardShapePreset = settingsManager.taskManagerCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), "rounded")

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 20)

    private val _processes = MutableStateFlow<List<ProcessInfo>>(emptyList())
    private val _searchQuery = MutableStateFlow("")
    private val _sortField = MutableStateFlow(ProcessSortField.CPU)
    private val _sortDescending = MutableStateFlow(true)

    val searchQuery: StateFlow<String> = _searchQuery.asStateFlow()
    val sortField: StateFlow<ProcessSortField> = _sortField.asStateFlow()
    val sortDescending: StateFlow<Boolean> = _sortDescending.asStateFlow()

    val processes: StateFlow<List<ProcessInfo>> = combine(
        _processes,
        _searchQuery,
        _sortField,
        _sortDescending
    ) { processes, search, field, descending ->
        val filtered = if (search.isBlank()) {
            processes
        } else {
            val query = search.trim().lowercase()
            processes.filter {
                it.name.lowercase().contains(query) || it.id.toString().contains(query)
            }
        }

        val sorted = when (field) {
            ProcessSortField.NAME -> filtered.sortedBy { it.name.lowercase() }
            ProcessSortField.CPU -> filtered.sortedBy { it.cpu }
            ProcessSortField.RAM -> filtered.sortedBy { it.ram }
            ProcessSortField.PID -> filtered.sortedBy { it.id }
        }

        if (descending) sorted.reversed() else sorted
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

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
                } catch (_: Exception) {
                }
            }
        }
        refreshProcesses()
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

    fun refreshProcesses() {
        viewModelScope.launch {
            if (RemexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("type", "process_list_request")
                }
                RemexCoreClient.SendMessage(request.toString())
            }
        }
    }

    fun killProcess(pid: Int) {
        viewModelScope.launch {
            if (RemexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("action", "KillProcess")
                    put("parameters", JSONObject().apply {
                        put("ProcessId", pid.toString())
                    })
                }
                RemexCoreClient.SendCommand(request.toString())
                delay(1000)
                refreshProcesses()
            }
        }
    }
}
