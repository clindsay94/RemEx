package com.clindsay94.remex.ui.screens

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

data class ProcessInfo(
    val id: Int,
    val name: String,
    val cpu: Double,
    val ram: Double
)

class TaskManagerViewModel : ViewModel() {

    private val _processes = MutableStateFlow<List<ProcessInfo>>(emptyList())
    val processes: StateFlow<List<ProcessInfo>> = _processes.asStateFlow()

    init {
        viewModelScope.launch {
            RemexClientManager.processList.collect { data ->
                try {
                    val array = JSONArray(data)
                    val list = mutableListOf<ProcessInfo>()
                    for (i in 0 until array.length()) {
                        val obj = array.getJSONObject(i)
                        list.add(ProcessInfo(
                            id = obj.getInt("id"),
                            name = obj.getString("name"),
                            cpu = obj.optDouble("cpuUsage", 0.0),
                            ram = obj.optDouble("memoryUsage", 0.0) / (1024 * 1024) // Convert to MB
                        ))
                    }
                    _processes.value = list.sortedByDescending { it.cpu }
                } catch (e: Exception) {
                    e.printStackTrace()
                }
            }
        }
        refreshProcesses()
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
                // Refresh after a short delay
                kotlinx.coroutines.delay(1000)
                refreshProcesses()
            }
        }
    }
}
