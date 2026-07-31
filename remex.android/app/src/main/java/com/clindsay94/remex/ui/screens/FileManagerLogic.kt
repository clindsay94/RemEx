package com.clindsay94.remex.ui.screens

import org.json.JSONArray

/**
 * Pure, Android-free logic for the file-manager UI (plan WP7). Extracted so the state decisions the
 * [FileTransferViewModel] makes — sorting, breadcrumb parsing, selection reduction, path math — can be
 * unit-tested on the JVM without a live socket, DataStore, or Compose runtime (mirrors how
 * `TransferResumeLogic` isolates the resume arithmetic in WP5).
 *
 * [parseSharedRoots]/[parseFileEntries] are the one exception to "nothing here touches the wire": they
 * parse the raw `roots`/`entries` JSON arrays shared verbatim between `file_roots_response` and
 * `file_browse_response` (see `remex.core`). [FileTransferViewModel] and
 * `com.clindsay94.remex.share.ShareToPcViewModel` both consumed this exact shape independently
 * (RemEx-4xhj); they now share the parse here and map the result onto their own screen-specific model
 * (`RemoteSharedRoot`/`RemoteFileEntry` vs the Share screen's lighter `ShareRoot`/`ShareDirEntry`).
 * `org.json` is plain old Java, so this stays JVM-testable without Robolectric (the app module adds the
 * real `org.json:json` jar as a `testImplementation` for exactly this).
 */

/** Sort field for the folder listing. */
enum class SortField { NAME, SIZE, DATE }

/** Current sort selection: which [SortField] and direction. */
data class SortOption(val field: SortField = SortField.NAME, val ascending: Boolean = true)

/** Whether the listing renders as a single-column list or a thumbnail grid. */
enum class FileViewMode { LIST, GRID }

/**
 * A mounted volume/drive on the peer, exposed once a full-browse consent grant is held. Mirrors
 * `remex.core` `FileVolumeInfo` fields (id/label/path/totalBytes/freeBytes/kind).
 */
data class RemoteVolume(
    val id: String,
    val label: String,
    val path: String,
    val totalBytes: Long,
    val freeBytes: Long,
    val kind: String,
)

/**
 * v3 file-transfer capabilities advertised by the host inside `file_roots_response`. Mirrors
 * `remex.core` `FileCapabilities`. Null until the first roots response arrives; a null (or
 * `protocol < 3`) capability set means the peer is a v2 host and only the legacy base64 path + basic
 * ops are offered — no v3 message is ever sent in that case.
 */
data class FileManagerCapabilities(
    val protocol: Int,
    val binary: Boolean,
    val resume: Boolean,
    val ops: Set<String>,
    val fullBrowse: Boolean,
    val push: Boolean,
) {
    val isV3: Boolean get() = protocol >= 3
}

/** One tappable breadcrumb segment: a display [label] and the absolute [path] it navigates to. */
data class Breadcrumb(val label: String, val path: String)

/**
 * Raw shape of one `roots` array item from `file_roots_response`, before a caller maps it onto its own
 * screen-specific model. Mirrors `remex.core` `FileSharedRoot` fields verbatim.
 */
data class ParsedSharedRoot(
    val rootId: String,
    val displayName: String,
    val isWritable: Boolean,
    val canRename: Boolean,
    val canMove: Boolean,
    val canDelete: Boolean,
    val canRemoveRoot: Boolean,
)

/**
 * Raw shape of one `entries` array item from `file_browse_response`, before a caller maps it onto its
 * own screen-specific model. Mirrors `remex.core` `FileEntry` fields verbatim.
 */
data class ParsedFileEntry(
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedUnixMs: Long,
)

/** Detailed metadata for the properties sheet. Mirrors `remex.core` `FileMetadataResponse`. */
data class FileProperties(
    val name: String,
    val sizeBytes: Long,
    val createdUtc: Long,
    val modifiedUtc: Long,
    val isDirectory: Boolean,
    val itemCount: Int?,
    val mimeType: String?,
    val readOnly: Boolean,
    /** Root-relative path of the target, used to look up its cached thumbnail for the sheet. */
    val relativePath: String? = null,
)

object FileManagerLogic {

    /** The sentinel "up one level" row that browse listings prepend when not at a root. */
    const val PARENT_ENTRY = ".."

    /**
     * Orders a folder listing for display. Directories are always grouped ahead of files (standard
     * file-manager behaviour) regardless of the active field, the "up one level" ([PARENT_ENTRY]) row
     * is always pinned to the very top, and within each group the entries are ordered by the chosen
     * [SortOption]. Name comparison is case-insensitive; size/date fall back to name for stable ties.
     */
    fun sortEntries(entries: List<RemoteFileEntry>, option: SortOption): List<RemoteFileEntry> {
        val parent = entries.filter { it.name == PARENT_ENTRY }
        val rest = entries.filter { it.name != PARENT_ENTRY }
        val (dirs, files) = rest.partition { it.isDirectory }

        val comparator: Comparator<RemoteFileEntry> =
            when (option.field) {
                SortField.NAME -> compareBy { it.name.lowercase() }
                SortField.SIZE -> compareBy<RemoteFileEntry> { it.sizeBytes }.thenBy { it.name.lowercase() }
                SortField.DATE ->
                    compareBy<RemoteFileEntry> { it.modifiedUnixMs }.thenBy { it.name.lowercase() }
            }
        val directed = if (option.ascending) comparator else comparator.reversed()

        return parent + dirs.sortedWith(directed) + files.sortedWith(directed)
    }

    /**
     * Cycles the sort state when the user taps a field: tapping the active field flips its direction;
     * tapping a different field switches to it, ascending.
     */
    fun nextSort(current: SortOption, tapped: SortField): SortOption =
        if (current.field == tapped) current.copy(ascending = !current.ascending)
        else SortOption(tapped, ascending = true)

    /**
     * Splits an absolute remote [path] (within a root labelled [rootLabel]) into tappable breadcrumb
     * segments. The first crumb is always the root itself (navigating to "/"); each subsequent crumb
     * carries the cumulative path so tapping it jumps straight there. Backslashes are normalised to
     * forward slashes so Windows and Linux hosts render identically.
     */
    fun buildBreadcrumbs(rootLabel: String, path: String): List<Breadcrumb> {
        val crumbs = mutableListOf(Breadcrumb(rootLabel, "/"))
        val normalized = path.replace('\\', '/').trim('/')
        if (normalized.isNotEmpty()) {
            var acc = ""
            for (segment in normalized.split('/').filter { it.isNotEmpty() }) {
                acc = if (acc.isEmpty()) segment else "$acc/$segment"
                crumbs.add(Breadcrumb(segment, acc))
            }
        }
        return crumbs
    }

    /**
     * Builds the file-manager header subtitle, appending the read-only marker when — and only
     * when — a selected root actually is read-only.
     *
     * [selectedRootIsWritable] is deliberately nullable, and that is the whole reason this is a
     * function rather than a string template at the call site. The screen's `canWrite` flag is
     * false in TWO different situations: a read-only root is selected, and nothing is selected at
     * all. Driving the marker from it would label the empty "/" placeholder "Read-only", which is
     * not true of anything. Null means no root is selected and must produce no marker.
     * (RemEx-dc57.)
     */
    fun buildLocationSubtitle(
        rootLabel: String,
        path: String,
        selectedRootIsWritable: Boolean?,
        readOnlyLabel: String,
    ): String =
        if (selectedRootIsWritable == false) "$rootLabel • $path • $readOnlyLabel"
        else "$rootLabel • $path"

    /** Adds or removes [name] from the current multi-select set. */
    fun toggleSelection(current: Set<String>, name: String): Set<String> =
        if (name in current) current - name else current + name

    /** Joins a folder [currentPath] and a [childName] into a normalised forward-slash relative path. */
    fun combinePath(currentPath: String, childName: String): String {
        val normalized = currentPath.replace('\\', '/').trimEnd('/')
        return if (normalized.isEmpty() || normalized == "/") childName else "$normalized/$childName"
    }

    /** Absolute path of [currentPath]'s parent folder ("/" when already at a root). */
    fun parentPath(currentPath: String): String {
        val normalized = currentPath.replace('\\', '/').trimEnd('/')
        if (normalized.isEmpty() || normalized == "/") return "/"
        val lastSlash = normalized.lastIndexOf('/')
        // No separator (a single top-level segment) or a leading slash both resolve to the root.
        return if (lastSlash <= 0) "/" else normalized.substring(0, lastSlash)
    }

    /** True at a root folder (nothing above to navigate to). */
    fun isAtRoot(path: String): Boolean {
        val normalized = path.replace('\\', '/').trim('/')
        return normalized.isEmpty()
    }

    /** Parses a `file_roots_response` `roots` array into [ParsedSharedRoot]s. A null array yields empty. */
    fun parseSharedRoots(arr: JSONArray?): List<ParsedSharedRoot> = buildList {
        if (arr == null) return@buildList
        for (i in 0 until arr.length()) {
            val item = arr.getJSONObject(i)
            add(
                ParsedSharedRoot(
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

    /** Parses a `file_browse_response` `entries` array into [ParsedFileEntry]s. A null array yields empty. */
    fun parseFileEntries(arr: JSONArray?): List<ParsedFileEntry> = buildList {
        if (arr == null) return@buildList
        for (i in 0 until arr.length()) {
            val item = arr.getJSONObject(i)
            add(
                ParsedFileEntry(
                    name = item.optString("name"),
                    isDirectory = item.optBoolean("isDirectory"),
                    sizeBytes = item.optLong("sizeBytes"),
                    modifiedUnixMs = item.optLong("modifiedUnixMs"),
                )
            )
        }
    }

    private val IMAGE_EXTENSIONS =
        setOf("jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif")
    private val VIDEO_EXTENSIONS =
        setOf("mp4", "mkv", "webm", "mov", "avi", "m4v", "3gp", "wmv")

    /** File extension (lowercase, no dot) or empty string. */
    fun extensionOf(name: String): String =
        name.substringAfterLast('.', "").lowercase().takeIf { name.contains('.') } ?: ""

    /** True for names a thumbnail could plausibly be generated for (images/videos only, v1). */
    fun isThumbnailCandidate(name: String): Boolean {
        val ext = extensionOf(name)
        return ext in IMAGE_EXTENSIONS || ext in VIDEO_EXTENSIONS
    }

    /** Human-readable byte size (KB/MB/GB), matching the existing file-transfer formatting. */
    fun formatBytes(bytes: Long): String =
        when {
            bytes >= 1_073_741_824L -> "%.1f GB".format(bytes / 1_073_741_824.0)
            bytes >= 1_048_576L -> "%.1f MB".format(bytes / 1_048_576.0)
            bytes >= 1_024L -> "%.1f KB".format(bytes / 1_024.0)
            else -> "$bytes B"
        }
}
