package com.clindsay94.remex.service

import android.content.Context
import android.net.Uri
import android.util.Base64
import android.util.Log
import androidx.documentfile.provider.DocumentFile
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

/**
 * Android host mirror for the PC's file-transfer control plane.
 *
 * Two protocol generations coexist here (plan §1.5):
 *  - **v2 legacy base64** (`file_transfer_start/chunk/end/cancel`) is kept **intact** and served by
 *    the handlers in this object, so a v2 PC keeps working for one release.
 *  - **v3** (roots/browse/manage/copy/move/mkdir, **file_root_manage_request**, volumes, search,
 *    metadata, thumbnail, offer/ready/complete/result/control, consent, and bidirectional binary
 *    frames on `/ws/files`) is delegated to the testable [FileHostHandler].
 *
 * This object owns the Android wiring only: it snapshots the user's shared-folder URIs + full-browse
 * grant, builds the SAF-backed [FileSystemFacade], and routes inbound `/ws` messages to the right
 * generation. Every v3 wire type-string / field name lives in [FileHostHandler] and mirrors
 * `remex.core` VERBATIM.
 */
object AndroidFileTransferHost {
    private const val TAG = "AndroidFileTransferHost"
    private const val CHUNK_SIZE = 65536
    private const val PROGRESS_CHUNK_INTERVAL = 10
    private const val MAX_UPLOAD_BYTES = 5_000_000_000L
    private const val ORPHAN_MAX_AGE_MS = 7L * 24 * 60 * 60 * 1000

    private var job: Job? = null
    private lateinit var settingsManager: SettingsManager
    private lateinit var context: Context
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    // Live snapshots of the served-device configuration, kept fresh by collectors started in start().
    @Volatile private var sharedFolderUris: Set<String> = emptySet()
    @Volatile private var fullBrowseRootUri: String? = null

    private var hostHandler: FileHostHandler? = null

    /**
     * Shared with [hostHandler] so the ids minted by an accepted push offer are the same ids the
     * offer handler will accept. Owned here because consent is obtained here (RemEx-z6lh).
     */
    private val pushConsent = PushConsentRegistry()

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
        // Wire the serving-side consent flow before any inbound request can raise a prompt (plan WP9).
        FileConsentManager.start(context)
        hostHandler = buildHostHandler()

        job?.cancel()
        job = scope.launch {
            RemexClientManager.fileTransferMessages.collect { json ->
                handleMessage(json)
            }
        }
        // Keep the served-device configuration snapshots fresh.
        scope.launch { settingsManager.sharedFolderUrisFlow.collect { sharedFolderUris = it } }
        scope.launch { settingsManager.fullBrowseRootUriFlow.collect { fullBrowseRootUri = it } }
        // Sweep any staging partials orphaned by a prior crash (plan §1.3).
        scope.launch {
            runCatching { hostHandler?.cleanupOrphans(ORPHAN_MAX_AGE_MS) }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
        activeTransfers.values.forEach { it.cleanup() }
        activeTransfers.clear()
    }

    private fun stagingDir(): File = File(context.filesDir, "transfers/incoming")

    private fun buildHostHandler(): FileHostHandler {
        val provider =
            object : SharedRootsProvider {
                override fun sharedRoots(): List<RootDescriptor> =
                    sharedFolderUris.mapNotNull { uriStr ->
                        val doc = DocumentFile.fromTreeUri(context, Uri.parse(uriStr))
                        if (doc != null && doc.canRead()) {
                            RootDescriptor(
                                rootId = uriStr,
                                displayName = doc.name ?: "Shared Folder",
                                isWritable = doc.canWrite(),
                                canRename = doc.canWrite(),
                                canMove = doc.canWrite(),
                                canDelete = doc.canWrite(),
                                canRemoveRoot = false,
                            )
                        } else null
                    }

                override fun fullBrowseVolumes(): List<VolumeDescriptor> {
                    val uri = fullBrowseRootUri ?: return emptyList()
                    val doc = DocumentFile.fromTreeUri(context, Uri.parse(uri)) ?: return emptyList()
                    return listOf(
                        VolumeDescriptor(
                            id = uri,
                            label = doc.name ?: "Device storage",
                            path = uri,
                            totalBytes = 0L,
                            freeBytes = 0L,
                            kind = "root",
                        )
                    )
                }

                override fun isFullBrowseGranted(): Boolean = fullBrowseRootUri != null
            }

        val mutator =
            object : RootMutator {
                override suspend fun removeRoot(rootId: String): Boolean {
                    if (!sharedFolderUris.contains(rootId)) return false
                    settingsManager.removeSharedFolderUri(rootId)
                    return true
                }

                override suspend fun addRoot(sourceRootId: String, sourceRelativePath: String?): Boolean =
                    false // SAF requires an on-device folder pick; handled in WP9's settings UI.
            }

        return FileHostHandler(
            facade = SafFileSystemFacade(context, provider),
            rootsProvider = provider,
            sender = ControlMessageSender { RemexCoreClient.SendMessage(it) },
            channel = FileTransferChannelClient,
            rootMutator = mutator,
            stagingDir = stagingDir(),
            scope = scope,
            pushConsent = pushConsent,
            onPushRefused = ::notifyPushRefused,
        )
    }

    /**
     * Tells the user that a file they agreed to receive is not coming, and why (RemEx-gipu).
     *
     * Before this, every one of these ended in silence: the consent prompt was answered, the transfer
     * was declined on the wire, the reason went into the PC's log, and the phone showed nothing. That
     * is indistinguishable from the app being broken — and it was the exact symptom of RemEx-h1p5,
     * where pushes really were broken, which is why nobody could tell the two apart.
     *
     * The reason code becomes a localized sentence HERE rather than in [FileHostHandler], which is
     * deliberately pure logic with no Context to resolve strings from.
     */
    private fun notifyPushRefused(refusal: PushRefusal) {
        // Deduped. A grant on a device that remembered an earlier answer is issued with no prompt and
        // no tap, so a paired-but-hostile PC can drive the same refusal in a loop with no interaction
        // at all. Repeating one identical notification is noise rather than information; the FIRST is
        // what the user needs, and each distinct reason still gets through.
        if (!reportedRefusals.add(refusal)) return

        val message =
            context.getString(
                when (refusal) {
                    PushRefusal.NoWritableSharedFolder -> R.string.file_push_failed_no_folder
                    PushRefusal.OfferedFileDiffers -> R.string.file_push_failed_wrong_file
                    PushRefusal.UnusableFileName -> R.string.file_push_failed_bad_name
                    PushRefusal.DestinationUnavailable -> R.string.file_push_failed_destination
                    PushRefusal.CouldNotBeSaved -> R.string.file_push_failed_not_saved
                }
            )
        FileTransferNotificationManager.showIncomingPushFailed(context, message)
    }

    /**
     * Refusal reasons already shown, so a repeat does not re-post the same notification.
     *
     * Cleared whenever a push is freshly consented to, so a genuinely new attempt is reported again —
     * the point is to suppress a loop, not to silence the feature after one failure.
     */
    private val reportedRefusals = java.util.Collections.synchronizedSet(mutableSetOf<PushRefusal>())

    /**
     * Ensures the shared binary `/ws/files` socket is open before serving a v3 transfer. The socket is
     * always Android-initiated (cert-pinned via the SPKI captured at pairing time). Returns true once
     * connected.
     */
    private suspend fun ensureBinaryChannel(): Boolean {
        if (FileTransferChannelClient.isOpen) return true
        val host = settingsManager.hostFlow.first()
        val port = settingsManager.portFlow.first()
        if (host.isBlank()) return false
        val clientId = settingsManager.getOrCreateClientId()
        val spki =
            PinnedHostStore.getPin(context, host)?.takeIf { it.isNotBlank() } ?: return false
        return FileTransferChannelClient.ensureConnected(context, host, port, clientId, spki)
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Consent hooks (plan §2 / WP9). The serving-side consent for full-device
    // browse is handled by the settings toggle (a SAF root pick is the OS-level
    // consent, mirrored into the per-device trust key). The live prompt below is
    // for an incoming file push, mirroring the PC's HandleFilePushOfferAsync.
    // ─────────────────────────────────────────────────────────────────────────

    /**
     * Handles an inbound `file_push_offer`: a paired PC offering to push files to this device. Gated by
     * per-device consent — if the PC does not already hold an auto-accept-incoming grant a prompt is
     * raised (notification + foreground dialog) and held until the user decides or the 60s timeout
     * auto-denies. On acceptance a fresh receiver-assigned transfer id is returned per offered file
     * (index-aligned to `files`); the PC then negotiates each file with a
     * `file_transfer_offer(mode="push")` carrying its assigned id. On denial the response is
     * `accepted=false` with no ids. Field names mirror `remex.core` FilePushOffer/FilePushResponse.
     */
    private suspend fun handlePushOffer(offer: JSONObject) {
        val pushId = offer.optString("pushId")
        if (pushId.isBlank()) return
        val filesArr = offer.optJSONArray("files") ?: JSONArray()
        val fileCount = filesArr.length()

        val decision =
            FileConsentManager.requestConsent(
                deviceId = peerDeviceId(),
                kind = FileConsentKinds.INCOMING_PUSH,
                detail = describePushFiles(filesArr),
            )

        val payload =
            JSONObject().apply {
                put("pushId", pushId)
                put("accepted", decision.granted)
                if (decision.granted) {
                    val minted = mintPushGrants(filesArr)
                    // Record BEFORE replying: the PC may negotiate the first file the instant this
                    // response lands, and an offer arriving before the grant would be refused.
                    pushConsent.grant(minted)
                    // A fresh acceptance is a fresh chance to be told what went wrong: without this,
                    // one failure would silence that reason for the rest of the process's life.
                    reportedRefusals.clear()
                    put("transferIds", JSONArray().apply { minted.keys.forEach { put(it) } })
                }
            }
        RemexCoreClient.SendMessage(
            JSONObject().apply {
                put("type", "file_push_response")
                put("protocolVersion", 3)
                put("filePushResponse", payload)
            }.toString()
        )
    }

    /** Stable per-device trust id for the paired PC (its configured host address). */
    private suspend fun peerDeviceId(): String =
        FilePeerIdentity.deviceId(runCatching { settingsManager.hostFlow.first() }.getOrNull())

    /**
     * Human-readable summary of a push offer's files (names, elided by length, plus the total size)
     * for the consent prompt. Mirrors the PC's FileTransferHandler.DescribePushFiles so both prompts
     * read the same.
     */
    private fun describePushFiles(filesArr: JSONArray): String {
        if (filesArr.length() == 0) return ""

        var totalBytes = 0L
        // ONE ENTRY PER SLOT, malformed included — do not `continue` past them. mintPushGrants mints a
        // transfer id for every index, so skipping here would make the prompt's "+N" count fewer files
        // than the grant actually covers: an offer of one real file and ten junk entries would show a
        // single name and no remainder at all, while eleven ids went back. Understating the offer is
        // the exact failure this bead exists to fix.
        val names = ArrayList<String>(filesArr.length())
        for (i in 0 until filesArr.length()) {
            val f = filesArr.optJSONObject(i)
            totalBytes += f?.optLong("size", 0L) ?: 0L
            names.add(f?.optString("name").orEmpty())
        }

        return "${joinOfferedNames(names)} (${formatBytes(totalBytes)})"
    }

    private fun formatBytes(bytes: Long): String =
        when {
            bytes >= 1_073_741_824L -> "%.1f GB".format(bytes / 1_073_741_824.0)
            bytes >= 1_048_576L -> "%.1f MB".format(bytes / 1_048_576.0)
            bytes >= 1_024L -> "%.1f KB".format(bytes / 1_024.0)
            else -> "$bytes B"
        }

    private suspend fun handleMessage(json: String) {
        try {
            val obj = JSONObject(json)
            when (obj.optString("type")) {
                // ── v2 legacy base64 path (kept intact for v2 peers) ──
                "file_transfer_start" -> handleTransferStart(obj.optJSONObject("fileTransferStart") ?: return)
                "file_transfer_chunk" -> handleTransferChunk(obj.optJSONObject("fileTransferChunk") ?: return)
                "file_transfer_end" -> handleTransferEnd(obj.optJSONObject("fileTransferEnd") ?: return)
                "file_transfer_cancel" -> handleTransferCancel(obj.optJSONObject("fileTransferCancel") ?: return)

                // ── v3 path (delegated to the testable host handler) ──
                "file_transfer_offer" -> {
                    // A v3 transfer needs the binary channel; open it before negotiating. Per-file
                    // push offers ride this after the file_push_offer below was already consented, so
                    // no second prompt is raised here (mirrors the PC's file_push_offer → offer flow).
                    ensureBinaryChannel()
                    hostHandler?.handleControlMessage(json)
                }
                // Incoming push from the PC: consent-gated on this (serving) device (plan §2 / WP9).
                // Dispatched off the collector so a 60s prompt cannot head-of-line-block other inbound
                // control messages (browse/manage responses share this stream).
                "file_push_offer" -> {
                    val offer = obj.optJSONObject("filePushOffer")
                    if (offer != null) scope.launch { handlePushOffer(offer) }
                }
                else -> {
                    // roots/browse/manage/root_manage/volumes/search/metadata/thumbnail/
                    // complete/control/result — all v3-capable, superset of the old v2 responses.
                    hostHandler?.handleControlMessage(json)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error handling file transfer message", e)
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // v2 legacy base64 transfer path — UNCHANGED behavior (plan §1.5). Do not
    // remove until 2.2; v2 PCs still stream chunks over /ws with this contract.
    // ─────────────────────────────────────────────────────────────────────────

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
                        Log.i(TAG, "Download cancelled: $transferId")
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
                    sendTransferEnd(transferId, false, "Hash mismatch. Expected $expectedHash, got $actualHash.")
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
}
