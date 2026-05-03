package com.clindsay94.remex.ui.screens

import android.app.Application
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

private const val TAG = "FileTransferVM"

data class RemoteFileEntry(
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
)

class FileTransferViewModel(application: Application) : AndroidViewModel(application) {

    private val _remotePath = MutableStateFlow("/")
    val remotePath = _remotePath.asStateFlow()

    private val _remoteEntries = MutableStateFlow<List<RemoteFileEntry>>(emptyList())
    val remoteEntries = _remoteEntries.asStateFlow()

    private val _isLoading = MutableStateFlow(false)
    val isLoading = _isLoading.asStateFlow()

    private val _isTransferring = MutableStateFlow(false)
    val isTransferring = _isTransferring.asStateFlow()

    private val _transferProgress = MutableStateFlow(0f)
    val transferProgress = _transferProgress.asStateFlow()

    private val _statusText = MutableStateFlow("")
    val statusText = _statusText.asStateFlow()

    private var _pendingBrowseRequestId: String? = null
    private var _activeTransferId: String? = null

    init {
        viewModelScope.launch {
            RemexClientManager.fileTransferMessages.collect { json ->
                handleFileTransferMessage(json)
            }
        }
    }

    fun browseRemote(path: String = _remotePath.value) {
        val requestId = UUID.randomUUID().toString().replace("-", "")
        _pendingBrowseRequestId = requestId
        _remotePath.value = path
        _isLoading.value = true
        _statusText.value = ""

        val msg = JSONObject().apply {
            put("type", "FileBrowseRequest")
            put("fileBrowseRequest", JSONObject().apply {
                put("requestId", requestId)
                put("path", path)
            })
        }

        sendMessage(msg)
    }

    fun navigateInto(entry: RemoteFileEntry) {
        if (!entry.isDirectory) return
        val current = _remotePath.value.trimEnd('/', '\\')
        val newPath = if (entry.name == "..") {
            val parent = current.substringBeforeLast('/')
            if (parent.isEmpty()) "/" else parent
        } else {
            "$current/${entry.name}"
        }
        browseRemote(newPath)
    }

    fun download(entry: RemoteFileEntry) {
        if (entry.isDirectory || _isTransferring.value) return
        val transferId = UUID.randomUUID().toString().replace("-", "")
        _activeTransferId = transferId
        _isTransferring.value = true
        _statusText.value = "Downloading ${entry.name}…"

        val remotePath = "${_remotePath.value.trimEnd('/', '\\')}/${entry.name}"
        val msg = JSONObject().apply {
            put("type", "FileTransferStart")
            put("fileTransferStart", JSONObject().apply {
                put("transferId", transferId)
                put("direction", "download")
                put("remotePath", remotePath)
                put("fileName", entry.name)
                put("totalBytes", 0)
                put("sha256Base64", "")
            })
        }
        sendMessage(msg)
    }

    fun cancelTransfer() {
        val id = _activeTransferId ?: return
        val msg = JSONObject().apply {
            put("type", "FileTransferCancel")
            put("fileTransferCancel", JSONObject().apply {
                put("transferId", id)
            })
        }
        sendMessage(msg)
        _isTransferring.value = false
        _transferProgress.value = 0f
        _statusText.value = "Cancelled."
        _activeTransferId = null
    }

    private fun handleFileTransferMessage(json: String) {
        try {
            val obj = JSONObject(json)
            when (obj.optString("type")) {
                "FileBrowseResponse" -> {
                    val resp = obj.optJSONObject("fileBrowseResponse") ?: return
                    if (resp.optString("requestId") != _pendingBrowseRequestId) return
                    _isLoading.value = false
                    val err = resp.optString("errorMessage", "")
                    if (err.isNotBlank()) {
                        _statusText.value = "Browse error: $err"
                        return
                    }
                    val entries = mutableListOf<RemoteFileEntry>()
                    val path = _remotePath.value
                    if (path != "/" && path != "\\") {
                        entries.add(RemoteFileEntry("..", isDirectory = true, sizeBytes = 0))
                    }
                    val arr: JSONArray = resp.optJSONArray("entries") ?: JSONArray()
                    for (i in 0 until arr.length()) {
                        val e = arr.getJSONObject(i)
                        entries.add(
                            RemoteFileEntry(
                                name = e.optString("name"),
                                isDirectory = e.optBoolean("isDirectory"),
                                sizeBytes = e.optLong("sizeBytes"),
                            )
                        )
                    }
                    _remoteEntries.value = entries
                }

                "FileTransferProgress" -> {
                    val prog = obj.optJSONObject("fileTransferProgress") ?: return
                    if (prog.optString("transferId") != _activeTransferId) return
                    val total = prog.optLong("totalBytes", 0)
                    val transferred = prog.optLong("bytesTransferred", 0)
                    if (total > 0) {
                        _transferProgress.value = transferred.toFloat() / total
                        _statusText.value = "Downloading… ${(transferred * 100 / total)}%"
                    }
                }

                "FileTransferEnd" -> {
                    val end = obj.optJSONObject("fileTransferEnd") ?: return
                    if (end.optString("transferId") != _activeTransferId) return
                    _isTransferring.value = false
                    _transferProgress.value = 0f
                    _activeTransferId = null
                    if (end.optBoolean("success", false)) {
                        _statusText.value = "Download complete."
                    } else {
                        _statusText.value = "Transfer failed: ${end.optString("errorMessage")}"
                    }
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error handling file transfer message", e)
        }
    }

    private fun sendMessage(msg: JSONObject) {
        try {
            RemexCoreClient.SendMessage(msg.toString())
        } catch (e: Exception) {
            Log.e(TAG, "SendMessage failed", e)
            _statusText.value = "Error: ${e.message}"
        }
    }
}
