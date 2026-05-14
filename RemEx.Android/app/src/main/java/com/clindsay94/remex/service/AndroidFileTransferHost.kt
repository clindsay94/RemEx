package com.clindsay94.remex.service

import android.content.Context
import android.net.Uri
import android.util.Base64
import android.util.Log
import androidx.documentfile.provider.DocumentFile
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

object AndroidFileTransferHost {
    private const val TAG = "AndroidFileTransferHost"
    private const val CHUNK_SIZE = 65536
    private const val PROGRESS_CHUNK_INTERVAL = 10
    private const val MAX_UPLOAD_BYTES = 5_000_000_000L

    private var job: Job? = null
    private lateinit var settingsManager: SettingsManager
    private lateinit var context: Context
    private val scope = CoroutineScope(Dispatchers.IO)

    private class TransferState(
        val transferId: String,
        val direction: String,
        val outputStream: OutputStream? = null,
        val inputStream: InputStream? = null,
        val digest: MessageDigest = MessageDigest.getInstance("SHA-256"),
        var bytesTransferred: Long = 0,
        val totalBytes: Long = 0,
        var chunkCount: Int = 0,
        var job: Job? = null
    ) {
        fun cleanup() {
            outputStream?.close()
            inputStream?.close()
            job?.cancel()
        }
    }

    private val activeTransfers = ConcurrentHashMap<String, TransferState>()

    fun start(ctx: Context) {
        context = ctx.applicationContext
        settingsManager = SettingsManager(context)
        job?.cancel()
        job = scope.launch {
            RemexClientManager.fileTransferMessages.collect { json ->
                handleMessage(json)
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
        activeTransfers.values.forEach { it.cleanup() }
        activeTransfers.clear()
    }

    private suspend fun handleMessage(json: String) {
        try {
            val obj = JSONObject(json)
            when (obj.optString("type")) {
                "file_roots_request" -> handleRootsRequest()
                "file_browse_request" -> handleBrowseRequest(obj.optJSONObject("fileBrowseRequest") ?: return)
                "file_transfer_start" -> handleTransferStart(obj.optJSONObject("fileTransferStart") ?: return)
                "file_transfer_chunk" -> handleTransferChunk(obj.optJSONObject("fileTransferChunk") ?: return)
                "file_transfer_end" -> handleTransferEnd(obj.optJSONObject("fileTransferEnd") ?: return)
                "file_transfer_cancel" -> handleTransferCancel(obj.optJSONObject("fileTransferCancel") ?: return)
                "file_manage_request" -> handleManageRequest(obj.optJSONObject("fileManageRequest") ?: return)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error handling file transfer message", e)
        }
    }

    private suspend fun handleRootsRequest() {
        try {
            val uris = settingsManager.sharedFolderUrisFlow.first()
            val rootsArray = JSONArray()
            for (uriStr in uris) {
                val uri = Uri.parse(uriStr)
                val doc = DocumentFile.fromTreeUri(context, uri)
                if (doc != null && doc.canRead()) {
                    rootsArray.put(JSONObject().apply {
                        put("rootId", uriStr)
                        put("displayName", doc.name ?: "Unknown Folder")
                        put("isWritable", doc.canWrite())
                        put("canRename", doc.canWrite())
                        put("canMove", doc.canWrite())
                        put("canDelete", doc.canWrite())
                        put("canRemoveRoot", false)
                    })
                }
            }
            val response = JSONObject().apply {
                put("type", "file_roots_response")
                put("fileRootsResponse", JSONObject().apply {
                    put("roots", rootsArray)
                })
            }
            RemexCoreClient.SendMessage(response.toString())
        } catch (e: Exception) {
            Log.e(TAG, "Failed to list roots", e)
            RemexCoreClient.SendMessage(JSONObject().apply {
                put("type", "file_roots_response")
                put("fileRootsResponse", JSONObject().apply {
                    put("roots", JSONArray())
                    put("errorMessage", e.message ?: "Failed to list Android folders")
                })
            }.toString())
        }
    }

    private fun resolveDocument(rootId: String, relativePath: String): DocumentFile? {
        val rootUri = Uri.parse(rootId)
        var currentDoc = DocumentFile.fromTreeUri(context, rootUri)
        
        if (currentDoc == null || !currentDoc.canRead()) return null

        if (relativePath.isNotEmpty()) {
            val parts = relativePath.split('/')
            for (part in parts) {
                currentDoc = currentDoc?.findFile(part)
                if (currentDoc == null) return null
            }
        }
        return currentDoc
    }

    private fun handleBrowseRequest(req: JSONObject) {
        val requestId = req.optString("requestId")
        val rootId = req.optString("rootId")
        val relativePath = req.optString("relativePath", "").trim('/')
        
        try {
            val currentDoc = resolveDocument(rootId, relativePath)
            if (currentDoc == null) {
                sendBrowseError(requestId, rootId, relativePath, "Path not found or access denied.")
                return
            }

            val entriesArray = JSONArray()
            currentDoc.listFiles().forEach { file ->
                entriesArray.put(JSONObject().apply {
                    put("name", file.name ?: "Unnamed")
                    put("isDirectory", file.isDirectory)
                    put("sizeBytes", if (file.isDirectory) 0L else file.length())
                    put("modifiedUnixMs", file.lastModified())
                })
            }

            RemexCoreClient.SendMessage(JSONObject().apply {
                put("type", "file_browse_response")
                put("fileBrowseResponse", JSONObject().apply {
                    put("requestId", requestId)
                    put("rootId", rootId)
                    put("relativePath", relativePath)
                    put("entries", entriesArray)
                })
            }.toString())
        } catch (e: Exception) {
            Log.e(TAG, "Browse error for path \$relativePath", e)
            sendBrowseError(requestId, rootId, relativePath, e.message ?: "Browse failed")
        }
    }

    private fun sendBrowseError(requestId: String, rootId: String, relativePath: String, error: String) {
        RemexCoreClient.SendMessage(JSONObject().apply {
            put("type", "file_browse_response")
            put("fileBrowseResponse", JSONObject().apply {
                put("requestId", requestId)
                put("rootId", rootId)
                put("relativePath", relativePath)
                put("entries", JSONArray())
                put("errorMessage", error)
            })
        }.toString())
    }

    private fun sendTransferEnd(transferId: String, success: Boolean, error: String? = null, hash: String? = null) {
        RemexCoreClient.SendMessage(JSONObject().apply {
            put("type", "file_transfer_end")
            put("fileTransferEnd", JSONObject().apply {
                put("transferId", transferId)
                put("success", success)
                if (error != null) put("errorMessage", error)
                if (hash != null) put("sha256", hash)
            })
        }.toString())
    }

    private fun handleTransferStart(req: JSONObject) {
        val transferId = req.optString("transferId")
        val direction = req.optString("direction") // "upload" means Client is uploading to us, "download" means Client downloading from us
        val rootId = req.optString("remoteRootId")
        val relativePath = req.optString("remoteRelativePath")
        val fileName = req.optString("fileName")
        val totalBytes = req.optLong("totalBytes", 0)

        try {
            val parentPath = relativePath.substringBeforeLast('/', "")
            val targetName = relativePath.substringAfterLast('/', fileName)
            
            if (direction == "upload") {
                val parentDoc = resolveDocument(rootId, parentPath)
                if (parentDoc == null || !parentDoc.canWrite()) {
                    sendTransferEnd(transferId, false, "Cannot write to destination folder.")
                    return
                }

                // Delete existing if any
                parentDoc.findFile(targetName)?.delete()
                
                val newFile = parentDoc.createFile("*/*", targetName)
                if (newFile == null) {
                    sendTransferEnd(transferId, false, "Failed to create file.")
                    return
                }

                val outStream = context.contentResolver.openOutputStream(newFile.uri, "w")
                if (outStream == null) {
                    sendTransferEnd(transferId, false, "Failed to open output stream.")
                    return
                }

                activeTransfers[transferId] = TransferState(
                    transferId = transferId,
                    direction = direction,
                    outputStream = outStream,
                    totalBytes = totalBytes
                )
            } else {
                // Download
                val fileDoc = resolveDocument(rootId, relativePath)
                if (fileDoc == null || !fileDoc.canRead()) {
                    sendTransferEnd(transferId, false, "File not found or access denied.")
                    return
                }

                val inStream = context.contentResolver.openInputStream(fileDoc.uri)
                if (inStream == null) {
                    sendTransferEnd(transferId, false, "Failed to open input stream.")
                    return
                }

                val state = TransferState(
                    transferId = transferId,
                    direction = direction,
                    inputStream = inStream,
                    totalBytes = fileDoc.length()
                )
                activeTransfers[transferId] = state

                state.job = scope.launch {
                    try {
                        val buffer = ByteArray(CHUNK_SIZE)
                        var read: Int
                        while (inStream.read(buffer).also { read = it } > 0) {
                            state.digest.update(buffer, 0, read)
                            state.bytesTransferred += read
                            state.chunkCount++

                            RemexCoreClient.SendMessage(JSONObject().apply {
                                put("type", "file_transfer_chunk")
                                put("fileTransferChunk", JSONObject().apply {
                                    put("transferId", transferId)
                                    put("offset", state.bytesTransferred - read)
                                    put("dataBase64", Base64.encodeToString(buffer, 0, read, Base64.NO_WRAP))
                                })
                            }.toString())

                            if (state.chunkCount % PROGRESS_CHUNK_INTERVAL == 0) {
                                RemexCoreClient.SendMessage(JSONObject().apply {
                                    put("type", "file_transfer_progress")
                                    put("fileTransferProgress", JSONObject().apply {
                                        put("transferId", transferId)
                                        put("bytesTransferred", state.bytesTransferred)
                                        put("totalBytes", state.totalBytes)
                                    })
                                }.toString())
                            }
                        }

                        val hash = Base64.encodeToString(state.digest.digest(), Base64.NO_WRAP)
                        sendTransferEnd(transferId, true, hash = hash)
                    } catch (e: CancellationException) {
                        Log.i(TAG, "Download cancelled: \$transferId")
                    } catch (e: Exception) {
                        Log.e(TAG, "Download stream failed", e)
                        sendTransferEnd(transferId, false, e.message)
                    } finally {
                        state.cleanup()
                        activeTransfers.remove(transferId)
                    }
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Start transfer failed", e)
            sendTransferEnd(transferId, false, e.message)
        }
    }

    private fun handleTransferChunk(req: JSONObject) {
        val transferId = req.optString("transferId")
        val dataBase64 = req.optString("dataBase64")
        val state = activeTransfers[transferId] ?: return

        try {
            val data = Base64.decode(dataBase64, Base64.DEFAULT)
            
            if (state.direction == "upload" && state.bytesTransferred + data.size > MAX_UPLOAD_BYTES) {
                state.cleanup()
                activeTransfers.remove(transferId)
                sendTransferEnd(transferId, false, "Upload exceeded the max size.")
                return
            }

            state.outputStream?.write(data)
            state.digest.update(data)
            state.bytesTransferred += data.size
            state.chunkCount++

            if (state.chunkCount % PROGRESS_CHUNK_INTERVAL == 0) {
                RemexCoreClient.SendMessage(JSONObject().apply {
                    put("type", "file_transfer_progress")
                    put("fileTransferProgress", JSONObject().apply {
                        put("transferId", transferId)
                        put("bytesTransferred", state.bytesTransferred)
                        put("totalBytes", state.totalBytes)
                    })
                }.toString())
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed writing chunk", e)
            state.cleanup()
            activeTransfers.remove(transferId)
            sendTransferEnd(transferId, false, e.message)
        }
    }

    private fun handleTransferEnd(req: JSONObject) {
        val transferId = req.optString("transferId")
        val success = req.optBoolean("success", false)
        val expectedHash = req.optString("sha256")
        val state = activeTransfers.remove(transferId) ?: return

        try {
            state.cleanup()
            if (state.direction == "upload" && success) {
                val actualHash = Base64.encodeToString(state.digest.digest(), Base64.NO_WRAP)
                if (expectedHash.isNotEmpty() && actualHash != expectedHash) {
                    sendTransferEnd(transferId, false, "Hash mismatch. Expected \$expectedHash, got \$actualHash.")
                    return
                }
                sendTransferEnd(transferId, true, hash = actualHash)
            }
        } catch (e: Exception) {
            Log.e(TAG, "End transfer failed", e)
            sendTransferEnd(transferId, false, e.message)
        }
    }

    private fun handleTransferCancel(req: JSONObject) {
        val transferId = req.optString("transferId")
        activeTransfers.remove(transferId)?.cleanup()
    }

    private fun handleManageRequest(req: JSONObject) {
        val requestId = req.optString("requestId")
        val operation = req.optString("operation") // "rename", "delete"
        val rootId = req.optString("rootId")
        val relativePath = req.optString("relativePath")
        val newName = req.optString("newName")

        try {
            val doc = resolveDocument(rootId, relativePath)
            if (doc == null || !doc.canWrite()) {
                sendManageResponse(requestId, false, "File not found or access denied.")
                return
            }

            when (operation) {
                "rename" -> {
                    if (doc.renameTo(newName)) {
                        sendManageResponse(requestId, true)
                    } else {
                        sendManageResponse(requestId, false, "Rename failed.")
                    }
                }
                "delete" -> {
                    if (doc.delete()) {
                        sendManageResponse(requestId, true)
                    } else {
                        sendManageResponse(requestId, false, "Delete failed.")
                    }
                }
                else -> sendManageResponse(requestId, false, "Unknown operation.")
            }
        } catch (e: Exception) {
            Log.e(TAG, "Manage request failed", e)
            sendManageResponse(requestId, false, e.message)
        }
    }

    private fun sendManageResponse(requestId: String, success: Boolean, error: String? = null) {
        RemexCoreClient.SendMessage(JSONObject().apply {
            put("type", "file_manage_response")
            put("fileManageResponse", JSONObject().apply {
                put("requestId", requestId)
                put("success", success)
                if (error != null) put("errorMessage", error)
            })
        }.toString())
    }
}
