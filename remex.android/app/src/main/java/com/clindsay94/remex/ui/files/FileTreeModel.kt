package com.clindsay94.remex.ui.files

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Which device a file is on, from the phone's point of view. */
enum class FileSide { Phone, Pc }

/**
 * A top-level folder in the tree: a shared folder or bookmark on the phone (its id is the tree URI), or a
 * shared folder or volume on the PC (its id is the PC's root id).
 */
data class TreeRootInfo(
    val rootId: String,
    val label: String,
    val isWritable: Boolean = true,
    /** Phone: on the "Access from your PC" list. */
    val isShared: Boolean = false,
    /** Phone: a bookmark the person added on this screen. */
    val isBookmark: Boolean = false,
    /** PC: a whole drive, listed after "Browse device". */
    val isVolume: Boolean = false,
)

/** One visible line of the tree, flattened for a LazyColumn. */
data class TreeRow(
    val key: String,
    val depth: Int,
    val kind: Kind,
    val side: FileSide,
    val rootId: String?,
    val path: String,
    val label: String,
    val expanded: Boolean,
    val loading: Boolean,
    val failed: Boolean,
    /** False for a folder known to have no sub-folders, so no chevron is drawn. */
    val expandable: Boolean,
    val root: TreeRootInfo?,
) {
    enum class Kind { Device, Root, Folder }
}

/**
 * The folder tree of the File Transfer screen (file browser redesign, 2026-10-08): "This phone" and "PC", their
 * top-level folders, and sub-folders loaded one level at a time when a row is opened. Mirrors the PC screen's
 * `FileTreeViewModel`. Android-free, so expanding, failing, refreshing and revealing are unit-tested.
 *
 * @param listFolders the names of the sub-folders of `(side, rootId, path)`; throws when they can't be listed.
 */
class FileTreeModel(
    private val scope: CoroutineScope,
    private val listFolders: suspend (FileSide, String, String) -> List<String>,
) {
    private class Node(var children: List<String>? = null, var expanded: Boolean = false, var loading: Boolean = false, var failed: Boolean = false)

    private val roots = mutableMapOf(FileSide.Phone to emptyList<TreeRootInfo>(), FileSide.Pc to emptyList<TreeRootInfo>())
    private val nodes = HashMap<String, Node>()
    private val available = mutableMapOf(FileSide.Phone to true, FileSide.Pc to true)

    private val _rows = MutableStateFlow<List<TreeRow>>(emptyList())
    val rows: StateFlow<List<TreeRow>> = _rows.asStateFlow()

    init {
        // Both devices start open: their folders are the first thing to pick from.
        node(deviceKey(FileSide.Phone)).expanded = true
        node(deviceKey(FileSide.Pc)).expanded = true
        publish()
    }

    /** Replaces one device's top-level folders, keeping what was open in the ones that are still there. */
    fun setRoots(side: FileSide, newRoots: List<TreeRootInfo>) = synchronized(this) {
        val gone = roots[side].orEmpty().map { it.rootId }.toSet() - newRoots.map { it.rootId }.toSet()
        roots[side] = newRoots
        if (gone.isNotEmpty()) {
            nodes.keys.removeAll { key -> gone.any { key.startsWith(rootPrefix(side, it)) } }
        }
        publish()
    }

    /** False hides a device's folders (the PC while disconnected) without forgetting what was open. */
    fun setAvailable(side: FileSide, isAvailable: Boolean) {
        synchronized(this) {
            if (available[side] == isAvailable) return
            available[side] = isAvailable
            publish()
        }
    }

    /** Opens or closes a row; opening a folder the first time loads its sub-folders. */
    fun toggle(row: TreeRow) {
        val start = synchronized(this) {
            val n = node(row.key)
            n.expanded = !n.expanded
            val load = n.expanded && row.kind != TreeRow.Kind.Device && (n.children == null || n.failed) && !n.loading
            if (load) { n.loading = true; n.failed = false }
            publish()
            load
        }
        if (start) load(row.side, row.rootId!!, row.path)
    }

    /**
     * Records the sub-folders of `(side, rootId, path)` from a listing made anyway (the contents pane browsing it),
     * so the tree stays current with no request of its own: a folder made, renamed or deleted shows at once.
     */
    fun setChildren(side: FileSide, rootId: String, path: String, folderNames: List<String>) {
        synchronized(this) {
            val n = node(folderKey(side, rootId, path))
            n.children = folderNames.distinct().sortedWith(String.CASE_INSENSITIVE_ORDER)
            n.loading = false
            n.failed = false
            publish()
        }
    }

    /**
     * Opens every level down to `(side, rootId, path)` so the folder being browsed is visible in the tree. Levels
     * not loaded yet are loaded on the way, one at a time; a level that fails stops the walk there.
     */
    suspend fun reveal(side: FileSide, rootId: String, path: String) {
        synchronized(this) { node(deviceKey(side)).expanded = true }
        var current = ""
        val segments = normalize(path).split('/').filter { it.isNotEmpty() }
        for (i in 0..segments.size) {
            val key = folderKey(side, rootId, current)
            val needsLoad = synchronized(this) {
                val n = node(key)
                if (i < segments.size) n.expanded = true
                val missing = i < segments.size && n.children == null && !n.loading
                if (missing) n.loading = true
                publish()
                missing
            }
            if (i == segments.size) break
            if (needsLoad && !loadNow(side, rootId, current)) break
            current = if (current.isEmpty()) segments[i] else "$current/${segments[i]}"
        }
        synchronized(this) { publish() }
    }

    private fun load(side: FileSide, rootId: String, path: String) {
        scope.launch { loadNow(side, rootId, path) }
    }

    private suspend fun loadNow(side: FileSide, rootId: String, path: String): Boolean {
        val key = folderKey(side, rootId, path)
        val result = try {
            // distinct(): a provider may list two folders with one name; two rows would share a key.
            Result.success(listFolders(side, rootId, path).distinct().sortedWith(String.CASE_INSENSITIVE_ORDER))
        } catch (e: CancellationException) {
            synchronized(this) { nodes[key]?.loading = false; publish() }
            throw e
        } catch (e: Exception) {
            Result.failure(e)
        }
        synchronized(this) {
            val n = node(key)
            n.loading = false
            n.failed = result.isFailure
            result.getOrNull()?.let { n.children = it }
            publish()
        }
        return result.isSuccess
    }

    private fun node(key: String) = nodes.getOrPut(key) { Node() }

    private fun publish() {
        val out = ArrayList<TreeRow>()
        for (side in FileSide.entries) {
            val deviceKey = deviceKey(side)
            val device = node(deviceKey)
            val sideRoots = if (available[side] == true) roots[side].orEmpty() else emptyList()
            out += TreeRow(deviceKey, 0, TreeRow.Kind.Device, side, null, "", "", device.expanded, false, false, true, null)
            if (!device.expanded) continue
            for (root in sideRoots) addFolder(out, side, root, root.rootId, "", root.label, depth = 1)
        }
        _rows.value = out
    }

    private fun addFolder(out: MutableList<TreeRow>, side: FileSide, root: TreeRootInfo, rootId: String, path: String, label: String, depth: Int) {
        val key = folderKey(side, rootId, path)
        val n = nodes[key]
        val expanded = n?.expanded == true
        val kind = if (path.isEmpty()) TreeRow.Kind.Root else TreeRow.Kind.Folder
        val expandable = n?.children?.isNotEmpty() ?: true
        out += TreeRow(key, depth, kind, side, rootId, path, label, expanded, n?.loading == true, n?.failed == true, expandable, root)
        if (!expanded) return
        for (child in n.children.orEmpty()) {
            addFolder(out, side, root, rootId, if (path.isEmpty()) child else "$path/$child", child, depth + 1)
        }
    }

    companion object {
        fun deviceKey(side: FileSide) = "${side.name}|"
        private fun rootPrefix(side: FileSide, rootId: String) = "${side.name}|$rootId|"
        fun folderKey(side: FileSide, rootId: String, path: String) = rootPrefix(side, rootId) + normalize(path)

        /** A tree path: '/' separators, no leading or trailing slash. */
        fun normalize(path: String) = path.replace('\\', '/').trim('/')
    }
}
