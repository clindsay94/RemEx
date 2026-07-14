package com.clindsay94.remex.share

import android.app.Application
import android.content.res.AssetFileDescriptor
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.FileTransferEngine
import com.clindsay94.remex.service.FileTransferJobService
import com.clindsay94.remex.ui.screens.FileManagerLogic
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

/** A single item picked from the OS share sheet, resolved to a display name + size. */
data class ShareFile(val uri: Uri, val name: String, val size: Long)

/** A shared root offered by the peer as a destination (mirrors `remex.core` `FileSharedRoot`). */
data class ShareRoot(val rootId: String, val displayName: String, val isWritable: Boolean)

/** A destination-picker folder row (directories only; the ".." row uses [FileManagerLogic.PARENT_ENTRY]). */
data class ShareDirEntry(val name: String, val isParent: Boolean)

/** Coarse UI state for the share screen. */
enum class SharePhase { CONNECTING, NEEDS_SETUP, READY, SENDING, SENT, EMPTY }

/**
 * Drives the "Send to PC" screen (plan WP8): resolves the shared files, mirrors the peer's shared
 * roots + folders over the existing JSON control plane (`file_roots_request` / `file_browse_request`,
 * field names verbatim from `remex.core`), and on confirm stages each file into app storage and
 * enqueues a **push** upload through the already-landed [FileTransferEngine] — so the transfer
 * survives this transient activity finishing.
 *
 * Staging is required for correctness: a share-sheet `content://` grant is scoped to this activity's
 * task and is revoked when it finishes, but the engine streams the bytes later (from the foreground
 * service). Copying into `filesDir/share_staging` first guarantees the source is still readable.
 */
class ShareToPcViewModel(application: Application) : AndroidViewModel(application) {

    private val settings = SettingsManager(application)

    private val _phase = MutableStateFlow(SharePhase.CONNECTING)
    val phase = _phase.asStateFlow()

    private val _files = MutableStateFlow<List<ShareFile>>(emptyList())
    val files = _files.asStateFlow()

    private val _hostLabel = MutableStateFlow("")
    val hostLabel = _hostLabel.asStateFlow()

    private val _roots = MutableStateFlow<List<ShareRoot>>(emptyList())
    val roots = _roots.asStateFlow()

    private val _selectedRootId = MutableStateFlow<String?>(null)
    val selectedRootId = _selectedRootId.asStateFlow()

    private val _path = MutableStateFlow("/")
    val path = _path.asStateFlow()

    private val _folders = MutableStateFlow<List<ShareDirEntry>>(emptyList())
    val folders = _folders.asStateFlow()

    private val _statusText = MutableStateFlow("")
    val statusText = _statusText.asStateFlow()

    private var hostConfigured = false
    private var rootsLoaded = false
    private var filesInitialised = false
    private var filesResolved = false
    private var pendingBrowseRequestId: String? = null

    init {
        // Mirror the peer's shared folders / folder listings from the control plane.
        viewModelScope.launch {
            RemexClientManager.fileTransferMessages.collect { json -> onControlMessage(json) }
        }
        // Track the configured PC + live connection so the screen can show "connecting" vs "ready".
        viewModelScope.launch {
            settings.connectionPreferencesFlow.collect { prefs ->
                hostConfigured = prefs.host.isNotBlank()
                _hostLabel.value =
                    if (prefs.host.isBlank()) "" else "${prefs.host}:${prefs.port}"
                recomputePhase()
            }
        }
        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (connected && hostConfigured && !rootsLoaded) loadRoots()
                recomputePhase()
            }
        }
    }

    /** Called once by the activity with the URIs extracted from the share intent. */
    fun setSharedFiles(uris: List<Uri>) {
        if (filesInitialised) return
        filesInitialised = true
        viewModelScope.launch(Dispatchers.IO) {
            val resolved =
                uris.mapNotNull { uri ->
                    try {
                        val (name, size) = queryMetadata(uri)
                        ShareFile(
                            uri = uri,
                            name = name ?: "shared-${System.currentTimeMillis()}",
                            size = size ?: 0L,
                        )
                    } catch (e: Exception) {
                        Log.w(TAG, "Could not resolve shared uri $uri", e)
                        null
                    }
                }
            _files.value = resolved
            filesResolved = true
            recomputePhase()
        }
    }

    // ── Destination picker ────────────────────────────────────────────────────

    fun selectRoot(rootId: String) {
        if (_selectedRootId.value == rootId) return
        _selectedRootId.value = rootId
        browse("/")
    }

    fun navigateInto(entry: ShareDirEntry) {
        val target =
            if (entry.isParent) FileManagerLogic.parentPath(_path.value)
            else FileManagerLogic.combinePath(_path.value, entry.name)
        browse(target)
    }

    private fun browse(target: String) {
        val rootId = _selectedRootId.value ?: return
        val requestId = UUID.randomUUID().toString().replace("-", "")
        pendingBrowseRequestId = requestId
        _path.value = target
        sendMessage(
            JSONObject().apply {
                put("type", "file_browse_request")
                put(
                    "fileBrowseRequest",
                    JSONObject().apply {
                        put("requestId", requestId)
                        put("path", target)
                        put("rootId", rootId)
                        put("relativePath", target)
                    },
                )
            }
        )
    }

    private fun loadRoots() {
        sendMessage(
            JSONObject().apply {
                put("type", "file_roots_request")
                put("fileRootsRequest", JSONObject())
            }
        )
    }

    // ── Confirm: stage + enqueue push offers ──────────────────────────────────

    /** Stages every shared file into app storage and enqueues a push upload to the chosen folder. */
    fun confirmSend() {
        val rootId = _selectedRootId.value ?: return
        val toSend = _files.value
        if (toSend.isEmpty()) return
        _phase.value = SharePhase.SENDING
        viewModelScope.launch(Dispatchers.IO) {
            var queued = 0
            var skipped = 0
            for (file in toSend) {
                val safeName = ShareDestinationResolver.sanitizeFileName(file.name)
                val staged = stage(file.uri, safeName)
                // Guard against silently uploading an empty file (RemEx-hb1t): stage() returns null on an
                // unreadable source, and can produce a 0-byte file when the content URI yields no bytes
                // (an unmaterialized cloud file, a revoked share grant, etc.). Uploading that saves an
                // empty file on the PC that the host "verifies" as 0 == 0 — silent data loss. Allow through
                // only a source that genuinely declares 0 bytes; queryMetadata is consulted just for the
                // 0-byte case (cheap and rare).
                if (staged == null || (staged.length() == 0L && queryMetadata(file.uri).second != 0L)) {
                    staged?.delete()
                    skipped++
                    continue
                }
                // Send the destination DIRECTORY only — the host appends fileName itself
                // (CombineHostRelative). Sending the full 'dir/name' path doubled it into 'name/name',
                // so every pushed file landed inside a folder named after the file. (RemEx-y6x6.)
                val destRelativePath = _path.value.replace('\\', '/').trim('/')
                FileTransferEngine.enqueueUpload(
                    localUri = Uri.fromFile(staged).toString(),
                    fileName = safeName,
                    size = staged.length(),
                    destRoot = rootId,
                    destRelativePath = destRelativePath,
                    push = true,
                    peerId = _hostLabel.value.ifBlank { null },
                )
                queued++
            }
            if (skipped > 0) {
                _statusText.value =
                    getApplication<Application>().getString(R.string.share_to_pc_skipped_unreadable, skipped)
            }
            if (queued > 0) {
                FileTransferJobService.schedule(getApplication<Application>())
                _phase.value = SharePhase.SENT
            } else {
                // Nothing was sendable — don't show a false "sent" confirmation; keep the user on the list.
                _phase.value = SharePhase.READY
            }
        }
    }

    // ── Inbound control-plane messages ────────────────────────────────────────

    private fun onControlMessage(json: String) {
        val obj =
            try {
                JSONObject(json)
            } catch (e: Exception) {
                return
            }
        when (obj.optString("type")) {
            "file_roots_response" -> handleRoots(obj.optJSONObject("fileRootsResponse"))
            "file_browse_response" -> handleBrowse(obj.optJSONObject("fileBrowseResponse"))
        }
    }

    private fun handleRoots(response: JSONObject?) {
        rootsLoaded = true
        val arr = response?.optJSONArray("roots") ?: JSONArray()
        val list = mutableListOf<ShareRoot>()
        for (i in 0 until arr.length()) {
            val item = arr.getJSONObject(i)
            list.add(
                ShareRoot(
                    rootId = item.optString("rootId"),
                    displayName = item.optString("displayName"),
                    isWritable = item.optBoolean("isWritable"),
                )
            )
        }
        _roots.value = list
        // Auto-select the first writable root (or the first root) so a destination is ready to go.
        if (_selectedRootId.value == null) {
            (list.firstOrNull { it.isWritable } ?: list.firstOrNull())?.let { selectRoot(it.rootId) }
        }
        recomputePhase()
    }

    private fun handleBrowse(response: JSONObject?) {
        if (response == null) return
        if (response.optString("requestId") != pendingBrowseRequestId) return
        val newPath = response.optString("relativePath", _path.value)
        _path.value = if (newPath.isBlank()) "/" else newPath
        val entries = mutableListOf<ShareDirEntry>()
        if (!FileManagerLogic.isAtRoot(_path.value)) {
            entries.add(ShareDirEntry(FileManagerLogic.PARENT_ENTRY, isParent = true))
        }
        val arr = response.optJSONArray("entries") ?: JSONArray()
        for (i in 0 until arr.length()) {
            val e = arr.getJSONObject(i)
            if (e.optBoolean("isDirectory")) {
                entries.add(ShareDirEntry(e.optString("name"), isParent = false))
            }
        }
        _folders.value = entries
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private fun recomputePhase() {
        // Terminal / in-flight phases are owned by confirmSend(); don't fight them.
        if (_phase.value == SharePhase.SENDING || _phase.value == SharePhase.SENT) return
        _phase.value =
            when {
                filesResolved && _files.value.isEmpty() -> SharePhase.EMPTY
                !hostConfigured -> SharePhase.NEEDS_SETUP
                rootsLoaded -> SharePhase.READY
                else -> SharePhase.CONNECTING
            }
    }

    private fun stage(source: Uri, safeName: String): File? =
        try {
            val dir = File(getApplication<Application>().filesDir, "$STAGING_DIR/${UUID.randomUUID()}")
            dir.mkdirs()
            val out = File(dir, safeName)
            getApplication<Application>().contentResolver.openInputStream(source)?.use { input ->
                out.outputStream().use { input.copyTo(it, 65536) }
            } ?: return null
            out
        } catch (e: Exception) {
            Log.w(TAG, "Failed to stage $safeName", e)
            null
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
            null,
        )?.use { cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                if (nameIndex >= 0) name = cursor.getString(nameIndex)
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex)
            }
        }
        if (size == null || size!! <= 0L) {
            try {
                resolver.openAssetFileDescriptor(uri, "r")?.use { afd ->
                    val length = afd.length
                    if (length != AssetFileDescriptor.UNKNOWN_LENGTH && length > 0L) size = length
                }
            } catch (e: Exception) {
                Log.w(TAG, "AFD size fallback failed for $uri", e)
            }
        }
        if (name.isNullOrBlank()) name = uri.lastPathSegment?.substringAfterLast('/')
        return name to size
    }

    /** Best-effort opportunistic sweep of stale staged files (>7 days) left by earlier shares. */
    fun sweepStaleStaging() {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val root = File(getApplication<Application>().filesDir, STAGING_DIR)
                val cutoff = System.currentTimeMillis() - STAGING_MAX_AGE_MS
                root.listFiles()?.forEach { dir ->
                    if (dir.lastModified() < cutoff) dir.deleteRecursively()
                }
            } catch (e: Exception) {
                Log.w(TAG, "Staging sweep failed", e)
            }
        }
    }

    private fun sendMessage(msg: JSONObject) {
        try {
            RemexCoreClient.SendMessage(msg.toString()).getOrNull()
        } catch (e: Exception) {
            Log.e(TAG, "SendMessage failed", e)
        }
    }

    companion object {
        private const val TAG = "ShareToPcVM"
        private const val STAGING_DIR = "share_staging"
        private const val STAGING_MAX_AGE_MS = 7L * 24 * 60 * 60 * 1000
    }
}
