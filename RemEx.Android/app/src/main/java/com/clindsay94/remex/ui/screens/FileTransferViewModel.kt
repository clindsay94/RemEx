package com.clindsay94.remex.ui.screens

import android.app.Application
import android.content.res.AssetFileDescriptor
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.R
import com.clindsay94.remex.service.FileTransferNotificationManager
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject

private const val TAG = "FileTransferVM"
private const val CHUNK_SIZE = 65_536

private fun JSONObject.optMeaningfulString(key: String): String? {
    if (!has(key) || isNull(key)) return null
    val value = optString(key, "").trim()
    return value.takeUnless { it.isEmpty() || it.equals("null", ignoreCase = true) }
}

data class RemoteFileEntry(
        val name: String,
        val isDirectory: Boolean,
        val sizeBytes: Long,
)

data class RemoteSharedRoot(
        val rootId: String,
        val displayName: String,
        val isWritable: Boolean,
        val canRename: Boolean,
        val canMove: Boolean,
        val canDelete: Boolean,
        val canRemoveRoot: Boolean = false,
)

private data class ActiveDownload(
        val transferId: String,
        val destinationUri: Uri,
        val outputStream: OutputStream,
        val chunkChannel: Channel<ByteArray>,
        val writerJob: Job,
        val digest: MessageDigest,
)

class FileTransferViewModel(application: Application) : AndroidViewModel(application) {

    private val _remotePath = MutableStateFlow("/")
    val remotePath = _remotePath.asStateFlow()

    private val _remoteEntries = MutableStateFlow<List<RemoteFileEntry>>(emptyList())
    val remoteEntries = _remoteEntries.asStateFlow()

    private val _remoteRoots = MutableStateFlow<List<RemoteSharedRoot>>(emptyList())
    val remoteRoots = _remoteRoots.asStateFlow()

    private val _selectedRootId = MutableStateFlow<String?>(null)
    val selectedRootId = _selectedRootId.asStateFlow()

    private val _isLoading = MutableStateFlow(false)
    val isLoading = _isLoading.asStateFlow()

    private val _isTransferring = MutableStateFlow(false)
    val isTransferring = _isTransferring.asStateFlow()

    private val _transferProgress = MutableStateFlow(0f)
    val transferProgress = _transferProgress.asStateFlow()

    private val _statusText = MutableStateFlow("")
    val statusText = _statusText.asStateFlow()

    // ── Selection mode ────────────────────────────────────────────────────────
    private val _isSelectionMode = MutableStateFlow(false)
    val isSelectionMode = _isSelectionMode.asStateFlow()

    private val _selectedEntryNames = MutableStateFlow<Set<String>>(emptySet())
    val selectedEntryNames = _selectedEntryNames.asStateFlow()

    // ── Pending manage / root-manage operations ───────────────────────────────
    private val pendingManageOps = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val pendingRootManageOps = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()

    private var pendingBrowseRequestId: String? = null
    private var activeTransferId: String? = null
    private var activeTransferFileName: String? = null
    private var activeDownload: ActiveDownload? = null
    private var activeUploadJob: Job? = null

    init {
        viewModelScope.launch {
            RemexClientManager.fileTransferMessages.collect { json ->
                handleFileTransferMessage(json)
            }
        }
        loadRemoteRoots()
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    fun enterSelectionMode(entry: RemoteFileEntry) {
        if (entry.name == "..") return
        _isSelectionMode.value = true
        _selectedEntryNames.value = setOf(entry.name)
    }

    fun toggleEntrySelection(entry: RemoteFileEntry) {
        if (entry.name == "..") return
        if (!_isSelectionMode.value) {
            enterSelectionMode(entry)
            return
        }
        val current = _selectedEntryNames.value
        val updated = if (entry.name in current) current - entry.name else current + entry.name
        _selectedEntryNames.value = updated
        if (updated.isEmpty()) _isSelectionMode.value = false
    }

    fun selectAll() {
        _selectedEntryNames.value = _remoteEntries.value
            .filter { it.name != ".." }
            .map { it.name }
            .toSet()
    }

    fun clearSelection() {
        _isSelectionMode.value = false
        _selectedEntryNames.value = emptySet()
    }

    // ── Remote roots / browsing ───────────────────────────────────────────────

    fun loadRemoteRoots() {
        _isLoading.value = true
        _statusText.value = ""
        sendMessage(
                JSONObject().apply {
                    put("type", "file_roots_request")
                    put("fileRootsRequest", JSONObject())
                }
        )
    }

    fun selectRoot(rootId: String) {
        if (_selectedRootId.value == rootId) return
        _selectedRootId.value = rootId
        clearSelection()
        browseRemote("/")
    }

    fun browseRemote(path: String = _remotePath.value) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_select_folder_first)
            return
        }

        clearSelection()
        val requestId = UUID.randomUUID().toString().replace("-", "")
        pendingBrowseRequestId = requestId
        _remotePath.value = path
        _isLoading.value = true
        _statusText.value = ""

        sendMessage(
                JSONObject().apply {
                    put("type", "file_browse_request")
                    put(
                            "fileBrowseRequest",
                            JSONObject().apply {
                                put("requestId", requestId)
                                put("path", path)
                                put("rootId", rootId)
                                put("relativePath", path)
                            }
                    )
                }
        )
    }

    fun navigateInto(entry: RemoteFileEntry) {
        if (!entry.isDirectory) return
        val current = _remotePath.value.replace('\\', '/').trimEnd('/')
        val newPath =
                if (entry.name == "..") {
                    val parent = current.substringBeforeLast('/')
                    if (parent.isEmpty()) "/" else parent
                } else if (current.isEmpty() || current == "/") {
                    entry.name
                } else {
                    "$current/${entry.name}"
                }
        browseRemote(newPath)
    }

    // ── File management ───────────────────────────────────────────────────────

    fun deleteEntry(entry: RemoteFileEntry) {
        val rootId = _selectedRootId.value ?: return
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_deleting_single, entry.name)
            val requestId = UUID.randomUUID().toString().replace("-", "")
            val deferred = CompletableDeferred<JSONObject>()
            pendingManageOps[requestId] = deferred

            sendMessage(
                    JSONObject().apply {
                        put("type", "file_manage_request")
                        put(
                                "fileManageRequest",
                                JSONObject().apply {
                                    put("requestId", requestId)
                                    put("rootId", rootId)
                                    put(
                                            "relativePath",
                                            combineRemotePath(_remotePath.value, entry.name)
                                    )
                                    put("operation", "delete")
                                }
                        )
                    }
            )

            try {
                val response = withTimeout(30_000) { deferred.await() }
                val error =
                        response
                                .optJSONObject("fileManageResponse")
                                ?.optMeaningfulString("errorMessage")
                if (error != null) {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_delete_failed, error)
                } else {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_deleted_success)
                    browseRemote()
                }
            } catch (_: TimeoutCancellationException) {
                _statusText.value = getApplication<Application>().getString(R.string.file_transfer_delete_timeout)
            } finally {
                pendingManageOps.remove(requestId)
                _isLoading.value = false
            }
        }
    }

    fun deleteSelectedEntries() {
        val rootId = _selectedRootId.value ?: return
        val toDelete = _remoteEntries.value
            .filter { it.name in _selectedEntryNames.value && it.name != ".." }
        if (toDelete.isEmpty()) return

        clearSelection()
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_deleting_multiple, toDelete.size)
            var errorCount = 0

            for (entry in toDelete) {
                val requestId = UUID.randomUUID().toString().replace("-", "")
                val deferred = CompletableDeferred<JSONObject>()
                pendingManageOps[requestId] = deferred

                sendMessage(
                        JSONObject().apply {
                            put("type", "file_manage_request")
                            put(
                                    "fileManageRequest",
                                    JSONObject().apply {
                                        put("requestId", requestId)
                                        put("rootId", rootId)
                                        put(
                                                "relativePath",
                                                combineRemotePath(_remotePath.value, entry.name)
                                        )
                                        put("operation", "delete")
                                    }
                            )
                        }
                )

                try {
                    val response = withTimeout(30_000) { deferred.await() }
                    val error =
                            response
                                    .optJSONObject("fileManageResponse")
                                    ?.optMeaningfulString("errorMessage")
                    if (error != null) errorCount++
                } catch (_: TimeoutCancellationException) {
                    errorCount++
                } finally {
                    pendingManageOps.remove(requestId)
                }
            }

            _statusText.value =
                    if (errorCount == 0) getApplication<Application>().getString(R.string.file_transfer_deleted_multiple_success, toDelete.size)
                    else getApplication<Application>().getString(R.string.file_transfer_deleted_multiple_failed, toDelete.size - errorCount, toDelete.size, errorCount)
            _isLoading.value = false
            browseRemote()
        }
    }

    fun renameEntry(entry: RemoteFileEntry, newName: String) {
        val trimmed = newName.trim()
        if (trimmed.isEmpty() || trimmed == entry.name) return
        val rootId = _selectedRootId.value ?: return

        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_renaming_single, entry.name)
            val requestId = UUID.randomUUID().toString().replace("-", "")
            val deferred = CompletableDeferred<JSONObject>()
            pendingManageOps[requestId] = deferred

            sendMessage(
                    JSONObject().apply {
                        put("type", "file_manage_request")
                        put(
                                "fileManageRequest",
                                JSONObject().apply {
                                    put("requestId", requestId)
                                    put("rootId", rootId)
                                    put(
                                            "relativePath",
                                            combineRemotePath(_remotePath.value, entry.name)
                                    )
                                    put("operation", "rename")
                                    put("newName", trimmed)
                                }
                        )
                    }
            )

            try {
                val response = withTimeout(30_000) { deferred.await() }
                val error =
                        response
                                .optJSONObject("fileManageResponse")
                                ?.optMeaningfulString("errorMessage")
                if (error != null) {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_rename_failed, error)
                } else {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_renamed_success)
                    browseRemote()
                }
            } catch (_: TimeoutCancellationException) {
                _statusText.value = getApplication<Application>().getString(R.string.file_transfer_rename_timeout)
            } finally {
                pendingManageOps.remove(requestId)
                _isLoading.value = false
            }
        }
    }

    fun pinCurrentFolder() {
        val rootId = _selectedRootId.value ?: return
        val path = _remotePath.value
        if (path == "/" || path == "\\") return

        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_adding_shortcut)
            val requestId = UUID.randomUUID().toString().replace("-", "")
            val deferred = CompletableDeferred<JSONObject>()
            pendingRootManageOps[requestId] = deferred

            sendMessage(
                    JSONObject().apply {
                        put("type", "file_root_manage_request")
                        put(
                                "fileRootManageRequest",
                                JSONObject().apply {
                                    put("requestId", requestId)
                                    put("operation", "add")
                                    put("sourceRootId", rootId)
                                    put("sourceRelativePath", path)
                                }
                        )
                    }
            )

            try {
                val response = withTimeout(30_000) { deferred.await() }
                val respObj = response.optJSONObject("fileRootManageResponse")
                val error = respObj?.optMeaningfulString("errorMessage")
                if (error != null) {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_pin_failed, error)
                } else {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_shortcut_added)
                    updateRootsFromResponse(respObj)
                }
            } catch (_: TimeoutCancellationException) {
                _statusText.value = getApplication<Application>().getString(R.string.file_transfer_pin_timeout)
            } finally {
                pendingRootManageOps.remove(requestId)
                _isLoading.value = false
            }
        }
    }

    fun removeRoot(rootId: String) {
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_removing_shortcut)
            val requestId = UUID.randomUUID().toString().replace("-", "")
            val deferred = CompletableDeferred<JSONObject>()
            pendingRootManageOps[requestId] = deferred

            sendMessage(
                    JSONObject().apply {
                        put("type", "file_root_manage_request")
                        put(
                                "fileRootManageRequest",
                                JSONObject().apply {
                                    put("requestId", requestId)
                                    put("operation", "remove")
                                    put("rootId", rootId)
                                }
                        )
                    }
            )

            try {
                val response = withTimeout(30_000) { deferred.await() }
                val respObj = response.optJSONObject("fileRootManageResponse")
                val error = respObj?.optMeaningfulString("errorMessage")
                if (error != null) {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_remove_failed, error)
                } else {
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_shortcut_removed)
                    updateRootsFromResponse(respObj)
                }
            } catch (_: TimeoutCancellationException) {
                _statusText.value = getApplication<Application>().getString(R.string.file_transfer_pin_timeout)
            } finally {
                pendingRootManageOps.remove(requestId)
                _isLoading.value = false
            }
        }
    }

    // ── Uploads / downloads ───────────────────────────────────────────────────

    fun uploadFromUri(uri: Uri) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_select_folder_first)
            return
        }
        if (_isTransferring.value) return

        val (displayName, sizeBytes) = queryMetadata(uri)
        val targetName = displayName ?: "upload-${System.currentTimeMillis()}"
        val remoteRelativePath = combineRemotePath(_remotePath.value, targetName)
        val transferId = UUID.randomUUID().toString().replace("-", "")

        activeTransferId = transferId
        activeTransferFileName = targetName
        _isTransferring.value = true
        _transferProgress.value = 0f
        _statusText.value = getApplication<Application>().getString(R.string.file_transfer_uploading_single, targetName)
        FileTransferNotificationManager.showTransferStarted(
                getApplication(),
                targetName,
                isDownload = false,
        )

        activeUploadJob =
                viewModelScope.launch(Dispatchers.IO) {
                    try {
                        val resolver = getApplication<Application>().contentResolver
                        val input =
                                resolver.openInputStream(uri)
                                        ?: throw IllegalStateException(
                                                "Unable to open the selected file."
                                        )

                        input.use { stream ->
                            sendMessage(
                                    JSONObject().apply {
                                        put("type", "file_transfer_start")
                                        put(
                                                "fileTransferStart",
                                                JSONObject().apply {
                                                    put("transferId", transferId)
                                                    put("direction", "upload")
                                                    put("remotePath", remoteRelativePath)
                                                    put("remoteRootId", rootId)
                                                    put("remoteRelativePath", remoteRelativePath)
                                                    put("fileName", targetName)
                                                    put("totalBytes", sizeBytes ?: 0L)
                                                    put("sha256", "")
                                                }
                                        )
                                    }
                            )

                            val digest = MessageDigest.getInstance("SHA-256")
                            val buffer = ByteArray(CHUNK_SIZE)
                            var offset = 0L

                            while (true) {
                                val read = stream.read(buffer)
                                if (read <= 0) break

                                digest.update(buffer, 0, read)
                                sendMessage(
                                        JSONObject().apply {
                                            put("type", "file_transfer_chunk")
                                            put(
                                                    "fileTransferChunk",
                                                    JSONObject().apply {
                                                        put("transferId", transferId)
                                                        put("offset", offset)
                                                        put(
                                                                "dataBase64",
                                                                Base64.encodeToString(
                                                                        buffer,
                                                                        0,
                                                                        read,
                                                                        Base64.NO_WRAP
                                                                )
                                                        )
                                                    }
                                            )
                                        }
                                )
                                offset += read
                            }

                            sendMessage(
                                    JSONObject().apply {
                                        put("type", "file_transfer_end")
                                        put(
                                                "fileTransferEnd",
                                                JSONObject().apply {
                                                    put("transferId", transferId)
                                                    put("success", true)
                                                    put(
                                                            "sha256",
                                                            Base64.encodeToString(
                                                                    digest.digest(),
                                                                    Base64.NO_WRAP
                                                            )
                                                    )
                                                }
                                        )
                                    }
                            )
                        }
                    } catch (_: CancellationException) {
                        Log.i(TAG, "Upload cancelled for $transferId")
                    } catch (e: Exception) {
                        Log.e(TAG, "Upload failed", e)
                        val message = e.message ?: getApplication<Application>().getString(R.string.file_transfer_unknown_error)
                        _statusText.value = getApplication<Application>().getString(R.string.file_transfer_upload_failed, message)
                        FileTransferNotificationManager.showTransferFailed(
                                getApplication(),
                                getApplication<Application>().getString(R.string.file_transfer_upload_failed, message),
                        )
                        resetTransferState()
                    }
                }
    }

    fun downloadToUri(entry: RemoteFileEntry, destinationUri: Uri) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_select_folder_first)
            return
        }
        if (entry.isDirectory || _isTransferring.value) return

        val transferId = UUID.randomUUID().toString().replace("-", "")
        val resolver = getApplication<Application>().contentResolver
        val output = resolver.openOutputStream(destinationUri, "w")
        if (output == null) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_unable_open_location)
            return
        }

        val channel = Channel<ByteArray>(Channel.UNLIMITED)
        val writerJob =
                viewModelScope.launch(Dispatchers.IO) {
                    output.use { stream ->
                        for (chunk in channel) {
                            stream.write(chunk)
                        }
                        stream.flush()
                    }
                }

        activeTransferId = transferId
        activeTransferFileName = entry.name
        activeDownload =
                ActiveDownload(
                        transferId = transferId,
                        destinationUri = destinationUri,
                        outputStream = output,
                        chunkChannel = channel,
                        writerJob = writerJob,
                        digest = MessageDigest.getInstance("SHA-256"),
                )
        _isTransferring.value = true
        _transferProgress.value = 0f
        _statusText.value = getApplication<Application>().getString(R.string.file_transfer_downloading_single, entry.name)
        FileTransferNotificationManager.showTransferStarted(
                getApplication(),
                entry.name,
                isDownload = true,
        )

        val remoteRelativePath = combineRemotePath(_remotePath.value, entry.name)
        sendMessage(
                JSONObject().apply {
                    put("type", "file_transfer_start")
                    put(
                            "fileTransferStart",
                            JSONObject().apply {
                                put("transferId", transferId)
                                put("direction", "download")
                                put("remotePath", remoteRelativePath)
                                put("remoteRootId", rootId)
                                put("remoteRelativePath", remoteRelativePath)
                                put("fileName", entry.name)
                                put("totalBytes", 0)
                                put("sha256", "")
                            }
                    )
                }
        )
    }

    fun cancelTransfer() {
        val id = activeTransferId ?: return
        sendMessage(
                JSONObject().apply {
                    put("type", "file_transfer_cancel")
                    put("fileTransferCancel", JSONObject().apply { put("transferId", id) })
                }
        )
        activeUploadJob?.cancel()
        cleanupDownload(deletePartial = true)
        _statusText.value = getApplication<Application>().getString(R.string.file_transfer_cancelled)
        FileTransferNotificationManager.cancel(getApplication())
        resetTransferState()
    }

    // ── Message handling ──────────────────────────────────────────────────────

    private fun handleFileTransferMessage(json: String) {
        try {
            val obj = JSONObject(json)
            when (obj.optString("type")) {
                "file_roots_response" -> handleRootsResponse(obj)
                "file_browse_response" -> handleBrowseResponse(obj)
                "file_transfer_progress" -> handleProgress(obj)
                "file_transfer_chunk" -> handleChunk(obj)
                "file_transfer_end" -> handleTransferEnd(obj)
                "file_manage_response" -> handleManageResponse(obj)
                "file_root_manage_response" -> handleRootManageResponse(obj)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error handling file transfer message", e)
        }
    }

    private fun handleRootsResponse(obj: JSONObject) {
        _isLoading.value = false
        val response = obj.optJSONObject("fileRootsResponse") ?: return
        val errorMessage = response.optMeaningfulString("errorMessage")
        if (errorMessage != null) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_shared_folders_unavailable, errorMessage)
            return
        }

        val arr = response.optJSONArray("roots") ?: JSONArray()
        val roots = parseRoots(arr)
        _remoteRoots.value = roots
        if (_selectedRootId.value.isNullOrBlank()) {
            roots.firstOrNull()?.let { selectRoot(it.rootId) }
        }
        if (roots.isEmpty()) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_no_shared_folders)
        }
    }

    private fun handleBrowseResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileBrowseResponse") ?: return
        if (response.optString("requestId") != pendingBrowseRequestId) return

        _isLoading.value = false
        val errorMessage = response.optMeaningfulString("errorMessage")
        if (errorMessage != null) {
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_browse_error, errorMessage)
            return
        }

        val path = response.optString("relativePath", _remotePath.value)
        _remotePath.value = if (path.isBlank()) "/" else path

        val entries = mutableListOf<RemoteFileEntry>()
        if (_remotePath.value != "/" && _remotePath.value != "\\") {
            entries.add(RemoteFileEntry("..", isDirectory = true, sizeBytes = 0))
        }

        val arr = response.optJSONArray("entries") ?: JSONArray()
        for (index in 0 until arr.length()) {
            val item = arr.getJSONObject(index)
            entries.add(
                    RemoteFileEntry(
                            name = item.optString("name"),
                            isDirectory = item.optBoolean("isDirectory"),
                            sizeBytes = item.optLong("sizeBytes"),
                    )
            )
        }
        _remoteEntries.value = entries
    }

    private fun handleManageResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileManageResponse") ?: return
        val requestId = response.optString("requestId")
        pendingManageOps[requestId]?.complete(obj)
    }

    private fun handleRootManageResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileRootManageResponse") ?: return
        val requestId = response.optString("requestId")
        pendingRootManageOps[requestId]?.complete(obj)
    }

    private fun handleProgress(obj: JSONObject) {
        val progress = obj.optJSONObject("fileTransferProgress") ?: return
        if (progress.optString("transferId") != activeTransferId) return

        val total = progress.optLong("totalBytes", 0)
        val transferred = progress.optLong("bytesTransferred", 0)
        val action = if (activeDownload != null) getApplication<Application>().getString(R.string.file_transfer_action_downloading) else getApplication<Application>().getString(R.string.file_transfer_action_uploading)

        if (total > 0) {
            _transferProgress.value = (transferred.toFloat() / total).coerceIn(0f, 1f)
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_progress_status, action, "${(transferred * 100 / total)}%")
        } else if (transferred > 0) {
            _transferProgress.value = 0f
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_progress_status, action, formatBytes(transferred))
        } else {
            return
        }

        FileTransferNotificationManager.showTransferProgress(
                getApplication(),
                fileName = activeTransferFileName ?: "File transfer",
                isDownload = activeDownload != null,
                transferredBytes = transferred,
                totalBytes = total,
        )
    }

    private fun formatBytes(bytes: Long): String =
            when {
                bytes >= 1_073_741_824L -> "%.1f GB".format(bytes / 1_073_741_824.0)
                bytes >= 1_048_576L -> "%.1f MB".format(bytes / 1_048_576.0)
                bytes >= 1_024L -> "%.1f KB".format(bytes / 1_024.0)
                else -> "$bytes B"
            }

    private fun handleChunk(obj: JSONObject) {
        val chunk = obj.optJSONObject("fileTransferChunk") ?: return
        val download = activeDownload ?: return
        if (chunk.optString("transferId") != download.transferId) return

        val data = Base64.decode(chunk.optString("dataBase64"), Base64.DEFAULT)
        download.digest.update(data)
        download.chunkChannel.trySend(data)
    }

    private fun handleTransferEnd(obj: JSONObject) {
        val end = obj.optJSONObject("fileTransferEnd") ?: return
        if (end.optString("transferId") != activeTransferId) return

        val success = end.optBoolean("success", false)
        val errorMessage = end.optMeaningfulString("errorMessage")
        val expectedSha256 = end.optMeaningfulString("sha256")

        if (success) {
            val download = activeDownload
            if (download != null) {
                val fileName = activeTransferFileName ?: getApplication<Application>().getString(R.string.file_transfer_file_fallback)
                val actualSha256 = Base64.encodeToString(download.digest.digest(), Base64.NO_WRAP)
                val hashMismatch = expectedSha256 != null && expectedSha256 != actualSha256
                if (hashMismatch) {
                    Log.w(TAG, "Download SHA-256 mismatch: expected=$expectedSha256 actual=$actualSha256")
                    cleanupDownload(deletePartial = true)
                    _statusText.value = getApplication<Application>().getString(R.string.file_transfer_download_failed_integrity)
                    FileTransferNotificationManager.showTransferFailed(
                            getApplication(),
                            getApplication<Application>().getString(R.string.file_transfer_download_failed_sha256),
                    )
                    resetTransferState()
                    return
                }
                cleanupDownload(deletePartial = false)
                _statusText.value = getApplication<Application>().getString(R.string.file_transfer_download_complete)
                FileTransferNotificationManager.showTransferComplete(
                        getApplication(),
                        fileName,
                        isDownload = true,
                )
            } else {
                val fileName = activeTransferFileName ?: getApplication<Application>().getString(R.string.file_transfer_file_fallback)
                _statusText.value = getApplication<Application>().getString(R.string.file_transfer_upload_complete)
                FileTransferNotificationManager.showTransferComplete(
                        getApplication(),
                        fileName,
                        isDownload = false,
                )
                browseRemote(_remotePath.value)
            }
        } else {
            cleanupDownload(deletePartial = true)
            val message = errorMessage ?: getApplication<Application>().getString(R.string.file_transfer_unknown_error)
            _statusText.value = getApplication<Application>().getString(R.string.file_transfer_transfer_failed, message)
            FileTransferNotificationManager.showTransferFailed(
                    getApplication(),
                    getApplication<Application>().getString(R.string.file_transfer_transfer_failed, message),
            )
        }

        resetTransferState()
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private fun updateRootsFromResponse(respObj: JSONObject?) {
        val arr = respObj?.optJSONArray("roots") ?: return
        val roots = parseRoots(arr)
        val prevId = _selectedRootId.value
        _remoteRoots.value = roots
        if (prevId != null && roots.none { it.rootId == prevId }) {
            roots.firstOrNull()?.let { selectRoot(it.rootId) }
        }
    }

    private fun parseRoots(arr: JSONArray): List<RemoteSharedRoot> = buildList {
        for (index in 0 until arr.length()) {
            val item = arr.getJSONObject(index)
            add(
                RemoteSharedRoot(
                    rootId = item.optString("rootId"),
                    displayName = item.optString("displayName"),
                    isWritable = item.optBoolean("isWritable"),
                    canRename = item.optBoolean("canRename"),
                    canMove = item.optBoolean("canMove"),
                    canDelete = item.optBoolean("canDelete"),
                    canRemoveRoot = item.optBoolean("canRemoveRoot"),
                )
            )
        }
    }

    private fun cleanupDownload(deletePartial: Boolean) {
        val download = activeDownload ?: return
        activeDownload = null
        download.chunkChannel.close()
        if (deletePartial) {
            download.writerJob.cancel()
            viewModelScope.launch(Dispatchers.IO) {
                try { download.outputStream.close() } catch (_: Exception) {}
                try {
                    DocumentsContract.deleteDocument(
                            getApplication<Application>().contentResolver,
                            download.destinationUri,
                    )
                } catch (_: Exception) {}
            }
            return
        }
        viewModelScope.launch(Dispatchers.IO) {
            try { download.writerJob.join() } catch (_: Exception) {}
        }
    }

    private fun resetTransferState() {
        activeTransferId = null
        activeTransferFileName = null
        activeUploadJob = null
        _isTransferring.value = false
        _transferProgress.value = 0f
    }

    private fun queryMetadata(uri: Uri): Pair<String?, Long?> {
        val resolver = getApplication<Application>().contentResolver
        var name: String? = null
        var size: Long? = null

        resolver.query(
                        uri,
                        arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
                        null,
                        null,
                        null
                )
                ?.use { cursor ->
                    if (cursor.moveToFirst()) {
                        val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                        if (nameIndex >= 0) name = cursor.getString(nameIndex)
                        if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) {
                            size = cursor.getLong(sizeIndex)
                        }
                    }
                }

        val initialSize = size
        if (initialSize == null || initialSize <= 0L) {
            try {
                resolver.openAssetFileDescriptor(uri, "r")?.use { afd ->
                    val length = afd.length
                    if (length != AssetFileDescriptor.UNKNOWN_LENGTH && length > 0L) {
                        size = length
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "openAssetFileDescriptor fallback failed for $uri", e)
            }
        }

        return name to size
    }

    private fun combineRemotePath(currentPath: String, childName: String): String {
        val normalizedCurrent = currentPath.replace('\\', '/').trimEnd('/')
        return if (normalizedCurrent.isEmpty() || normalizedCurrent == "/") childName
        else "$normalizedCurrent/$childName"
    }

    private fun sendMessage(msg: JSONObject) {
        try {
            RemexCoreClient.SendMessage(msg.toString()).getOrNull()
        } catch (e: Exception) {
            Log.e(TAG, "SendMessage failed", e)
            _statusText.value = "Error: ${e.message}"
        }
    }
}
