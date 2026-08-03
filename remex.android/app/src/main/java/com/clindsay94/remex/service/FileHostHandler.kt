package com.clindsay94.remex.service

import android.util.Base64
import java.io.File
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

/**
 * Which shared folder a pushed file goes into, or null when nothing writable is shared (RemEx-h1p5).
 *
 * Top-level and pure so the choice can be tested without a handler, a socket or a filesystem — it is
 * the whole decision, and everything around it is plumbing.
 *
 * **FIRST WRITABLE, AND THE ORDER IS NOT A GUARANTEE — REVIEW CAUGHT THIS BEING CLAIMED AS ONE.**
 * Today the list arrives in the order folders were shared, but only incidentally: the underlying
 * store is a DataStore `Set<String>` (`SettingsManager.sharedFolderUrisFlow`) with no ordering
 * contract, preserved by `Set.plus` happening to return a LinkedHashSet. It also genuinely moves —
 * `sharedRoots()` drops any root it can no longer read, so unmounting the card holding the first
 * shared folder shifts the destination to the next one with nothing said to anybody.
 *
 * So this is "wherever the provider puts first", which is stable enough to ship and not stable enough
 * to promise. Giving the user an explicit "save incoming files here" choice is RemEx-pwkc's business
 * and would replace this rule outright.
 *
 * Read-only roots are skipped rather than tried and failed: `sharedRoots()` admits a root on
 * `canRead` alone, so a readable-but-unwritable share is a real entry in this list, and offering to
 * receive into one produces a refusal after the user has already agreed to the transfer.
 */
internal fun pushDestinationRoot(roots: List<RootDescriptor>): String? =
    roots.firstOrNull { it.isWritable && it.rootId.isNotBlank() }?.rootId

/** Sends a control-plane JSON envelope to the peer over the native `/ws` socket. */
fun interface ControlMessageSender {
    fun send(json: String)
}

/** Mutates the served device's shared-root set (suspend: backed by DataStore in production). */
interface RootMutator {
    suspend fun removeRoot(rootId: String): Boolean

    suspend fun addRoot(sourceRootId: String, sourceRelativePath: String?): Boolean
}

/**
 * Testable core of the Android host mirror (plan WP6). Handles every PC-initiated file op against the
 * served device's SAF roots — browse, roots, manage (delete/rename/copy/move/mkdir),
 * **file_root_manage_request** (previously unhandled — the parity gap), volumes, search, metadata,
 * thumbnail, consent — plus the v3 transfer negotiation and bidirectional binary frames on the shared
 * `/ws/files` socket.
 *
 * Every JSON type-string and field name mirrors `remex.core` (RemexMessage / FileTransferMessages)
 * VERBATIM. This class is pure logic over injected seams ([FileSystemFacade], [ControlMessageSender],
 * [FileFrameChannel], [RootMutator]) so it can be unit-tested against fakes; the Android wiring lives
 * in [AndroidFileTransferHost].
 */
class FileHostHandler(
    private val facade: FileSystemFacade,
    private val rootsProvider: SharedRootsProvider,
    private val sender: ControlMessageSender,
    private val channel: FileFrameChannel,
    private val rootMutator: RootMutator,
    private val stagingDir: File,
    private val scope: CoroutineScope,
    /**
     * Which PUSH transfer ids the user actually consented to. Defaulted so existing call sites and
     * tests that never exercise push keep working; AndroidFileTransferHost passes the shared one.
     */
    private val pushConsent: PushConsentRegistry = PushConsentRegistry(),
) {
    private val receiveSessions = ConcurrentHashMap<String, HostReceiveSession>()
    private val sendSessions = ConcurrentHashMap<String, HostSendSession>()

    /** Dispatches one inbound control-plane message. Returns true if this handler consumed it. */
    suspend fun handleControlMessage(json: String): Boolean {
        val obj =
            try {
                JSONObject(json)
            } catch (e: Exception) {
                return false
            }
        return when (obj.optString("type")) {
            "file_roots_request" -> { handleRootsRequest(); true }
            "file_browse_request" -> { handleBrowse(obj.optJSONObject("fileBrowseRequest")); true }
            "file_manage_request" -> { handleManage(obj.optJSONObject("fileManageRequest")); true }
            "file_root_manage_request" -> {
                handleRootManage(obj.optJSONObject("fileRootManageRequest")); true
            }
            "file_volumes_request" -> { handleVolumes(obj.optJSONObject("fileVolumesRequest")); true }
            "file_search_request" -> { handleSearch(obj.optJSONObject("fileSearchRequest")); true }
            "file_metadata_request" -> { handleMetadata(obj.optJSONObject("fileMetadataRequest")); true }
            "file_thumbnail_request" -> { handleThumbnail(obj.optJSONObject("fileThumbnailRequest")); true }
            "file_transfer_offer" -> { handleOffer(obj.optJSONObject("fileTransferOffer")); true }
            "file_transfer_complete" -> { handleComplete(obj.optJSONObject("fileTransferComplete")); true }
            "file_transfer_control" -> { handleControl(obj.optJSONObject("fileTransferControl")); true }
            "file_transfer_result" -> { handleResult(obj.optJSONObject("fileTransferResult")); true }
            else -> false
        }
    }

    // ── Roots / browse (v2 + capabilities) ────────────────────────────────────

    private fun handleRootsRequest() {
        val roots = facade.listRoots()
        val rootsArr = JSONArray()
        roots.forEach { rootsArr.put(rootJson(it)) }
        val payload =
            JSONObject().apply {
                put("roots", rootsArr)
                // Additive capability object — v2 peers ignore it (optJSONObject); v3 peers read it to
                // learn this device speaks the binary protocol. The envelope itself stays at the default
                // (v2) version because file_roots_response is answered to v2 peers too (plan §1.5), and a
                // v3 message must never be sent to a v2 peer.
                put("fileCapabilities", capabilitiesJson())
            }
        sender.send(envelope("file_roots_response", "fileRootsResponse", payload, v3 = false))
    }

    private fun capabilitiesJson(): JSONObject =
        JSONObject().apply {
            put("protocol", 3)
            put("binary", true)
            put("resume", true)
            put(
                "ops",
                JSONArray().apply {
                    put(FileManageOperations.DELETE)
                    put(FileManageOperations.RENAME)
                    put(FileManageOperations.COPY)
                    put(FileManageOperations.MOVE)
                    put(FileManageOperations.MKDIR)
                    put("search")
                },
            )
            put("fullBrowse", rootsProvider.isFullBrowseGranted())
            put("push", true)
        }

    private fun handleBrowse(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val rootId = req.optString("rootId")
        val relativePath = req.optString("relativePath", "").trim('/')
        val node = facade.resolve(rootId, relativePath)
        if (node == null || !node.canRead) {
            sendBrowseError(requestId, rootId, relativePath, "Path not found or access denied.")
            return
        }
        val entries = JSONArray()
        node.listChildren().forEach { child ->
            entries.put(
                JSONObject().apply {
                    put("name", child.name)
                    put("isDirectory", child.isDirectory)
                    put("sizeBytes", child.length)
                    put("modifiedUnixMs", child.lastModifiedMs)
                }
            )
        }
        val payload =
            JSONObject().apply {
                put("requestId", requestId)
                put("rootId", rootId)
                put("relativePath", relativePath)
                put("entries", entries)
            }
        sender.send(envelope("file_browse_response", "fileBrowseResponse", payload, v3 = false))
    }

    private fun sendBrowseError(requestId: String, rootId: String, relativePath: String, error: String) {
        val payload =
            JSONObject().apply {
                put("requestId", requestId)
                put("rootId", rootId)
                put("relativePath", relativePath)
                put("entries", JSONArray())
                put("errorMessage", error)
            }
        sender.send(envelope("file_browse_response", "fileBrowseResponse", payload, v3 = false))
    }

    // ── Manage: delete / rename / copy / move / mkdir ─────────────────────────

    private fun handleManage(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val rootId = req.optString("rootId")
        val relativePath = req.optString("relativePath")
        val operation = req.optString("operation")
        val newName = req.optString("newName")
        val destinationPath = if (req.has("destinationPath")) req.optString("destinationPath") else null
        val overwrite = req.optBoolean("overwrite", false)

        try {
            when (operation) {
                FileManageOperations.DELETE -> {
                    val node = facade.resolve(rootId, relativePath)
                    if (node == null || !node.canWrite) {
                        sendManage(requestId, false, "File not found or access denied.")
                    } else {
                        val ok = node.delete()
                        sendManage(requestId, ok, if (ok) null else "Delete failed.")
                    }
                }
                FileManageOperations.RENAME -> {
                    val node = facade.resolve(rootId, relativePath)
                    when {
                        node == null || !node.canWrite ->
                            sendManage(requestId, false, "File not found or access denied.")
                        newName.isBlank() -> sendManage(requestId, false, "A new name is required.")
                        node.renameTo(newName) -> sendManage(requestId, true, null)
                        else -> sendManage(requestId, false, "Rename failed.")
                    }
                }
                FileManageOperations.MKDIR -> {
                    val parent = facade.resolve(rootId, relativePath)
                    when {
                        parent == null || !parent.canWrite ->
                            sendManage(requestId, false, "Destination folder not found or read-only.")
                        newName.isBlank() -> sendManage(requestId, false, "A folder name is required.")
                        parent.findChild(newName) != null && !overwrite ->
                            sendManage(requestId, false, "A folder with that name already exists.")
                        parent.createDirectory(newName) != null -> sendManage(requestId, true, null)
                        else -> sendManage(requestId, false, "Could not create folder.")
                    }
                }
                FileManageOperations.COPY, FileManageOperations.MOVE -> {
                    handleCopyOrMove(requestId, rootId, relativePath, destinationPath, overwrite, isMove = operation == FileManageOperations.MOVE)
                }
                else -> sendManage(requestId, false, "Unknown operation.")
            }
        } catch (e: Exception) {
            sendManage(requestId, false, e.message ?: "Operation failed.")
        }
    }

    private fun handleCopyOrMove(
        requestId: String,
        rootId: String,
        relativePath: String,
        destinationPath: String?,
        overwrite: Boolean,
        isMove: Boolean,
    ) {
        if (destinationPath.isNullOrBlank()) {
            sendManage(requestId, false, "A destination path is required.")
            return
        }
        val source = facade.resolve(rootId, relativePath)
        if (source == null || !source.canRead) {
            sendManage(requestId, false, "Source not found or access denied.")
            return
        }
        if (source.isDirectory) {
            sendManage(requestId, false, "Copying folders is not supported.")
            return
        }
        val destDir = destinationPath.trim('/').substringBeforeLast('/', "")
        val destName = destinationPath.trim('/').substringAfterLast('/')
        if (destName.isBlank()) {
            sendManage(requestId, false, "A destination file name is required.")
            return
        }
        val parent = facade.resolve(rootId, destDir)
        if (parent == null || !parent.canWrite) {
            sendManage(requestId, false, "Destination folder not found or read-only.")
            return
        }
        val existing = parent.findChild(destName)
        if (existing != null) {
            if (!overwrite) {
                sendManage(requestId, false, "A file with that name already exists.")
                return
            }
            existing.delete()
        }
        val mime = source.mimeType ?: "application/octet-stream"
        val target = parent.createFile(mime, destName)
        if (target == null) {
            sendManage(requestId, false, "Could not create the destination file.")
            return
        }
        val copied =
            try {
                facade.openInput(source)?.use { input ->
                    facade.openOutput(target)?.use { output ->
                        input.copyTo(output, 65536)
                        true
                    } ?: false
                } ?: false
            } catch (e: Exception) {
                false
            }
        if (!copied) {
            target.delete()
            sendManage(requestId, false, "Copy failed.")
            return
        }
        if (isMove) source.delete()
        sendManage(requestId, true, null)
    }

    private fun sendManage(requestId: String, success: Boolean, error: String?) {
        val payload =
            JSONObject().apply {
                put("requestId", requestId)
                put("success", success)
                if (error != null) put("errorMessage", error)
            }
        sender.send(envelope("file_manage_response", "fileManageResponse", payload, v3 = false))
    }

    // ── Root management (parity fix: previously unhandled → protocol hang) ─────

    private suspend fun handleRootManage(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val operation = req.optString("operation")
        var error: String? = null
        when (operation) {
            "remove" -> {
                val rootId = req.optString("rootId")
                if (rootId.isBlank() || !rootMutator.removeRoot(rootId)) {
                    error = "Could not remove that shared folder."
                }
            }
            "add" -> {
                // Adding a shared root on Android requires an on-device SAF folder pick; it cannot be
                // performed remotely. Respond with the current roots and an explanatory message rather
                // than leaving the request unanswered (the parity gap this fixes).
                error = "Adding a shared folder must be done on the phone."
            }
            else -> error = "Unknown root operation."
        }
        val roots = JSONArray()
        facade.listRoots().forEach { roots.put(rootJson(it)) }
        val payload =
            JSONObject().apply {
                put("requestId", requestId)
                put("roots", roots)
                if (error != null) put("errorMessage", error)
            }
        sender.send(envelope("file_root_manage_response", "fileRootManageResponse", payload, v3 = false))
    }

    // ── Volumes / search / metadata / thumbnail ───────────────────────────────

    private fun handleVolumes(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val granted = rootsProvider.isFullBrowseGranted()
        val volumes = JSONArray()
        if (granted) {
            facade.listVolumes().forEach { v ->
                volumes.put(
                    JSONObject().apply {
                        put("id", v.id)
                        put("label", v.label)
                        put("path", v.path)
                        put("totalBytes", v.totalBytes)
                        put("freeBytes", v.freeBytes)
                        put("kind", v.kind)
                    }
                )
            }
        }
        val payload =
            JSONObject().apply {
                put("requestId", requestId)
                put("volumes", volumes)
                put("fullBrowseGranted", granted)
            }
        sender.send(envelope("file_volumes_response", "fileVolumesResponse", payload, v3 = true))
    }

    private fun handleSearch(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val rootId = req.optString("rootId")
        val relativePath = if (req.has("relativePath")) req.optString("relativePath").trim('/') else ""
        val query = req.optString("query").lowercase()
        val requestedCap = req.optInt("maxResults", FileTransferLimits.SEARCH_MAX_RESULTS)
        val cap = requestedCap.coerceIn(1, FileTransferLimits.SEARCH_MAX_RESULTS)

        val entries = JSONArray()
        var truncated = false
        val start = facade.resolve(rootId, relativePath)
        if (start == null || !start.canRead || query.isBlank()) {
            sendSearch(requestId, entries, false, if (query.isBlank()) "A search term is required." else "Path not found.")
            return
        }
        // Iterative BFS so a deep tree cannot blow the stack; stop as soon as the cap is exceeded.
        val queue = ArrayDeque<Pair<FileNode, String>>()
        queue.add(start to relativePath)
        var visited = 0
        while (queue.isNotEmpty()) {
            val (node, prefix) = queue.removeFirst()
            if (++visited > 20_000) { truncated = true; break } // hard traversal ceiling
            val children =
                try {
                    node.listChildren()
                } catch (e: Exception) {
                    continue
                }
            for (child in children) {
                val childRel = if (prefix.isEmpty()) child.name else "$prefix/${child.name}"
                if (child.name.lowercase().contains(query)) {
                    if (entries.length() >= cap) {
                        truncated = true
                        break
                    }
                    entries.put(
                        JSONObject().apply {
                            put("name", child.name)
                            put("relativePath", childRel)
                            put("isDirectory", child.isDirectory)
                            put("sizeBytes", child.length)
                            put("modifiedUnixMs", child.lastModifiedMs)
                        }
                    )
                }
                if (child.isDirectory) queue.add(child to childRel)
            }
            if (truncated) break
        }
        sendSearch(requestId, entries, truncated, null)
    }

    private fun sendSearch(requestId: String, entries: JSONArray, truncated: Boolean, error: String?) {
        val payload =
            JSONObject().apply {
                put("requestId", requestId)
                put("entries", entries)
                put("truncated", truncated)
                if (error != null) put("errorMessage", error)
            }
        sender.send(envelope("file_search_response", "fileSearchResponse", payload, v3 = true))
    }

    private fun handleMetadata(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val rootId = req.optString("rootId")
        val relativePath = req.optString("relativePath")
        val node = facade.resolve(rootId, relativePath)
        val payload = JSONObject().apply { put("requestId", requestId) }
        if (node == null || !node.canRead) {
            payload.put("size", 0)
            payload.put("createdUtc", 0)
            payload.put("modifiedUtc", 0)
            payload.put("isDirectory", false)
            payload.put("readOnly", true)
            payload.put("errorMessage", "Not found or access denied.")
        } else {
            payload.put("size", node.length)
            // SAF exposes only last-modified; use it for both created and modified.
            payload.put("createdUtc", node.lastModifiedMs)
            payload.put("modifiedUtc", node.lastModifiedMs)
            payload.put("isDirectory", node.isDirectory)
            if (node.isDirectory) {
                payload.put("itemCount", runCatching { node.listChildren().size }.getOrDefault(0))
            }
            node.mimeType?.let { payload.put("mimeType", it) }
            payload.put("readOnly", !node.canWrite)
        }
        sender.send(envelope("file_metadata_response", "fileMetadataResponse", payload, v3 = true))
    }

    private fun handleThumbnail(req: JSONObject?) {
        req ?: return
        val requestId = req.optString("requestId")
        val rootId = req.optString("rootId")
        val relativePath = req.optString("relativePath")
        val maxDim = req.optInt("maxDim", FileTransferLimits.THUMBNAIL_DEFAULT_MAX_DIM)
        val payload = JSONObject().apply { put("requestId", requestId) }
        val node = facade.resolve(rootId, relativePath)
        if (node == null || !node.canRead || node.isDirectory) {
            payload.put("errorMessage", "Not found or not an image/video.")
        } else {
            val jpeg =
                try {
                    facade.loadThumbnailJpeg(node, maxDim, FileTransferLimits.THUMBNAIL_MAX_BYTES)
                } catch (e: Exception) {
                    null
                }
            if (jpeg != null) {
                payload.put("jpegBase64", Base64.encodeToString(jpeg, Base64.NO_WRAP))
            } else {
                payload.put("errorMessage", "No thumbnail available.")
            }
        }
        sender.send(envelope("file_thumbnail_response", "fileThumbnailResponse", payload, v3 = true))
    }

    // ── v3 binary transfer (Android served: PC downloads-from / uploads-to Android) ──

    private fun handleOffer(req: JSONObject?) {
        req ?: return
        val transferId = req.optString("transferId")
        val mode = req.optString("mode")
        val destRoot = if (req.has("destRoot")) req.optString("destRoot") else null
        val destRelativePath = if (req.has("destRelativePath")) req.optString("destRelativePath") else null
        val fileName = req.optString("fileName")
        val size = req.optLong("size", 0L)
        val resumeRequested = req.optBoolean("resumeRequested", false)

        if (transferId.isBlank()) return
        when (mode) {
            FileTransferModes.DOWNLOAD -> beginHostSend(transferId, destRoot, destRelativePath, fileName)
            FileTransferModes.UPLOAD ->
                beginHostReceive(transferId, destRoot, destRelativePath, fileName, size, resumeRequested)

            FileTransferModes.PUSH -> {
                // A push is only allowed under an id this device minted after the user accepted a
                // file_push_offer. Without this the consent prompt was merely conventional: a paired
                // PC could invent an id, skip the offer, and land files in any writable shared root
                // with no prompt shown (RemEx-z6lh).
                //
                // Checked HERE rather than in beginHostReceive because UPLOAD shares that function
                // and must not be gated - an upload targets a folder the user already shared for
                // writing, so the share is the consent.
                // Checked against the FILE NAME too, not the id alone (RemEx-tutz). An id proves some
                // prompt was answered; only the name proves it was answered about this file. Without
                // it, an id minted for one file could be negotiated carrying another and accepted with
                // no second prompt.
                //
                // No release on this path, unlike the declines inside beginHostReceive: the id may be
                // perfectly valid and merely mis-addressed, and throwing away a grant on the strength
                // of a message that failed its own check would let a bad offer cancel a good one.
                if (!pushConsent.isGrantedFor(transferId, fileName)) {
                    sendReady(transferId, false, 0, "This push was not accepted on the device.")
                    return
                }

                // **THE PHONE PICKS WHERE A PUSHED FILE LANDS, NOT THE PC (RemEx-h1p5).** The PC sends
                // no destRoot at all — it has no way to know what this device has shared, and no
                // business choosing a folder on somebody else's phone. beginHostReceive requires one
                // and refused every push outright, so the consent-gated push has never delivered a
                // byte: the user tapped Allow, the PC got its transfer ids, and each file was then
                // declined with "A destination shared root is required."
                //
                // Resolving it from this device's own shared folders is the same reasoning the UPLOAD
                // branch above already relies on — a folder shared for writing IS the consent to write
                // there — with the push prompt on top of it, not instead of it.
                val pushRoot = pushDestinationRoot(facade.listRoots())
                if (pushRoot == null) {
                    // Release the id: this transfer is over, and a grant left behind would sit in the
                    // registry authorising a push that can never arrive.
                    pushConsent.release(transferId)
                    sendReady(
                        transferId,
                        false,
                        0,
                        "No folder on this device is shared for writing, so there is nowhere to save " +
                            "the file. Share a folder in RemEx first.",
                    )
                    return
                }

                // Null relative path: pushed files go to the top of the chosen folder. The PC named no
                // subfolder and inventing one would put the file somewhere the user did not expect.
                // It is also what keeps the fileName guard below sufficient — with no relative path
                // there is no path assembly for a crafted name to escape through.
                beginHostReceive(
                    transferId,
                    pushRoot,
                    null,
                    fileName,
                    size,
                    resumeRequested,
                    replaceExisting = false,
                )
            }
            else -> sendReady(transferId, false, 0, "Unsupported transfer mode.")
        }
    }

    /** PC uploads/pushes to Android: Android receives, staging + resume, then commits to SAF on complete. */
    private fun beginHostReceive(
        transferId: String,
        destRoot: String?,
        destRelativePath: String?,
        fileName: String,
        size: Long,
        resumeRequested: Boolean,
        /** True for UPLOAD (replace on name collision), false for PUSH (let SAF uniquify). */
        replaceExisting: Boolean = true,
    ) {
        // Every decline below hands a PUSH grant back, and the reason is narrower than it was when
        // this was written. A grant is now bound to the file name it was minted for (RemEx-tutz), so
        // a leftover one can only ever re-deliver the very file the user approved — it is no longer a
        // blank cheque a hostile PC could spend on something else. What is left is simply that these
        // declines end the transfer: the destination is unusable or the name is unfilable, and a grant
        // for a transfer that can never happen is the wrong thing to keep sitting in the registry.
        //
        // Contrast the name-mismatch check in the PUSH branch, which deliberately does NOT release:
        // a decline here is evidence about the TRANSFER, while a mismatch is evidence about one
        // MESSAGE, and the grant behind it may be perfectly good. (RemEx-h1p5, RemEx-tutz.)
        fun decline(reason: String) {
            if (!replaceExisting) pushConsent.release(transferId)
            sendReady(transferId, false, 0, reason)
        }

        if (destRoot.isNullOrBlank()) {
            decline("A destination shared root is required.")
            return
        }
        if (fileName.isBlank() || fileName.contains('/') || fileName.contains('\\')) {
            decline("Invalid file name.")
            return
        }
        val parent = facade.resolve(destRoot, (destRelativePath ?: "").trim('/'))
        if (parent == null || !parent.canWrite) {
            decline("Destination folder not found or read-only.")
            return
        }

        stagingDir.mkdirs()
        val partial = File(stagingDir, safeStem(transferId) + ".remexpart")
        val manifest = File(stagingDir, safeStem(transferId) + ".manifest.json")

        var startOffset = 0L
        val digest = MessageDigest.getInstance("SHA-256")
        if (resumeRequested && partial.exists() && manifestMatches(manifest, transferId, size, fileName, destRoot)) {
            val len = partial.length()
            if (len in 0..size) {
                // Re-hash the surviving partial (incremental hash state can't be serialized — plan §1.3).
                partial.inputStream().use { input ->
                    val buf = ByteArray(65536)
                    while (true) {
                        val r = input.read(buf)
                        if (r <= 0) break
                        digest.update(buf, 0, r)
                    }
                }
                startOffset = len
            } else {
                partial.delete()
            }
        } else {
            partial.delete()
            writeManifest(manifest, transferId, size, fileName, destRoot, destRelativePath)
        }
        if (startOffset == 0L) writeManifest(manifest, transferId, size, fileName, destRoot, destRelativePath)

        val session =
            HostReceiveSession(
                transferId = transferId,
                destRoot = destRoot,
                destRelativePath = destRelativePath,
                fileName = fileName,
                expectedSize = size,
                partial = partial,
                manifest = manifest,
                digest = digest,
                bytesReceived = startOffset,
                lastAcked = startOffset,
                replaceExisting = replaceExisting,
            )
        receiveSessions[transferId]?.close()
        receiveSessions[transferId] = session
        channel.registerSink(transferId, session)
        sendReady(transferId, true, startOffset, null)
    }

    /** PC downloads from Android: Android streams data frames out with ack-driven backpressure. */
    private fun beginHostSend(
        transferId: String,
        srcRoot: String?,
        srcRelativePath: String?,
        fileName: String,
    ) {
        if (srcRoot.isNullOrBlank() || fileName.isBlank()) {
            sendReady(transferId, false, 0, "A source file is required.")
            return
        }
        val rel = combineRelative(srcRelativePath, fileName)
        val node = facade.resolve(srcRoot, rel)
        if (node == null || !node.canRead || node.isDirectory) {
            sendReady(transferId, false, 0, "Source file not found or access denied.")
            return
        }
        val size = node.length
        sendReady(transferId, true, 0, null)

        val session = HostSendSession(transferId)
        sendSessions[transferId] = session
        channel.registerSink(transferId, session)

        session.job =
            scope.launch {
                val digest = MessageDigest.getInstance("SHA-256")
                var sent = 0L
                try {
                    val input = facade.openInput(node) ?: throw IllegalStateException("Cannot open source.")
                    input.use {
                        val buf = ByteArray(FileTransferLimits.DATA_PAYLOAD_BYTES)
                        while (true) {
                            val read = it.read(buf)
                            if (read <= 0) break
                            // Backpressure: never exceed the unacked cap.
                            while (sent - session.committedOffset > FileTransferLimits.MAX_UNACKED_BYTES) {
                                session.ackSignal.receive()
                            }
                            val isFinal = sent + read >= size
                            if (!FileTransferChannelClient.sendData(transferId, sent, buf, read, isFinal)) {
                                throw IllegalStateException("Binary channel closed.")
                            }
                            digest.update(buf, 0, read)
                            sent += read
                        }
                    }
                    val sha = Base64.encodeToString(digest.digest(), Base64.NO_WRAP)
                    sendComplete(transferId, sha)
                } catch (e: Exception) {
                    channel.sendFrame(
                        FileFrameEnvelope(FileFrameKinds.ERROR, transferId, error = e.message ?: "send failed"),
                        ByteArray(0),
                    )
                } finally {
                    channel.unregisterSink(transferId)
                    sendSessions.remove(transferId)
                }
            }
    }

    private fun handleComplete(req: JSONObject?) {
        req ?: return
        val transferId = req.optString("transferId")
        val expectedSha = if (req.has("sha256")) req.optString("sha256") else null
        // The push grant dies with its transfer, so a completed id cannot be replayed as a fresh
        // push later. Harmless for UPLOAD ids, which were never in the registry (RemEx-z6lh).
        pushConsent.release(transferId)
        val session = receiveSessions.remove(transferId) ?: return
        channel.unregisterSink(transferId)
        scope.launch {
            val result = session.finalizeAndCommit(expectedSha, facade)
            sendResult(transferId, result.verified, result.sha256, result.error)
        }
    }

    private fun handleControl(req: JSONObject?) {
        req ?: return
        val transferId = req.optString("transferId")
        when (req.optString("action")) {
            FileTransferControlActions.CANCEL, FileTransferControlActions.PAUSE -> {
                pushConsent.release(transferId)
                receiveSessions.remove(transferId)?.let {
                    channel.unregisterSink(transferId)
                    // Cancel deletes the partial; pause keeps it. Both stop the live session.
                    if (req.optString("action") == FileTransferControlActions.CANCEL) it.deleteStaging()
                    it.close()
                }
                sendSessions.remove(transferId)?.let {
                    channel.unregisterSink(transferId)
                    it.job?.cancel()
                }
            }
            FileTransferControlActions.RESUME -> { /* driven by a fresh offer with resumeRequested */ }
        }
    }

    private fun handleResult(req: JSONObject?) {
        req ?: return
        val transferId = req.optString("transferId")
        sendSessions.remove(transferId)?.let {
            channel.unregisterSink(transferId)
            it.job?.cancel()
        }
    }

    // ── Response builders (envelope + payload) ────────────────────────────────

    private fun sendReady(transferId: String, accepted: Boolean, startOffset: Long, declineReason: String?) {
        val payload =
            JSONObject().apply {
                put("transferId", transferId)
                put("accepted", accepted)
                put("startOffset", startOffset)
                if (declineReason != null) put("declineReason", declineReason)
            }
        sender.send(envelope("file_transfer_ready", "fileTransferReady", payload, v3 = true))
    }

    private fun sendComplete(transferId: String, sha256: String) {
        val payload =
            JSONObject().apply {
                put("transferId", transferId)
                put("sha256", sha256)
            }
        sender.send(envelope("file_transfer_complete", "fileTransferComplete", payload, v3 = true))
    }

    private fun sendResult(transferId: String, verified: Boolean, sha256: String?, error: String?) {
        val payload =
            JSONObject().apply {
                put("transferId", transferId)
                put("verified", verified)
                if (sha256 != null) put("sha256", sha256)
                if (error != null) put("error", error)
            }
        sender.send(envelope("file_transfer_result", "fileTransferResult", payload, v3 = true))
    }

    private fun rootJson(r: RootDescriptor): JSONObject =
        JSONObject().apply {
            put("rootId", r.rootId)
            put("displayName", r.displayName)
            put("isWritable", r.isWritable)
            put("canRename", r.canRename)
            put("canMove", r.canMove)
            put("canDelete", r.canDelete)
            put("canRemoveRoot", r.canRemoveRoot)
        }

    private fun envelope(type: String, payloadKey: String, payload: JSONObject, v3: Boolean): String =
        JSONObject().apply {
            put("type", type)
            if (v3) put("protocolVersion", 3)
            put(payloadKey, payload)
        }.toString()

    // ── Session state ─────────────────────────────────────────────────────────

    /** Receiver session (PC → Android). A [FileFrameSink] that writes data frames to a staging file. */
    private inner class HostReceiveSession(
        val transferId: String,
        val destRoot: String,
        val destRelativePath: String?,
        val fileName: String,
        val expectedSize: Long,
        val partial: File,
        val manifest: File,
        val digest: MessageDigest,
        var bytesReceived: Long,
        var lastAcked: Long,
        /**
         * Whether a same-named file in the destination is replaced (upload) or left alone so SAF
         * uniquifies the incoming one (push). See the commit site in [finalizeAndCommit].
         */
        val replaceExisting: Boolean,
    ) : FileFrameSink {
        private val raf = RandomAccessFile(partial, "rw").apply { seek(bytesReceived) }
        private val lock = Any()

        override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
            if (envelope.kind != FileFrameKinds.DATA) return
            synchronized(lock) {
                try {
                    if (envelope.offset != bytesReceived) {
                        FileTransferChannelClient.sendError(transferId, "Out-of-order frame.")
                        deleteStaging()
                        receiveSessions.remove(transferId)
                        close()
                        return
                    }
                    if (expectedSize > 0 && bytesReceived + payload.size > expectedSize) {
                        FileTransferChannelClient.sendError(transferId, "Overshot declared size.")
                        deleteStaging()
                        receiveSessions.remove(transferId)
                        close()
                        return
                    }
                    raf.write(payload)
                    digest.update(payload)
                    bytesReceived += payload.size
                    if (envelope.final || bytesReceived - lastAcked >= FileTransferLimits.ACK_INTERVAL_BYTES) {
                        lastAcked = bytesReceived
                        FileTransferChannelClient.sendAck(transferId, bytesReceived)
                    }
                } catch (e: Exception) {
                    FileTransferChannelClient.sendError(transferId, e.message ?: "write failed")
                }
            }
        }

        override fun onChannelClosed() {
            // Keep the partial + manifest for a later resume; just release the file handle.
            close()
        }

        suspend fun finalizeAndCommit(expectedSha: String?, facade: FileSystemFacade): TransferOutcome {
            synchronized(lock) {
                try { raf.fd.sync() } catch (e: Exception) { /* best-effort */ }
                try { raf.close() } catch (e: Exception) { /* ignore */ }
            }
            val actual = Base64.encodeToString(digest.digest(), Base64.NO_WRAP)
            val complete = expectedSize <= 0 || bytesReceived == expectedSize
            val matches = expectedSha.isNullOrEmpty() || actual == expectedSha
            if (!complete || !matches) {
                deleteStaging()
                return TransferOutcome(false, actual, if (!complete) "Transfer incomplete." else "SHA-256 mismatch.")
            }
            // Commit the verified partial into the SAF destination.
            val parent = facade.resolve(destRoot, (destRelativePath ?: "").trim('/'))
            if (parent == null || !parent.canWrite) {
                deleteStaging()
                return TransferOutcome(false, actual, "Destination no longer writable.")
            }
            // **ONLY AN UPLOAD REPLACES (RemEx-h1p5).** SAF's createFile uniquifies a colliding name
            // by itself ("resume (1).pdf"); this delete exists to defeat that, which is right for an
            // upload — the PC user is looking at that folder and named that file, so replacing it is
            // what they asked for.
            //
            // A PUSH is the opposite situation and used to be unreachable, so this never applied to
            // one. Nobody chose the folder: the PC does not know it and the phone picks it, and the
            // consent prompt names the incoming files without ever saying where they will go. Deleting
            // a same-named file there would destroy something the user never mentioned — silently,
            // with no undo, and with no prompt at all for a device on remembered auto-accept. Letting
            // SAF uniquify costs an odd file name in a rare case; the alternative costs a document.
            if (replaceExisting) parent.findChild(fileName)?.delete()
            val target = parent.createFile("application/octet-stream", fileName)
            if (target == null) {
                deleteStaging()
                return TransferOutcome(false, actual, "Could not create destination file.")
            }
            val ok =
                try {
                    partial.inputStream().use { input ->
                        facade.openOutput(target)?.use { out ->
                            input.copyTo(out, 65536)
                            true
                        } ?: false
                    }
                } catch (e: Exception) {
                    false
                }
            deleteStaging()
            return if (ok) TransferOutcome(true, actual, null)
            else TransferOutcome(false, actual, "Verified but could not be saved.")
        }

        fun deleteStaging() {
            try { partial.delete() } catch (e: Exception) {}
            try { manifest.delete() } catch (e: Exception) {}
        }

        fun close() {
            try { raf.close() } catch (e: Exception) {}
        }
    }

    /** Sender session (Android → PC). A [FileFrameSink] that consumes inbound ack frames. */
    private inner class HostSendSession(val transferId: String) : FileFrameSink {
        @Volatile var committedOffset: Long = 0L
        val ackSignal = Channel<Unit>(Channel.CONFLATED)
        var job: Job? = null

        override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
            when (envelope.kind) {
                FileFrameKinds.ACK -> {
                    envelope.committedOffset?.let { committedOffset = it }
                    ackSignal.trySend(Unit)
                }
                FileFrameKinds.ERROR -> job?.cancel()
            }
        }

        override fun onChannelClosed() {
            job?.cancel()
        }
    }

    private data class TransferOutcome(val verified: Boolean, val sha256: String?, val error: String?)

    // ── Staging helpers (mirror TransferSessionManager) ───────────────────────

    private fun safeStem(transferId: String): String =
        buildString {
            for (c in transferId) append(if (c.isLetterOrDigit() || c == '-' || c == '_') c else '_')
        }

    private fun combineRelative(directory: String?, fileName: String): String {
        val dir = (directory ?: "").replace('\\', '/').trim('/')
        return if (dir.isEmpty()) fileName else "$dir/$fileName"
    }

    private fun manifestMatches(
        manifest: File,
        transferId: String,
        size: Long,
        fileName: String,
        destRoot: String,
    ): Boolean {
        if (!manifest.exists()) return false
        return try {
            val obj = JSONObject(manifest.readText())
            obj.optString("transferId") == transferId &&
                obj.optLong("expectedSize") == size &&
                obj.optString("fileName") == fileName &&
                obj.optString("destRoot") == destRoot
        } catch (e: Exception) {
            false
        }
    }

    private fun writeManifest(
        manifest: File,
        transferId: String,
        size: Long,
        fileName: String,
        destRoot: String,
        destRelativePath: String?,
    ) {
        try {
            val obj =
                JSONObject().apply {
                    put("transferId", transferId)
                    put("expectedSize", size)
                    put("fileName", fileName)
                    put("destRoot", destRoot)
                    if (destRelativePath != null) put("destRelativePath", destRelativePath)
                    put("createdUtc", System.currentTimeMillis())
                }
            manifest.writeText(obj.toString())
        } catch (e: Exception) {
            // best-effort: a missing manifest just forces a fresh (offset 0) transfer next time.
        }
    }

    /** Sweeps staging partials/manifests older than [maxAgeMs] (plan §1.3: 7-day cleanup). */
    fun cleanupOrphans(maxAgeMs: Long) {
        val cutoff = System.currentTimeMillis() - maxAgeMs
        stagingDir.listFiles()?.forEach { f ->
            if (f.isFile && f.lastModified() < cutoff) {
                try { f.delete() } catch (e: Exception) {}
            }
        }
    }
}
