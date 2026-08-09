package com.clindsay94.remex.ui.screens

import android.app.Application
import android.content.res.AssetFileDescriptor
import android.net.Uri
import android.os.SystemClock
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.R
import com.clindsay94.remex.service.BatchConflictChoice
import com.clindsay94.remex.service.ConflictAction
import com.clindsay94.remex.service.FileConflictPolicy
import com.clindsay94.remex.service.FileManageOperations
import com.clindsay94.remex.service.FileTransferEngine
import com.clindsay94.remex.service.FileTransferJobService
import com.clindsay94.remex.service.FileTransferLimits
import com.clindsay94.remex.service.FileTransferNotificationManager
import com.clindsay94.remex.ui.components.FileConflictPrompt
import com.clindsay94.remex.service.TransferProgressFormat
import com.clindsay94.remex.service.TransferProgressText
import com.clindsay94.remex.service.TransferRateEstimator
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
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject

private const val TAG = "FileTransferVM"
private const val CHUNK_SIZE = 65_536
private const val SEARCH_DEBOUNCE_MS = 400L

private fun JSONObject.optMeaningfulString(key: String): String? {
    if (!has(key) || isNull(key)) return null
    val value = optString(key, "").trim()
    return value.takeUnless { it.isEmpty() || it.equals("null", ignoreCase = true) }
}

/**
 * One row in a browse listing (folder entry) OR a search hit. [modifiedUnixMs] powers date sorting;
 * [relativePath] is non-null only for search hits (which live outside the current folder) so the UI
 * can act on them directly. Mirrors `remex.core` `FileEntry` / `FileSearchEntry` fields.
 */
data class RemoteFileEntry(
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedUnixMs: Long = 0L,
    /** Full path relative to the root — set for search hits, null for ordinary current-folder rows. */
    val relativePath: String? = null,
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

/**
 * File-manager screen state + actions (plan WP7). The control plane (roots/browse/manage/volumes/
 * search/metadata/thumbnail) is JSON on `/ws`; bulk v3 transfers are delegated to
 * [FileTransferEngine] (which owns the binary `/ws/files` channel + persistent queue + foreground
 * service) so they survive the screen being closed. A v2 host (no [FileManagerCapabilities.binary])
 * transparently falls back to the untouched legacy base64 transfer path — no v3 message is sent to a
 * v2 peer.
 *
 * All wire field names below mirror `remex.core` (`RemexMessage` / `FileTransferMessages`) VERBATIM.
 */
class FileTransferViewModel(application: Application) : AndroidViewModel(application) {

    // ── Roots / capabilities / volumes ────────────────────────────────────────
    private val _remoteRoots = MutableStateFlow<List<RemoteSharedRoot>>(emptyList())
    val remoteRoots = _remoteRoots.asStateFlow()

    private val _capabilities = MutableStateFlow<FileManagerCapabilities?>(null)
    val capabilities = _capabilities.asStateFlow()

    private val _volumes = MutableStateFlow<List<RemoteVolume>>(emptyList())
    val volumes = _volumes.asStateFlow()

    private val _fullBrowseGranted = MutableStateFlow(false)
    val fullBrowseGranted = _fullBrowseGranted.asStateFlow()

    private val _selectedRootId = MutableStateFlow<String?>(null)
    val selectedRootId = _selectedRootId.asStateFlow()

    // ── Current listing ────────────────────────────────────────────────────────
    private val _remotePath = MutableStateFlow("/")
    val remotePath = _remotePath.asStateFlow()

    private val _rawEntries = MutableStateFlow<List<RemoteFileEntry>>(emptyList())

    private val _sortOption = MutableStateFlow(SortOption())
    val sortOption = _sortOption.asStateFlow()

    private val _viewMode = MutableStateFlow(FileViewMode.LIST)
    val viewMode = _viewMode.asStateFlow()

    /** Sorted view of the current folder (or the search results while searching). */
    private val _displayedEntries = MutableStateFlow<List<RemoteFileEntry>>(emptyList())
    val displayedEntries = _displayedEntries.asStateFlow()

    // ── Search ───────────────────────────────────────────────────────────────
    private val _searchQuery = MutableStateFlow("")
    val searchQuery = _searchQuery.asStateFlow()

    private val _searchActive = MutableStateFlow(false)
    val searchActive = _searchActive.asStateFlow()

    private val _searchTruncated = MutableStateFlow(false)
    val searchTruncated = _searchTruncated.asStateFlow()

    private var searchDebounceJob: Job? = null
    private var pendingSearchRequestId: String? = null
    private val _searchResults = MutableStateFlow<List<RemoteFileEntry>>(emptyList())

    // ── Status / loading ──────────────────────────────────────────────────────
    private val _isLoading = MutableStateFlow(false)
    val isLoading = _isLoading.asStateFlow()

    private val _isRefreshing = MutableStateFlow(false)
    val isRefreshing = _isRefreshing.asStateFlow()

    private val _statusText = MutableStateFlow("")
    val statusText = _statusText.asStateFlow()

    // ── Selection ─────────────────────────────────────────────────────────────
    private val _isSelectionMode = MutableStateFlow(false)
    val isSelectionMode = _isSelectionMode.asStateFlow()

    private val _selectedEntryNames = MutableStateFlow<Set<String>>(emptySet())
    val selectedEntryNames = _selectedEntryNames.asStateFlow()

    // ── Properties sheet ──────────────────────────────────────────────────────
    private val _properties = MutableStateFlow<FileProperties?>(null)
    val properties = _properties.asStateFlow()

    private val _propertiesLoading = MutableStateFlow(false)
    val propertiesLoading = _propertiesLoading.asStateFlow()

    private var pendingPropertiesRequestId: String? = null
    private var propertiesTargetName: String = ""
    private var propertiesTargetRelative: String? = null

    // ── Thumbnails (relativePath -> base64 JPEG, "" = requested/absent) ────────
    private val _thumbnails = MutableStateFlow<Map<String, String>>(emptyMap())
    val thumbnails = _thumbnails.asStateFlow()
    private val requestedThumbnails = ConcurrentHashMap<String, Boolean>()

    // ── Destination picker (copy/move) ────────────────────────────────────────
    private val _destinationPath = MutableStateFlow("/")
    val destinationPath = _destinationPath.asStateFlow()

    private val _destinationEntries = MutableStateFlow<List<RemoteFileEntry>>(emptyList())
    val destinationEntries = _destinationEntries.asStateFlow()

    private val _destinationLoading = MutableStateFlow(false)
    val destinationLoading = _destinationLoading.asStateFlow()

    private var pendingDestinationRequestId: String? = null

    // ── v3 transfer queue (from the shared engine) ────────────────────────────
    val transferQueue = FileTransferEngine.queue

    // ── Legacy (v2) single-transfer state ─────────────────────────────────────
    private val _isTransferring = MutableStateFlow(false)
    val isTransferring = _isTransferring.asStateFlow()

    private val _transferProgress = MutableStateFlow(0f)
    val transferProgress = _transferProgress.asStateFlow()

    // ── Pending request bookkeeping ───────────────────────────────────────────
    private val pendingManageOps = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val pendingRootManageOps = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private var pendingBrowseRequestId: String? = null
    private var pendingRootsDeferred: CompletableDeferred<Unit>? = null
    private var pendingBrowseDeferred: CompletableDeferred<Unit>? = null

    /** The collision the sheet is currently asking about, or null when it is closed (RemEx-agpn). */
    private val _conflictPrompt = MutableStateFlow<FileConflictPrompt?>(null)
    val conflictPrompt = _conflictPrompt.asStateFlow()

    private var pendingConflictAnswer: CompletableDeferred<Pair<ConflictAction, Boolean>>? = null

    /**
     * Identifies the prompt currently on screen, so a late answer cannot resolve a different one.
     *
     * A torn-down sheet can still emit one last dismissal. Without this, that stray Skip would
     * answer the NEXT file's prompt - silently skipping an item the user never saw.
     */
    private var pendingConflictToken: Long = 0L
    private var nextConflictToken: Long = 0L

    private var activeTransferId: String? = null
    private var activeTransferFileName: String? = null
    private var activeDownload: ActiveDownload? = null
    private var activeUploadJob: Job? = null

    /**
     * Throughput and time-remaining for the transfer currently in flight (RemEx-qmiv).
     *
     * One estimator, reset between transfers rather than recreated, because it is fed from
     * [handleProgress] which is the single place progress arrives for either direction.
     */
    private val transferRate = TransferRateEstimator()

    init {
        // Start the (idempotent) engine so its queue is readable and its control-message collector is
        // live before the user enqueues anything; the foreground service holds the process open.
        FileTransferEngine.start(getApplication())
        viewModelScope.launch {
            RemexClientManager.fileTransferMessages.collect { json -> handleFileTransferMessage(json) }
        }
        loadRemoteRoots()
    }

    private fun app() = getApplication<Application>()

    private fun caps() = _capabilities.value

    // ── Roots / browsing ──────────────────────────────────────────────────────

    fun loadRemoteRoots() {
        _isLoading.value = true
        _statusText.value = ""
        val deferred = CompletableDeferred<Unit>()
        pendingRootsDeferred = deferred
        sendMessage(
            JSONObject().apply {
                put("type", "file_roots_request")
                put("fileRootsRequest", JSONObject())
            }
        )
        viewModelScope.launch {
            try {
                withTimeout(30_000) { deferred.await() }
            } catch (_: TimeoutCancellationException) {
                if (pendingRootsDeferred === deferred) {
                    _statusText.value = app().getString(R.string.file_transfer_pin_timeout)
                    _isLoading.value = false
                }
            } finally {
                if (pendingRootsDeferred === deferred) pendingRootsDeferred = null
            }
        }
    }

    fun selectRoot(rootId: String) {
        if (_selectedRootId.value == rootId) return
        _selectedRootId.value = rootId
        clearSelection()
        clearSearchInternal()
        browseRemote("/")
    }

    /** Selects a full-browse volume as the active browsing root (its id doubles as a root id). */
    fun selectVolume(volume: RemoteVolume) {
        _selectedRootId.value = volume.id
        clearSelection()
        clearSearchInternal()
        browseRemote("/")
    }

    fun browseRemote(path: String = _remotePath.value) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = app().getString(R.string.file_transfer_select_folder_first)
            return
        }
        clearSelection()
        val requestId = newRequestId()
        pendingBrowseRequestId = requestId
        _remotePath.value = path
        _isLoading.value = true
        _statusText.value = ""

        val deferred = CompletableDeferred<Unit>()
        pendingBrowseDeferred = deferred
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
                    },
                )
            }
        )
        viewModelScope.launch {
            try {
                withTimeout(30_000) { deferred.await() }
            } catch (_: TimeoutCancellationException) {
                if (pendingBrowseRequestId == requestId) {
                    _statusText.value = app().getString(R.string.file_transfer_pin_timeout)
                    _isLoading.value = false
                    _isRefreshing.value = false
                }
            } finally {
                if (pendingBrowseDeferred === deferred) pendingBrowseDeferred = null
            }
        }
    }

    fun navigateInto(entry: RemoteFileEntry) {
        if (!entry.isDirectory) return
        // Search hits carry their own relativePath; ordinary rows derive it from the current folder.
        val target = entry.relativePath ?: run {
            if (entry.name == FileManagerLogic.PARENT_ENTRY) FileManagerLogic.parentPath(_remotePath.value)
            else FileManagerLogic.combinePath(_remotePath.value, entry.name)
        }
        clearSearchInternal()
        browseRemote(target)
    }

    /** Breadcrumb tap — jump directly to an ancestor path. */
    fun navigateToPath(path: String) {
        clearSearchInternal()
        browseRemote(path)
    }

    /** Pull-to-refresh: re-fetch roots and re-browse the current folder. */
    fun refresh() {
        _isRefreshing.value = true
        loadRemoteRoots()
        if (!_selectedRootId.value.isNullOrBlank()) browseRemote(_remotePath.value)
        else _isRefreshing.value = false
    }

    // ── Sort / view ───────────────────────────────────────────────────────────

    fun setSort(field: SortField) {
        _sortOption.value = FileManagerLogic.nextSort(_sortOption.value, field)
        recomputeDisplayed()
    }

    fun toggleViewMode() {
        _viewMode.value = if (_viewMode.value == FileViewMode.LIST) FileViewMode.GRID else FileViewMode.LIST
    }

    private fun recomputeDisplayed() {
        val source = if (_searchActive.value) _searchResults.value else _rawEntries.value
        _displayedEntries.value = FileManagerLogic.sortEntries(source, _sortOption.value)
    }

    // ── Search (debounced) ──────────────────────────────────────────────────────

    fun setSearchQuery(query: String) {
        _searchQuery.value = query
        searchDebounceJob?.cancel()
        val trimmed = query.trim()
        if (trimmed.isEmpty()) {
            clearSearchInternal()
            return
        }
        // Server-side search is a v3 capability; on a v2 host fall back to filtering the loaded folder.
        val supportsServerSearch = caps()?.ops?.contains("search") == true
        searchDebounceJob = viewModelScope.launch {
            delay(SEARCH_DEBOUNCE_MS)
            if (supportsServerSearch) sendSearch(trimmed) else localFilter(trimmed)
        }
    }

    fun clearSearch() = clearSearchInternal()

    private fun clearSearchInternal() {
        searchDebounceJob?.cancel()
        pendingSearchRequestId = null
        if (_searchQuery.value.isNotEmpty()) _searchQuery.value = ""
        _searchActive.value = false
        _searchTruncated.value = false
        _searchResults.value = emptyList()
        recomputeDisplayed()
    }

    private fun localFilter(query: String) {
        _searchActive.value = true
        _searchTruncated.value = false
        _searchResults.value = _rawEntries.value.filter {
            it.name != FileManagerLogic.PARENT_ENTRY && it.name.contains(query, ignoreCase = true)
        }
        recomputeDisplayed()
    }

    private fun sendSearch(query: String) {
        val rootId = _selectedRootId.value ?: return
        val requestId = newRequestId()
        pendingSearchRequestId = requestId
        _searchActive.value = true
        _isLoading.value = true
        sendV3(
            "file_search_request",
            "fileSearchRequest",
            JSONObject().apply {
                put("requestId", requestId)
                put("rootId", rootId)
                put("relativePath", _remotePath.value)
                put("query", query)
                put("maxResults", FileTransferLimits.SEARCH_MAX_RESULTS)
            },
        )
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    fun enterSelectionMode(entry: RemoteFileEntry) {
        if (entry.name == FileManagerLogic.PARENT_ENTRY) return
        _isSelectionMode.value = true
        _selectedEntryNames.value = setOf(entry.name)
    }

    fun toggleEntrySelection(entry: RemoteFileEntry) {
        if (entry.name == FileManagerLogic.PARENT_ENTRY) return
        if (!_isSelectionMode.value) {
            enterSelectionMode(entry)
            return
        }
        val updated = FileManagerLogic.toggleSelection(_selectedEntryNames.value, entry.name)
        _selectedEntryNames.value = updated
        if (updated.isEmpty()) _isSelectionMode.value = false
    }

    fun selectAll() {
        _selectedEntryNames.value =
            _displayedEntries.value.filter { it.name != FileManagerLogic.PARENT_ENTRY }.map { it.name }.toSet()
    }

    fun clearSelection() {
        _isSelectionMode.value = false
        _selectedEntryNames.value = emptySet()
    }

    // ── File management (delete / rename / mkdir / copy / move) ────────────────

    fun deleteEntry(entry: RemoteFileEntry) {
        val rootId = _selectedRootId.value ?: return
        val relative = entry.relativePath ?: FileManagerLogic.combinePath(_remotePath.value, entry.name)
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = app().getString(R.string.file_transfer_deleting_single, entry.name)
            val outcome = runManage(rootId, relative, FileManageOperations.DELETE)
            if (outcome.failed) Log.w(TAG, "Delete failed: $outcome")
            _statusText.value = when (outcome) {
                is ManageOutcome.TimedOut -> app().getString(R.string.file_transfer_delete_timeout)
                is ManageOutcome.HostRefused -> app().getString(R.string.file_transfer_delete_failed)
                is ManageOutcome.Ok -> app().getString(R.string.file_transfer_deleted_success)
            }
            _isLoading.value = false
            if (!outcome.failed) browseRemote()
        }
    }

    fun deleteSelectedEntries() {
        val rootId = _selectedRootId.value ?: return
        val targets = _displayedEntries.value.filter {
            it.name in _selectedEntryNames.value && it.name != FileManagerLogic.PARENT_ENTRY
        }
        if (targets.isEmpty()) return
        clearSelection()
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = app().getString(R.string.file_transfer_deleting_multiple, targets.size)
            var errors = 0
            for (entry in targets) {
                val relative = entry.relativePath ?: FileManagerLogic.combinePath(_remotePath.value, entry.name)
                if (runManage(rootId, relative, FileManageOperations.DELETE).failed) errors++
            }
            _statusText.value = multiResultText(targets.size, errors)
            _isLoading.value = false
            browseRemote()
        }
    }

    fun renameEntry(entry: RemoteFileEntry, newName: String) {
        val trimmed = newName.trim()
        if (trimmed.isEmpty() || trimmed == entry.name) return
        val rootId = _selectedRootId.value ?: return
        val relative = entry.relativePath ?: FileManagerLogic.combinePath(_remotePath.value, entry.name)
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = app().getString(R.string.file_transfer_renaming_single, entry.name)
            val outcome = runManage(rootId, relative, FileManageOperations.RENAME, newName = trimmed)
            if (outcome.failed) Log.w(TAG, "Rename failed: $outcome")
            _statusText.value = when (outcome) {
                is ManageOutcome.TimedOut -> app().getString(R.string.file_transfer_rename_timeout)
                is ManageOutcome.HostRefused -> app().getString(R.string.file_transfer_rename_failed)
                is ManageOutcome.Ok -> app().getString(R.string.file_transfer_renamed_success)
            }
            _isLoading.value = false
            if (!outcome.failed) browseRemote()
        }
    }

    fun createFolder(name: String) {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return
        val rootId = _selectedRootId.value ?: return
        val relative = FileManagerLogic.combinePath(_remotePath.value, trimmed)
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = app().getString(R.string.file_manager_creating_folder, trimmed)
            val outcome = runManage(rootId, relative, FileManageOperations.MKDIR)
            if (outcome.failed) Log.w(TAG, "Create folder failed: $outcome")
            _statusText.value = when (outcome) {
                is ManageOutcome.TimedOut -> app().getString(R.string.file_transfer_mkdir_timeout)
                // The host's own wording is kept here: FileHostHandler answers this with copy a
                // user can act on, so replacing it with something generic would lose the most
                // useful text in the flow.
                is ManageOutcome.HostRefused ->
                    app().getString(R.string.file_manager_new_folder_failed, outcome.message)
                is ManageOutcome.Ok -> app().getString(R.string.file_manager_folder_created)
            }
            _isLoading.value = false
            if (!outcome.failed) browseRemote()
        }
    }

    fun copySelectedTo(destFolder: String) = manageSelectedTo(destFolder, FileManageOperations.COPY)

    fun moveSelectedTo(destFolder: String) = manageSelectedTo(destFolder, FileManageOperations.MOVE)

    private companion object {
        /**
         * How many times one item may be re-sent after the user answers a collision.
         *
         * **A BOUND EXISTS BECAUSE THE USER IS NOT ALWAYS IN THE LOOP.** A remembered "apply to all"
         * answer satisfies the sheet without asking, so a squatter that keeps claiming each name the
         * host picks would otherwise spin server round-trips behind a spinner with no way to stop it.
         * The host's own 10,000-suffix cap does not help: that fires when 10,000 siblings genuinely
         * exist, not when a racing writer re-takes each freshly chosen name.
         *
         * Three is slack rather than a limit anyone should reach - the honest race resolves on round
         * two. Running out is counted as an ordinary failure, which is what it is.
         */
        const val MAX_CONFLICT_ROUNDS = 3
    }

    private fun manageSelectedTo(destFolder: String, operation: String) {
        val rootId = _selectedRootId.value ?: return
        val targets = _displayedEntries.value.filter {
            it.name in _selectedEntryNames.value && it.name != FileManagerLogic.PARENT_ENTRY
        }
        if (targets.isEmpty()) return
        clearSelection()
        viewModelScope.launch {
            _isLoading.value = true
            try {
            val movingLabel =
                if (operation == FileManageOperations.MOVE) app().getString(R.string.file_manager_moving, targets.size)
                else app().getString(R.string.file_manager_copying, targets.size)
            _statusText.value = movingLabel
            var errors = 0
            var skipped = 0
            val renamed = mutableListOf<String>()

            // SCOPED TO THIS BATCH BY CONSTRUCTION. A remembered "Replace, apply to all" that
            // outlived the operation it was given for would overwrite a file in some later,
            // unrelated copy the user was never asked about - so it is created here and dies here,
            // rather than living on the ViewModel where forgetting to clear it is silent data loss.
            val batchChoice = BatchConflictChoice()

            for ((index, entry) in targets.withIndex()) {
                val relative = FileManagerLogic.combinePath(_remotePath.value, entry.name)
                val destination = FileManagerLogic.combinePath(destFolder, entry.name)

                // A LOOP, NOT TWO ATTEMPTS, and review is the reason. This used to run the operation,
                // ask once, retry once, and collapse the retry's outcome to errors++ without ever
                // reading its error code. That made resolved_name_taken UNREACHABLE: the host can
                // only send it on a retry, because the name it describes is one the host picks only
                // when a conflictResolution arrives. So the code, its action set, its body text and
                // all nine translations were dead on arrival - the exact "declared but never
                // delivered" shape this repo has been bitten by before.
                //
                // BOUNDED, because a standing "keep both, apply to all" satisfies the sheet without
                // asking. Unbounded, a squatter re-taking each name the host picks would spin
                // round-trips behind a spinner with nobody able to stop it. The honest case resolves
                // on round two; three is slack, and running out is an ordinary failure.
                lastResolvedName = null
                var outcome = runManage(rootId, relative, operation, destinationPath = destination)
                var rounds = 0
                // NAMED FOR WHAT IT GUARDS, not for what happened. The first name said the user had
                // resolved it, which reads backwards - Skip IS the user resolving it, arguably the
                // most decisive answer available - and would mislead whoever adds the next exit path
                // into setting the wrong side. What this actually means is that the item is already
                // in a tally and the code below must not add it to another.
                var alreadyCounted = false

                while (outcome.failed && rounds < MAX_CONFLICT_ROUNDS) {
                    val conflict = outcome as? ManageOutcome.HostRefused
                    val actions = FileConflictPolicy.actionsFor(conflict?.errorCode, operation)
                    if (actions.isEmpty()) {
                        // Not a collision - an ordinary failure the user cannot answer. Raising a
                        // sheet would offer "Replace" as a response to "the disk is full".
                        break
                    }

                    val answer = resolveConflict(
                        fileName = conflict?.conflictingName ?: entry.name,
                        errorCode = conflict?.errorCode.orEmpty(),
                        operation = operation,
                        actions = actions,
                        hasRemaining = index < targets.lastIndex,
                        batchChoice = batchChoice,
                    )

                    val resolution = FileConflictPolicy.resolutionFor(answer)
                    if (resolution == null) {
                        // Skip sends nothing at all, so it cannot fail - and it is NOT an error,
                        // which is why it is counted apart. Reporting a deliberate skip as a failure
                        // would tell the user something went wrong when they chose it.
                        alreadyCounted = true
                        skipped++
                        break
                    }

                    lastResolvedName = null
                    outcome = runManage(rootId, relative, operation, destinationPath = destination,
                        conflictResolution = resolution)
                    rounds++
                }

                // THE TAIL IS A PURE FUNCTION NOW (RemEx-dtbd). Five exits reach this point and the
                // accounting across them was verified only by inspection: the source-shape guards in
                // FileConflictWiringTest would pass unchanged on a loop that double-counted skipped
                // or dropped renamed. It depends on nothing but these three locals, so it is tested
                // directly instead.
                val tally = FileManagerLogic.tallyItem(alreadyCounted, outcome.failed, lastResolvedName)
                errors += tally.errors
                tally.renamed?.let { renamed += it }
            }

            // ASSEMBLED ONCE, AT THE END. Review caught the per-item versions being written to a
            // conflated StateFlow and overwritten by this line with no suspension in between, so
            // "Saved as report (2).pdf" was never observable - and never at all for a single-file
            // copy, which is the common case and the exact guarantee the bead asked for.
            //
            // SKIPS ARE REPORTED, NOT SUBTRACTED INTO SILENCE. Counting them out of both numerator
            // and denominator made an all-skipped batch read "Deleted 0 items", with nothing saying
            // the user's own choice was why.
            // BROWSE FIRST, THEN THE SUMMARY - the order is the whole fix. browseRemote() clears
            // _statusText synchronously, and _statusText is a conflated StateFlow, so a summary
            // written before it is discarded without ever being collected. Review caught the first
            // attempt at this defect merely relocating it: the mid-loop writes were folded into one
            // terminal write that was STILL overwritten one line later.
            browseRemote()
            _statusText.value = batchSummary(operation, targets.size - skipped, errors, skipped, renamed)
            } finally {
                // The spinner comes down even if this coroutine is cancelled mid-sheet, which is
                // what a disconnect while the prompt is open looks like. Leaving it up strands the
                // screen in a loading state nothing will ever clear.
                _isLoading.value = false
            }
        }
    }

    /**
     * Asks the user what to do about one collision, reusing a standing answer where it is valid.
     *
     * **A STANDING ANSWER IS STILL CHECKED AGAINST THIS COLLISION.** Someone who chose "Replace,
     * apply to all" for a batch of ordinary collisions has not agreed to destroy a directory tree
     * when the next item turns out to be a different kind of thing - so a remembered answer that is
     * not on this collision's offer list is discarded and the question asked again.
     */
    private suspend fun resolveConflict(
        fileName: String,
        errorCode: String,
        operation: String,
        actions: List<ConflictAction>,
        hasRemaining: Boolean,
        batchChoice: BatchConflictChoice,
    ): ConflictAction {
        batchChoice.standingAnswer?.let { standing ->
            // The REAL operation. A hardcoded COPY was harmless only because copy and move share
            // a rule today - which is exactly why it would have survived until the policy gained a
            // move-specific one, and then been wrong silently.
            if (batchChoice.canApply(standing, errorCode, operation)) {
                return standing
            }
        }

        val answer = CompletableDeferred<Pair<ConflictAction, Boolean>>()
        val token = ++nextConflictToken
        pendingConflictAnswer = answer
        pendingConflictToken = token
        _conflictPrompt.value = FileConflictPrompt(token, fileName, errorCode, actions, hasRemaining)

        val (action, applyToAll) = try {
            answer.await()
        } finally {
            // CLEARED ON EVERY EXIT, INCLUDING CANCELLATION. If the batch coroutine is cancelled
            // while the sheet is open - a disconnect, the screen going away - leaving _conflictPrompt
            // set would strand a sheet with nothing behind it to answer.
            if (pendingConflictToken == token) {
                _conflictPrompt.value = null
                pendingConflictAnswer = null
            }
        }

        batchChoice.remember(action, applyToAll)
        return action
    }

    /** The sheet's answer. Skip on dismissal - see FileConflictSheet for why that is the default. */
    fun onConflictResolved(token: Long, action: ConflictAction, applyToAll: Boolean) {
        // DROPPED IF IT IS NOT ANSWERING THE PROMPT ON SCREEN. A torn-down sheet can emit one last
        // dismissal, and without this check that stray Skip would answer the NEXT file's question -
        // skipping an item the user was never shown.
        if (token != pendingConflictToken) return

        pendingConflictAnswer?.complete(action to applyToAll)
    }

    /**
     * The one line a finished copy/move batch leaves behind.
     *
     * Built in one place because [_statusText] is a conflated StateFlow rendered into a single
     * label: anything written mid-loop is overwritten by the next write with no suspension between,
     * so it is never observed. Every fact the user needs has to arrive together.
     */
    private fun batchSummary(
        operation: String,
        succeeded: Int,
        errors: Int,
        skipped: Int,
        renamed: List<String>,
    ): String {
        val res = app().resources

        // COPY AND MOVE GET THEIR OWN WORDS. This used to borrow multiResultText, whose strings are
        // delete-specific, so an all-skipped copy of three files read "Deleted 0 items." - a file
        // manager that also deletes telling the user it deleted something it did not touch.
        val done = succeeded - errors
        val parts = mutableListOf(
            res.getQuantityString(
                if (operation == FileManageOperations.MOVE) R.plurals.file_conflict_moved_count
                else R.plurals.file_conflict_copied_count,
                done,
                done,
            )
        )

        if (errors > 0) parts += res.getQuantityString(R.plurals.file_conflict_failed_count, errors, errors)
        if (skipped > 0) parts += res.getQuantityString(R.plurals.file_conflict_skipped_count, skipped, skipped)

        // Named only when there is ONE, because a list of renamed files does not fit a status line -
        // and the case that matters is the single-file copy, where the user is looking at exactly
        // one name and would otherwise believe it was the one they asked for.
        if (renamed.size == 1) parts += app().getString(R.string.file_conflict_saved_as, renamed.single())

        return parts.reduce { acc, part ->
            app().getString(R.string.file_transfer_detail_separator, acc, part)
        }
    }

    /**
     * How one file_manage_request ended.
     *
     * The point of the type is that [TimedOut] and [HostRefused] are different events that need
     * different words. Before it, runManage returned a String? and its timeout branch handed back
     * the DELETE-specific message for every operation it serves - delete, rename, mkdir, copy and
     * move - so a mkdir that timed out literally read "New folder failed: Delete timed out.", a
     * rename that timed out was flattened into the same sentence as a rename the host refused, and
     * copy/move lost the reason entirely. Delete was correct only by accident, because the
     * hardcoded string happened to be its own. (RemEx-201d.)
     */
    private sealed interface ManageOutcome {
        data object Ok : ManageOutcome

        /** The host never answered. Nothing is known about whether it acted. */
        data object TimedOut : ManageOutcome

        /**
         * The host answered and said no. [message] is its own user-facing wording.
         *
         * [errorCode] and [conflictingName] are the machine-readable half (RemEx-6vd8): non-null
         * only for a filename collision, and the ONLY thing a client may branch on. The message is
         * English prose that the host will one day translate, so matching it would work today and
         * silently stop working later.
         */
        data class HostRefused(
            val message: String,
            val errorCode: String? = null,
            val conflictingName: String? = null,
        ) : ManageOutcome
    }

    private val ManageOutcome.failed: Boolean
        get() = this !is ManageOutcome.Ok

    /** Sends one file_manage_request and awaits its response. */
    /** Set by [runManage] when the host renamed the destination. Read and cleared by the caller. */
    private var lastResolvedName: String? = null

    private suspend fun runManage(
        rootId: String,
        relativePath: String,
        operation: String,
        conflictResolution: String? = null,
        newName: String? = null,
        destinationPath: String? = null,
    ): ManageOutcome {
        val requestId = newRequestId()
        val deferred = CompletableDeferred<JSONObject>()
        pendingManageOps[requestId] = deferred
        // copy/move/mkdir are v3-only ops; delete/rename ride the untouched legacy path.
        val v3Op = operation == FileManageOperations.COPY ||
            operation == FileManageOperations.MOVE ||
            operation == FileManageOperations.MKDIR
        val payload = JSONObject().apply {
            put("requestId", requestId)
            put("rootId", rootId)
            put("relativePath", relativePath)
            put("operation", operation)
            if (newName != null) put("newName", newName)
            if (destinationPath != null) put("destinationPath", destinationPath)
            if (v3Op) put("overwrite", false)
            // Only ever sent on a RETRY, after the user answered a collision sheet. A first attempt
            // carries none, which is what makes the host fail loudly instead of guessing.
            if (v3Op && conflictResolution != null) put("conflictResolution", conflictResolution)
        }
        if (v3Op) sendV3("file_manage_request", "fileManageRequest", payload)
        else sendMessage(JSONObject().apply { put("type", "file_manage_request"); put("fileManageRequest", payload) })
        return try {
            val response = withTimeout(30_000) { deferred.await() }
            val body = response.optJSONObject("fileManageResponse")
            val hostError = body?.optMeaningfulString("errorMessage")
            if (hostError == null) {
                // A rename the HOST chose, reported so the user is not left believing they have
                // report.pdf when the file on disk is report (2).pdf.
                body?.optMeaningfulString("resolvedName")?.let { lastResolvedName = it }
                ManageOutcome.Ok
            } else {
                ManageOutcome.HostRefused(
                    hostError,
                    errorCode = body.optMeaningfulString("errorCode"),
                    conflictingName = body.optMeaningfulString("conflictingName"),
                )
            }
        } catch (_: TimeoutCancellationException) {
            // Deliberately carries no message. Naming the operation is the CALLER's job - this
            // function serves five of them and cannot know which one it is being used for, which is
            // exactly how the delete-specific wording used to leak into the others.
            ManageOutcome.TimedOut
        } finally {
            pendingManageOps.remove(requestId)
        }
    }

    private fun multiResultText(total: Int, errors: Int): String =
        if (errors == 0) app().getString(R.string.file_transfer_deleted_multiple_success, total)
        else app().getString(R.string.file_transfer_deleted_multiple_failed, total - errors, total, errors)

    // ── Quick-access root management ──────────────────────────────────────────

    fun pinCurrentFolder() {
        val rootId = _selectedRootId.value ?: return
        val path = _remotePath.value
        if (FileManagerLogic.isAtRoot(path)) return
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = app().getString(R.string.file_transfer_adding_shortcut)
            val respObj = runRootManage { requestId ->
                JSONObject().apply {
                    put("requestId", requestId)
                    put("operation", "add")
                    put("sourceRootId", rootId)
                    put("sourceRelativePath", path)
                }
            }
            handleRootManageResult(respObj, R.string.file_transfer_shortcut_added, R.string.file_transfer_pin_failed)
        }
    }

    fun removeRoot(rootId: String) {
        viewModelScope.launch {
            _isLoading.value = true
            _statusText.value = app().getString(R.string.file_transfer_removing_shortcut)
            val respObj = runRootManage { requestId ->
                JSONObject().apply {
                    put("requestId", requestId)
                    put("operation", "remove")
                    put("rootId", rootId)
                }
            }
            handleRootManageResult(respObj, R.string.file_transfer_shortcut_removed, R.string.file_transfer_remove_failed)
        }
    }

    /** Builds a root-manage payload with the supplied request id, sends it, and awaits the response. */
    private suspend fun runRootManage(buildPayload: (String) -> JSONObject): JSONObject? {
        val requestId = newRequestId()
        val deferred = CompletableDeferred<JSONObject>()
        pendingRootManageOps[requestId] = deferred
        sendMessage(
            JSONObject().apply {
                put("type", "file_root_manage_request")
                put("fileRootManageRequest", buildPayload(requestId))
            }
        )
        return try {
            val response = withTimeout(30_000) { deferred.await() }
            response.optJSONObject("fileRootManageResponse")
        } catch (_: TimeoutCancellationException) {
            null
        } finally {
            pendingRootManageOps.remove(requestId)
        }
    }

    private fun handleRootManageResult(respObj: JSONObject?, successRes: Int, failRes: Int) {
        _isLoading.value = false
        if (respObj == null) {
            _statusText.value = app().getString(R.string.file_transfer_pin_timeout)
            return
        }
        val error = respObj.optMeaningfulString("errorMessage")
        if (error != null) {
            Log.w(TAG, "Root manage failed: $error")
            _statusText.value = app().getString(failRes)
        } else {
            _statusText.value = app().getString(successRes)
            updateRootsFromResponse(respObj)
        }
    }

    // ── Volumes (full-browse) ─────────────────────────────────────────────────

    fun loadVolumes() {
        if (caps()?.fullBrowse != true) return
        val requestId = newRequestId()
        _statusText.value = app().getString(R.string.file_manager_requesting_volumes)
        sendV3(
            "file_volumes_request",
            "fileVolumesRequest",
            JSONObject().apply { put("requestId", requestId) },
        )
    }

    // ── Properties sheet ──────────────────────────────────────────────────────

    fun showProperties(entry: RemoteFileEntry) {
        val rootId = _selectedRootId.value ?: return
        val relative = entry.relativePath ?: FileManagerLogic.combinePath(_remotePath.value, entry.name)
        // Warm the thumbnail cache so the properties sheet can show a preview for image files.
        requestThumbnail(entry)
        if (caps()?.isV3 != true) {
            // v2 host has no metadata message; show the little we already know locally.
            _properties.value = FileProperties(
                name = entry.name,
                sizeBytes = entry.sizeBytes,
                createdUtc = 0,
                modifiedUtc = entry.modifiedUnixMs,
                isDirectory = entry.isDirectory,
                itemCount = null,
                mimeType = null,
                readOnly = false,
                relativePath = relative,
            )
            return
        }
        val requestId = newRequestId()
        pendingPropertiesRequestId = requestId
        propertiesTargetName = entry.name
        propertiesTargetRelative = relative
        _propertiesLoading.value = true
        _properties.value = null
        sendV3(
            "file_metadata_request",
            "fileMetadataRequest",
            JSONObject().apply {
                put("requestId", requestId)
                put("rootId", rootId)
                put("relativePath", relative)
            },
        )
    }

    fun dismissProperties() {
        _properties.value = null
        _propertiesLoading.value = false
        pendingPropertiesRequestId = null
    }

    // ── Thumbnails ────────────────────────────────────────────────────────────

    fun requestThumbnail(entry: RemoteFileEntry) {
        if (caps()?.isV3 != true) return
        if (entry.isDirectory || !FileManagerLogic.isThumbnailCandidate(entry.name)) return
        val rootId = _selectedRootId.value ?: return
        val relative = entry.relativePath ?: FileManagerLogic.combinePath(_remotePath.value, entry.name)
        if (requestedThumbnails.putIfAbsent(relative, true) != null) return // already asked
        val requestId = newRequestId()
        // The thumbnail response echoes only requestId (not the path), so remember which path this id
        // is for and resolve it on the way back.
        pendingThumbnailPaths[requestId] = relative
        sendV3(
            "file_thumbnail_request",
            "fileThumbnailRequest",
            JSONObject().apply {
                put("requestId", requestId)
                put("rootId", rootId)
                put("relativePath", relative)
                put("maxDim", FileTransferLimits.THUMBNAIL_DEFAULT_MAX_DIM)
            },
        )
    }
    private val pendingThumbnailPaths = ConcurrentHashMap<String, String>()

    // ── Destination picker (copy/move browsing) ──────────────────────────────

    fun openDestinationPicker() {
        _destinationPath.value = "/"
        browseDestination("/")
    }

    fun browseDestination(path: String) {
        val rootId = _selectedRootId.value ?: return
        val requestId = newRequestId()
        pendingDestinationRequestId = requestId
        _destinationPath.value = path
        _destinationLoading.value = true
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
                    },
                )
            }
        )
    }

    fun navigateDestinationInto(entry: RemoteFileEntry) {
        if (!entry.isDirectory) return
        val target =
            if (entry.name == FileManagerLogic.PARENT_ENTRY) FileManagerLogic.parentPath(_destinationPath.value)
            else FileManagerLogic.combinePath(_destinationPath.value, entry.name)
        browseDestination(target)
    }

    // ── Transfers ─────────────────────────────────────────────────────────────

    fun uploadFromUri(uri: Uri) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = app().getString(R.string.file_transfer_select_folder_first)
            return
        }
        val (displayName, sizeBytes) = queryMetadata(uri)
        val targetName = displayName ?: "upload-${System.currentTimeMillis()}"
        // Destination DIRECTORY only — the host appends fileName itself. Including it here doubled the
        // path into 'name/name', so every upload became a folder named after the file. (RemEx-y6x6.)
        val destRelativePath = _remotePath.value.replace('\\', '/').trim('/')

        if (caps()?.binary == true) {
            // v3: hand off to the persistent queue + foreground service.
            FileTransferEngine.enqueueUpload(
                localUri = uri.toString(),
                fileName = targetName,
                size = sizeBytes ?: 0L,
                destRoot = rootId,
                destRelativePath = destRelativePath,
            )
            FileTransferJobService.schedule(app())
            _statusText.value = app().getString(R.string.file_manager_queued_upload, targetName)
        } else {
            legacyUpload(uri, rootId, targetName, destRelativePath, sizeBytes)
        }
    }

    /** SAF destination already chosen by the caller (CreateDocument); routes v3 vs legacy. */
    fun downloadEntryTo(entry: RemoteFileEntry, destinationUri: Uri) {
        val rootId = _selectedRootId.value
        if (rootId.isNullOrBlank()) {
            _statusText.value = app().getString(R.string.file_transfer_select_folder_first)
            return
        }
        if (entry.isDirectory) return
        val relative = entry.relativePath ?: FileManagerLogic.combinePath(_remotePath.value, entry.name)

        if (caps()?.binary == true) {
            FileTransferEngine.enqueueDownload(
                destUri = destinationUri.toString(),
                fileName = entry.name,
                size = entry.sizeBytes,
                sourceRoot = rootId,
                sourceRelativePath = relative,
            )
            FileTransferJobService.schedule(app())
            _statusText.value = app().getString(R.string.file_manager_queued_download, entry.name)
        } else {
            legacyDownload(entry, rootId, relative, destinationUri)
        }
    }

    // ── v3 queue controls ─────────────────────────────────────────────────────

    fun pauseTransfer(id: String) = FileTransferEngine.pause(id)

    fun resumeTransfer(id: String) {
        FileTransferEngine.resume(id)
        FileTransferJobService.schedule(app())
    }

    fun cancelTransfer(id: String) = FileTransferEngine.cancel(id)

    fun clearFinishedTransfers() = FileTransferEngine.clearFinished()

    // ── Legacy (v2) base64 transfer path — kept intact for v2 peers ────────────

    private fun legacyUpload(uri: Uri, rootId: String, targetName: String, remoteRelativePath: String, sizeBytes: Long?) {
        if (_isTransferring.value) return
        val transferId = newRequestId()
        activeTransferId = transferId
        activeTransferFileName = targetName
        _isTransferring.value = true
        _transferProgress.value = 0f
        _statusText.value = app().getString(R.string.file_transfer_uploading_single, targetName)
        FileTransferNotificationManager.showTransferStarted(app(), targetName, isDownload = false)

        activeUploadJob = viewModelScope.launch(Dispatchers.IO) {
            try {
                val input = app().contentResolver.openInputStream(uri)
                    ?: throw IllegalStateException("Unable to open the selected file.")
                input.use { stream ->
                    sendMessage(JSONObject().apply {
                        put("type", "file_transfer_start")
                        put("fileTransferStart", JSONObject().apply {
                            put("transferId", transferId)
                            put("direction", "upload")
                            put("remotePath", remoteRelativePath)
                            put("remoteRootId", rootId)
                            put("remoteRelativePath", remoteRelativePath)
                            put("fileName", targetName)
                            put("totalBytes", sizeBytes ?: 0L)
                            put("sha256", "")
                        })
                    })
                    val digest = MessageDigest.getInstance("SHA-256")
                    val buffer = ByteArray(CHUNK_SIZE)
                    var offset = 0L
                    while (true) {
                        val read = stream.read(buffer)
                        if (read <= 0) break
                        digest.update(buffer, 0, read)
                        sendMessage(JSONObject().apply {
                            put("type", "file_transfer_chunk")
                            put("fileTransferChunk", JSONObject().apply {
                                put("transferId", transferId)
                                put("offset", offset)
                                put("dataBase64", Base64.encodeToString(buffer, 0, read, Base64.NO_WRAP))
                            })
                        })
                        offset += read
                    }
                    sendMessage(JSONObject().apply {
                        put("type", "file_transfer_end")
                        put("fileTransferEnd", JSONObject().apply {
                            put("transferId", transferId)
                            put("success", true)
                            put("sha256", Base64.encodeToString(digest.digest(), Base64.NO_WRAP))
                        })
                    })
                }
            } catch (_: CancellationException) {
                Log.i(TAG, "Upload cancelled for $transferId")
            } catch (e: Exception) {
                Log.e(TAG, "Upload failed", e)
                _statusText.value = app().getString(R.string.file_transfer_upload_failed)
                FileTransferNotificationManager.showTransferFailed(app(), app().getString(R.string.file_transfer_upload_failed))
                resetTransferState()
            }
        }
    }

    private fun legacyDownload(entry: RemoteFileEntry, rootId: String, remoteRelativePath: String, destinationUri: Uri) {
        if (_isTransferring.value) return
        val transferId = newRequestId()
        val output = app().contentResolver.openOutputStream(destinationUri, "w")
        if (output == null) {
            _statusText.value = app().getString(R.string.file_transfer_unable_open_location)
            return
        }
        val channel = Channel<ByteArray>(Channel.UNLIMITED)
        val writerJob = viewModelScope.launch(Dispatchers.IO) {
            output.use { stream ->
                for (chunk in channel) stream.write(chunk)
                stream.flush()
            }
        }
        activeTransferId = transferId
        activeTransferFileName = entry.name
        activeDownload = ActiveDownload(transferId, destinationUri, output, channel, writerJob, MessageDigest.getInstance("SHA-256"))
        _isTransferring.value = true
        _transferProgress.value = 0f
        _statusText.value = app().getString(R.string.file_transfer_downloading_single, entry.name)
        FileTransferNotificationManager.showTransferStarted(app(), entry.name, isDownload = true)
        sendMessage(JSONObject().apply {
            put("type", "file_transfer_start")
            put("fileTransferStart", JSONObject().apply {
                put("transferId", transferId)
                put("direction", "download")
                put("remotePath", remoteRelativePath)
                put("remoteRootId", rootId)
                put("remoteRelativePath", remoteRelativePath)
                put("fileName", entry.name)
                put("totalBytes", 0)
                put("sha256", "")
            })
        })
    }

    fun cancelLegacyTransfer() {
        val id = activeTransferId ?: return
        sendMessage(JSONObject().apply {
            put("type", "file_transfer_cancel")
            put("fileTransferCancel", JSONObject().apply { put("transferId", id) })
        })
        activeUploadJob?.cancel()
        cleanupDownload(deletePartial = true)
        _statusText.value = app().getString(R.string.file_transfer_cancelled)
        FileTransferNotificationManager.cancel(app())
        resetTransferState()
    }

    // ── Inbound message handling ──────────────────────────────────────────────

    private fun handleFileTransferMessage(json: String) {
        try {
            val obj = JSONObject(json)
            when (obj.optString("type")) {
                "file_roots_response" -> handleRootsResponse(obj)
                "file_browse_response" -> handleBrowseResponse(obj)
                "file_manage_response" -> handleManageResponse(obj)
                "file_root_manage_response" -> handleRootManageResponse(obj)
                "file_volumes_response" -> handleVolumesResponse(obj)
                "file_search_response" -> handleSearchResponse(obj)
                "file_metadata_response" -> handleMetadataResponse(obj)
                "file_thumbnail_response" -> handleThumbnailResponse(obj)
                // Legacy (v2) transfer stream:
                "file_transfer_progress" -> handleProgress(obj)
                "file_transfer_chunk" -> handleChunk(obj)
                "file_transfer_end" -> handleTransferEnd(obj)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error handling file transfer message", e)
        }
    }

    private fun handleRootsResponse(obj: JSONObject) {
        pendingRootsDeferred?.complete(Unit)
        _isLoading.value = false
        _isRefreshing.value = false
        val response = obj.optJSONObject("fileRootsResponse") ?: return
        parseCapabilities(response.optJSONObject("fileCapabilities"))
        val errorMessage = response.optMeaningfulString("errorMessage")
        if (errorMessage != null) {
            _statusText.value = app().getString(R.string.file_transfer_shared_folders_unavailable, errorMessage)
            return
        }
        val roots = parseRoots(response.optJSONArray("roots") ?: JSONArray())
        _remoteRoots.value = roots
        if (_selectedRootId.value.isNullOrBlank()) {
            roots.firstOrNull()?.let { selectRoot(it.rootId) }
        }
        if (roots.isEmpty() && !_fullBrowseGranted.value) {
            _statusText.value = app().getString(R.string.file_transfer_no_shared_folders)
        }
        // Volumes are NOT auto-requested: an ungranted file_volumes_request raises a consent prompt on
        // the PC, so full-device browse is only ever kicked off by an explicit user tap (loadVolumes()).
    }

    private fun parseCapabilities(obj: JSONObject?) {
        if (obj == null) {
            _capabilities.value = null
            return
        }
        val ops = mutableSetOf<String>()
        obj.optJSONArray("ops")?.let { arr -> for (i in 0 until arr.length()) ops.add(arr.optString(i)) }
        _capabilities.value = FileManagerCapabilities(
            protocol = obj.optInt("protocol", 2),
            binary = obj.optBoolean("binary", false),
            resume = obj.optBoolean("resume", false),
            ops = ops,
            fullBrowse = obj.optBoolean("fullBrowse", false),
            push = obj.optBoolean("push", false),
        )
    }

    private fun handleBrowseResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileBrowseResponse") ?: return
        val requestId = response.optString("requestId")
        // Route to the destination picker if this is its browse.
        if (requestId == pendingDestinationRequestId) {
            _destinationLoading.value = false
            // Surface a failed folder browse instead of silently showing an empty picker: without
            // this the copy/move destination sheet looked like an empty folder on any error.
            response.optMeaningfulString("errorMessage")?.let { error ->
                Log.w(TAG, "Browse failed: $error")
                _statusText.value = app().getString(R.string.file_transfer_browse_error)
                _destinationEntries.value = emptyList()
                return
            }
            val entries = mutableListOf<RemoteFileEntry>()
            if (!FileManagerLogic.isAtRoot(_destinationPath.value)) {
                entries.add(RemoteFileEntry(FileManagerLogic.PARENT_ENTRY, true, 0))
            }
            parseEntries(response.optJSONArray("entries")).filter { it.isDirectory }.forEach { entries.add(it) }
            _destinationEntries.value = entries
            return
        }
        if (requestId != pendingBrowseRequestId) return

        pendingBrowseDeferred?.complete(Unit)
        _isLoading.value = false
        _isRefreshing.value = false
        val errorMessage = response.optMeaningfulString("errorMessage")
        if (errorMessage != null) {
            Log.w(TAG, "Browse failed: $errorMessage")
            _statusText.value = app().getString(R.string.file_transfer_browse_error)
            return
        }
        val path = response.optString("relativePath", _remotePath.value)
        _remotePath.value = if (path.isBlank()) "/" else path

        val entries = mutableListOf<RemoteFileEntry>()
        if (!FileManagerLogic.isAtRoot(_remotePath.value)) {
            entries.add(RemoteFileEntry(FileManagerLogic.PARENT_ENTRY, true, 0))
        }
        entries.addAll(parseEntries(response.optJSONArray("entries")))
        _rawEntries.value = entries
        requestedThumbnails.clear()
        recomputeDisplayed()
    }

    // Shared parse lives in FileManagerLogic (RemEx-4xhj) — ShareToPcViewModel consumes the same
    // `entries` shape (folders only, for its destination picker). This screen additionally needs
    // sizeBytes/modifiedUnixMs for the full listing, so it maps onto RemoteFileEntry itself.
    private fun parseEntries(arr: JSONArray?): List<RemoteFileEntry> =
        FileManagerLogic.parseFileEntries(arr).map {
            RemoteFileEntry(
                name = it.name,
                isDirectory = it.isDirectory,
                sizeBytes = it.sizeBytes,
                modifiedUnixMs = it.modifiedUnixMs,
            )
        }

    private fun handleManageResponse(obj: JSONObject) {
        val requestId = obj.optJSONObject("fileManageResponse")?.optString("requestId") ?: return
        pendingManageOps[requestId]?.complete(obj)
    }

    private fun handleRootManageResponse(obj: JSONObject) {
        val requestId = obj.optJSONObject("fileRootManageResponse")?.optString("requestId") ?: return
        pendingRootManageOps[requestId]?.complete(obj)
    }

    private fun handleVolumesResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileVolumesResponse") ?: return
        // Classified from the value just PARSED, not from the flow it was written to (review): the
        // outcome must describe the message in hand, not whatever shared state happens to hold when
        // the next line runs.
        val granted = response.optBoolean("fullBrowseGranted", false)
        _fullBrowseGranted.value = granted
        val error = response.optMeaningfulString("errorMessage")

        // SAY WHICH OF THE THREE THIS IS (RemEx-3qmd). Until RemEx-l580 the wire could not tell a
        // refusal somebody made from one the host had to make because it could not reach this phone,
        // so this end said nothing at all — it cleared "Loading drives…" and left the screen blank.
        // The unreachable case is the one worth distinguishing: it is the only one the person holding
        // the phone can actually fix.
        val outcome = FileManagerLogic.classifyVolumesResponse(
            fullBrowseGranted = granted,
            denyReason = response.optMeaningfulString("denyReason"),
            errorMessage = error,
        )
        if (error != null) Log.w(TAG, "Browse failed: $error")
        when (outcome) {
            FileManagerLogic.VolumesOutcome.FAILED ->
                _statusText.value = app().getString(R.string.file_transfer_browse_error)
            // Only overwrite the status while it is still OUR spinner, the same rule the tail of this
            // method already follows (review). The host holds this response for up to 60 seconds
            // waiting on consent, and a user who gave up waiting and renamed a file should not have
            // the rename result replaced by an answer to a question they stopped caring about.
            FileManagerLogic.VolumesOutcome.PHONE_UNREACHABLE ->
                replaceRequestingVolumesStatus(R.string.file_manager_full_browse_unreachable)
            FileManagerLogic.VolumesOutcome.REFUSED ->
                replaceRequestingVolumesStatus(R.string.file_manager_full_browse_refused)
            FileManagerLogic.VolumesOutcome.GRANTED -> Unit
        }

        val arr = response.optJSONArray("volumes") ?: JSONArray()
        val list = mutableListOf<RemoteVolume>()
        for (i in 0 until arr.length()) {
            val v = arr.getJSONObject(i)
            list.add(
                RemoteVolume(
                    id = v.optString("id"),
                    label = v.optString("label"),
                    path = v.optString("path"),
                    totalBytes = v.optLong("totalBytes"),
                    freeBytes = v.optLong("freeBytes"),
                    kind = v.optString("kind"),
                )
            )
        }
        _volumes.value = list
        if (_statusText.value == app().getString(R.string.file_manager_requesting_volumes)) _statusText.value = ""
    }

    /**
     * Swaps the "Loading drives…" spinner for [messageRes], and leaves anything else alone.
     *
     * The full-browse answer can arrive up to a minute after the tap, because the host holds it while
     * a consent prompt is open. By then the user may well have done something else on this screen, and
     * clobbering that operation's result to answer a question they abandoned is a worse outcome than
     * saying nothing (review of RemEx-3qmd). Same rule the end of [handleVolumesResponse] already
     * applies when it clears the spinner on success.
     */
    private fun replaceRequestingVolumesStatus(messageRes: Int) {
        if (_statusText.value == app().getString(R.string.file_manager_requesting_volumes)) {
            _statusText.value = app().getString(messageRes)
        }
    }

    private fun handleSearchResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileSearchResponse") ?: return
        if (response.optString("requestId") != pendingSearchRequestId) return
        _isLoading.value = false
        val error = response.optMeaningfulString("errorMessage")
        if (error != null) {
            Log.w(TAG, "Browse failed: $error")
            _statusText.value = app().getString(R.string.file_transfer_browse_error)
            return
        }
        _searchTruncated.value = response.optBoolean("truncated", false)
        val arr = response.optJSONArray("entries") ?: JSONArray()
        val hits = mutableListOf<RemoteFileEntry>()
        for (i in 0 until arr.length()) {
            val e = arr.getJSONObject(i)
            hits.add(
                RemoteFileEntry(
                    name = e.optString("name"),
                    isDirectory = e.optBoolean("isDirectory"),
                    sizeBytes = e.optLong("sizeBytes"),
                    modifiedUnixMs = e.optLong("modifiedUnixMs"),
                    relativePath = e.optString("relativePath"),
                )
            )
        }
        _searchResults.value = hits
        recomputeDisplayed()
    }

    private fun handleMetadataResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileMetadataResponse") ?: return
        if (response.optString("requestId") != pendingPropertiesRequestId) return
        _propertiesLoading.value = false
        val error = response.optMeaningfulString("errorMessage")
        if (error != null) {
            Log.w(TAG, "Browse failed: $error")
            _statusText.value = app().getString(R.string.file_transfer_browse_error)
            _properties.value = null
            return
        }
        _properties.value = FileProperties(
            name = propertiesTargetName,
            sizeBytes = response.optLong("size"),
            createdUtc = response.optLong("createdUtc"),
            modifiedUtc = response.optLong("modifiedUtc"),
            isDirectory = response.optBoolean("isDirectory"),
            itemCount = if (response.has("itemCount") && !response.isNull("itemCount")) response.optInt("itemCount") else null,
            mimeType = response.optMeaningfulString("mimeType"),
            readOnly = response.optBoolean("readOnly"),
            relativePath = propertiesTargetRelative,
        )
    }

    private fun handleThumbnailResponse(obj: JSONObject) {
        val response = obj.optJSONObject("fileThumbnailResponse") ?: return
        val requestId = response.optString("requestId")
        val path = pendingThumbnailPaths.remove(requestId) ?: return
        val base64 = response.optMeaningfulString("jpegBase64") ?: return
        _thumbnails.value = _thumbnails.value + (path to base64)
    }

    // ── Legacy transfer stream handlers ───────────────────────────────────────

    private fun handleProgress(obj: JSONObject) {
        val progress = obj.optJSONObject("fileTransferProgress") ?: return
        if (progress.optString("transferId") != activeTransferId) return
        val total = progress.optLong("totalBytes", 0)
        val transferred = progress.optLong("bytesTransferred", 0)

        // SystemClock.elapsedRealtime(), never System.currentTimeMillis(). A wall clock steps
        // backwards on an NTP correction, and the estimator correctly refuses a negative interval -
        // so a wall clock would not produce a wrong speed, it would silently stop producing one.
        transferRate.update(transferred, SystemClock.elapsedRealtime())

        val action = if (activeDownload != null) app().getString(R.string.file_transfer_action_downloading)
            else app().getString(R.string.file_transfer_action_uploading)
        if (total > 0) {
            _transferProgress.value = (transferred.toFloat() / total).coerceIn(0f, 1f)
            _statusText.value = app().getString(
                R.string.file_transfer_progress_status,
                action,
                withRateAndEta("${(transferred * 100 / total)}%", transferred, total),
            )
        } else if (transferred > 0) {
            _transferProgress.value = 0f
            // Total unknown, so there is no ETA to offer - but the speed is still known, and it is
            // the only evidence the user has that a sizeless transfer is moving at all.
            _statusText.value = app().getString(
                R.string.file_transfer_progress_status,
                action,
                withRateAndEta(FileManagerLogic.formatBytes(transferred), transferred, null),
            )
        } else return
        FileTransferNotificationManager.showTransferProgress(
            app(), activeTransferFileName ?: "File transfer", activeDownload != null, transferred, total,
            bytesPerSecond = transferRate.bytesPerSecondAt(SystemClock.elapsedRealtime()),
            secondsRemaining = transferRate.secondsRemainingAt(
                transferred, total.takeIf { it > 0L }, SystemClock.elapsedRealtime()),
        )
    }

    /**
     * Appends "12.4 MB/s · 38 seconds left" to a progress figure, or returns it untouched.
     *
     * Untouched is the common early case and is deliberate: the estimator needs two observations
     * before it can say anything, and padding the gap with a placeholder number is what the parent
     * bead ruled out - a user plans around "14 hours remaining" even when it is about to become
     * "30 seconds".
     */
    private fun withRateAndEta(base: String, transferred: Long, total: Long?): String {
        val now = SystemClock.elapsedRealtime()
        val suffix = TransferProgressText.progressSuffix(
            app(),
            TransferProgressFormat.rate(transferRate.bytesPerSecondAt(now)),
            TransferProgressFormat.eta(transferRate.secondsRemainingAt(transferred, total, now)),
        ) ?: return base

        return app().getString(R.string.file_transfer_detail_separator, base, suffix)
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
                val fileName = activeTransferFileName ?: app().getString(R.string.file_transfer_file_fallback)
                val actualSha256 = Base64.encodeToString(download.digest.digest(), Base64.NO_WRAP)
                if (expectedSha256 != null && expectedSha256 != actualSha256) {
                    Log.w(TAG, "Download SHA-256 mismatch")
                    cleanupDownload(deletePartial = true)
                    _statusText.value = app().getString(R.string.file_transfer_download_failed_integrity)
                    FileTransferNotificationManager.showTransferFailed(app(), app().getString(R.string.file_transfer_download_failed_sha256))
                    resetTransferState()
                    return
                }
                cleanupDownload(deletePartial = false)
                _statusText.value = app().getString(R.string.file_transfer_download_complete)
                FileTransferNotificationManager.showTransferComplete(app(), fileName, isDownload = true)
            } else {
                val fileName = activeTransferFileName ?: app().getString(R.string.file_transfer_file_fallback)
                _statusText.value = app().getString(R.string.file_transfer_upload_complete)
                FileTransferNotificationManager.showTransferComplete(app(), fileName, isDownload = false)
                browseRemote(_remotePath.value)
            }
        } else {
            cleanupDownload(deletePartial = true)
            Log.w(TAG, "Transfer failed: ${errorMessage ?: "no detail"}")
            _statusText.value = app().getString(R.string.file_transfer_transfer_failed)
            FileTransferNotificationManager.showTransferFailed(app(), app().getString(R.string.file_transfer_transfer_failed))
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

    // Shared parse lives in FileManagerLogic (RemEx-4xhj) — ShareToPcViewModel consumes the same
    // `roots` shape. Only the mapping to this screen's richer RemoteSharedRoot (canRename/canMove/
    // canDelete/canRemoveRoot) stays here; error handling for the response stays in the caller.
    private fun parseRoots(arr: JSONArray): List<RemoteSharedRoot> =
        FileManagerLogic.parseSharedRoots(arr).map {
            RemoteSharedRoot(
                rootId = it.rootId,
                displayName = it.displayName,
                isWritable = it.isWritable,
                canRename = it.canRename,
                canMove = it.canMove,
                canDelete = it.canDelete,
                canRemoveRoot = it.canRemoveRoot,
            )
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
                    DocumentsContract.deleteDocument(app().contentResolver, download.destinationUri)
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

        // The estimate belongs to ONE transfer. Carrying it into the next would describe the last
        // one, and the gap between them would read as a long stall - so the first seconds of every
        // subsequent transfer would show a speed nobody is achieving.
        transferRate.resetRate()
    }

    private fun queryMetadata(uri: Uri): Pair<String?, Long?> {
        val resolver = app().contentResolver
        var name: String? = null
        var size: Long? = null
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                if (nameIndex >= 0) name = cursor.getString(nameIndex)
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex)
            }
        }
        val initialSize = size
        if (initialSize == null || initialSize <= 0L) {
            try {
                resolver.openAssetFileDescriptor(uri, "r")?.use { afd ->
                    val length = afd.length
                    if (length != AssetFileDescriptor.UNKNOWN_LENGTH && length > 0L) size = length
                }
            } catch (e: Exception) {
                Log.w(TAG, "openAssetFileDescriptor fallback failed for $uri", e)
            }
        }
        return name to size
    }

    private fun newRequestId(): String = UUID.randomUUID().toString().replace("-", "")

    /** Sends a v3-only control/browse message (protocolVersion 3). Only ever called for a v3 host. */
    private fun sendV3(type: String, payloadKey: String, payload: JSONObject) {
        sendMessage(
            JSONObject().apply {
                put("type", type)
                put("protocolVersion", 3)
                put(payloadKey, payload)
            }
        )
    }

    private fun sendMessage(msg: JSONObject) {
        try {
            RemexCoreClient.SendMessage(msg.toString()).getOrNull()
        } catch (e: Exception) {
            Log.e(TAG, "SendMessage failed", e)
            _statusText.value =
                app().getString(R.string.file_transfer_send_error)
        }
    }
}
