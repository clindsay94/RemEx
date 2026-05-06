package com.clindsay94.remex.ui.screens

import android.app.Application
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.service.FileTransferNotificationManager
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
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
)

private data class ActiveDownload(
        val transferId: String,
        val destinationUri: Uri,
        val outputStream: OutputStream,
        val chunkChannel: Channel<ByteArray>,
        val writerJob: Job,
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
        browseRemote("/")
    }

    fun browseRemote(path: String = _remotePath.value) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = "Select a remote shared folder first."
            return
        }

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
                } else if (current.isEmpty()) {
                    entry.name
                } else if (current == "/") {
                    entry.name
                } else {
                    "$current/${entry.name}"
                }
        browseRemote(newPath)
    }

    fun uploadFromUri(uri: Uri) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = "Select a remote shared folder first."
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
        _statusText.value = "Uploading $targetName..."
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
                        val message = e.message ?: "Unknown error."
                        _statusText.value = "Upload failed: $message"
                        FileTransferNotificationManager.showTransferFailed(
                                getApplication(),
                                "Upload failed: $message",
                        )
                        resetTransferState()
                    }
                }
    }

    fun downloadToUri(entry: RemoteFileEntry, destinationUri: Uri) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = "Select a remote shared folder first."
            return
        }
        if (entry.isDirectory || _isTransferring.value) return

        val transferId = UUID.randomUUID().toString().replace("-", "")
        val resolver = getApplication<Application>().contentResolver
        val output = resolver.openOutputStream(destinationUri, "w")
        if (output == null) {
            _statusText.value = "Unable to open the selected save location."
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
        activeDownload = ActiveDownload(transferId, destinationUri, output, channel, writerJob)
        _isTransferring.value = true
        _transferProgress.value = 0f
        _statusText.value = "Downloading ${entry.name}..."
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
        _statusText.value = "Cancelled."
        FileTransferNotificationManager.cancel(getApplication())
        resetTransferState()
    }

    private fun handleFileTransferMessage(json: String) {
        try {
            val obj = JSONObject(json)
            when (obj.optString("type")) {
                "file_roots_response" -> handleRootsResponse(obj)
                "file_browse_response" -> handleBrowseResponse(obj)
                "file_transfer_progress" -> handleProgress(obj)
                "file_transfer_chunk" -> handleChunk(obj)
                "file_transfer_end" -> handleTransferEnd(obj)
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
            _statusText.value = "Shared folders unavailable: $errorMessage"
            return
        }

        val arr = response.optJSONArray("roots") ?: JSONArray()
        val roots = buildList {
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
                        )
                )
            }
        }

        _remoteRoots.value = roots
        if (_selectedRootId.value.isNullOrBlank()) {
            roots.firstOrNull()?.let { selectRoot(it.rootId) }
        }
        if (roots.isEmpty()) {
            _statusText.value = "This host is not exposing any shared folders yet."
        }
    }

    private fun handleBrowseResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileBrowseResponse") ?: return
        if (response.optString("requestId") != pendingBrowseRequestId) return

        _isLoading.value = false
        val errorMessage = response.optMeaningfulString("errorMessage")
        if (errorMessage != null) {
            _statusText.value = "Browse error: $errorMessage"
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

    private fun handleProgress(obj: JSONObject) {
        val progress = obj.optJSONObject("fileTransferProgress") ?: return
        if (progress.optString("transferId") != activeTransferId) return

        val total = progress.optLong("totalBytes", 0)
        val transferred = progress.optLong("bytesTransferred", 0)
        if (total > 0) {
            _transferProgress.value = transferred.toFloat() / total
            val action = if (activeDownload != null) "Downloading" else "Uploading"
            _statusText.value = "$action... ${(transferred * 100 / total)}%"
            FileTransferNotificationManager.showTransferProgress(
                    getApplication(),
                    fileName = activeTransferFileName ?: "File transfer",
                    isDownload = activeDownload != null,
                    transferredBytes = transferred,
                    totalBytes = total,
            )
        }
    }

    private fun handleChunk(obj: JSONObject) {
        val chunk = obj.optJSONObject("fileTransferChunk") ?: return
        val download = activeDownload ?: return
        if (chunk.optString("transferId") != download.transferId) return

        val data = Base64.decode(chunk.optString("dataBase64"), Base64.DEFAULT)
        download.chunkChannel.trySend(data)
    }

    private fun handleTransferEnd(obj: JSONObject) {
        val end = obj.optJSONObject("fileTransferEnd") ?: return
        if (end.optString("transferId") != activeTransferId) return

        val success = end.optBoolean("success", false)
        val errorMessage = end.optMeaningfulString("errorMessage")

        if (success) {
            if (activeDownload != null) {
                val fileName = activeTransferFileName ?: "File"
                cleanupDownload(deletePartial = false)
                _statusText.value = "Download complete."
                FileTransferNotificationManager.showTransferComplete(
                        getApplication(),
                        fileName,
                        isDownload = true,
                )
            } else {
                val fileName = activeTransferFileName ?: "File"
                _statusText.value = "Upload complete."
                FileTransferNotificationManager.showTransferComplete(
                        getApplication(),
                        fileName,
                        isDownload = false,
                )
                browseRemote(_remotePath.value)
            }
        } else {
            cleanupDownload(deletePartial = true)
            val message = errorMessage ?: "Unknown error."
            _statusText.value = "Transfer failed: $message"
            FileTransferNotificationManager.showTransferFailed(
                    getApplication(),
                    "Transfer failed: $message",
            )
        }

        resetTransferState()
    }

    private fun cleanupDownload(deletePartial: Boolean) {
        val download = activeDownload ?: return
        activeDownload = null
        download.chunkChannel.close()
        if (deletePartial) {
            download.writerJob.cancel()
            viewModelScope.launch(Dispatchers.IO) {
                try {
                    download.outputStream.close()
                } catch (_: Exception) {}
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
            try {
                download.writerJob.join()
            } catch (_: Exception) {}
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
                        val name = if (nameIndex >= 0) cursor.getString(nameIndex) else null
                        val size =
                                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex))
                                        cursor.getLong(sizeIndex)
                                else null
                        return name to size
                    }
                }
        return null to null
    }

    private fun combineRemotePath(currentPath: String, childName: String): String {
        val normalizedCurrent = currentPath.replace('\\', '/').trimEnd('/')
        return if (normalizedCurrent.isEmpty() || normalizedCurrent == "/") childName
        else "$normalizedCurrent/$childName"
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
