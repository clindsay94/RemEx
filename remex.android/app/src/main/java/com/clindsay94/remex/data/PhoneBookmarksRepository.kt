package com.clindsay94.remex.data

import android.content.ContentResolver
import android.content.Intent
import android.net.Uri
import android.util.Log
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import com.clindsay94.remex.service.FileTransferEngine
import com.clindsay94.remex.service.QueuedTransfer
import com.clindsay94.remex.service.TransferState

/**
 * A folder under "This phone" in the File Transfer tree.
 *
 * @property isBookmark the person added it on the File Transfer screen (phone-only unless shared).
 * @property isShared it is on the "Access from your PC" list, so the PC can see it.
 */
data class PhoneFolder(val uri: String, val isBookmark: Boolean, val isShared: Boolean)

/** The two folder lists the repository joins. The app's is [SettingsManager]; tests use a map. */
interface PhoneFolderStore {
    val sharedUris: Flow<Set<String>>
    val bookmarkUris: Flow<Set<String>>

    /**
     * Grants something else still relies on, which are never released here: the whole-device browse root, and any
     * folder a transfer that isn't finished reads from or writes into (a paused one resumes through the same grant).
     */
    val otherHeldUris: Flow<Set<String>>
    suspend fun setShared(uri: String, shared: Boolean)
    suspend fun setBookmarked(uri: String, bookmarked: Boolean)
}

/**
 * The phone folders the File Transfer screen can browse (file browser redesign, 2026-10-08): the folders shared
 * with the PC, plus phone-only bookmarks.
 *
 * There is still ONE consent surface. A bookmark lives in its own list, which the PC-facing host never reads;
 * "Share with PC" on a bookmark adds it to the same "Access from your PC" list Settings edits, and turning it
 * off takes it back out. The app keeps its persisted folder grant while either list holds the folder, and
 * releases it once neither does (and whole-device browsing doesn't use it), so removing a bookmark doesn't leave a
 * grant behind.
 *
 * @param releaseGrant gives up the persisted URI grant for a folder no list holds any more.
 */
class PhoneBookmarksRepository(
    private val store: PhoneFolderStore,
    private val releaseGrant: (String) -> Unit,
) {
    /** Every folder to list under "This phone": shared ones first, then phone-only bookmarks, each by URI. */
    val folders: Flow<List<PhoneFolder>> = combine(store.sharedUris, store.bookmarkUris, ::merge)

    /** Adds a folder the person just picked (and granted) as a phone-only bookmark. */
    suspend fun addBookmark(uri: String) = store.setBookmarked(uri, true)

    /**
     * Shares a folder with the PC, or stops sharing it. Stopping keeps a bookmarked folder on the phone and
     * releases the grant of one that was only shared.
     */
    suspend fun setShared(uri: String, shared: Boolean) {
        store.setShared(uri, shared)
        if (!shared) releaseIfUnused(uri)
    }

    /** Removes a bookmark. A folder that is still shared stays shared, and keeps its grant. */
    suspend fun removeBookmark(uri: String) {
        store.setBookmarked(uri, false)
        releaseIfUnused(uri)
    }

    private suspend fun releaseIfUnused(uri: String) {
        val held = store.sharedUris.first() + store.bookmarkUris.first() + store.otherHeldUris.first()
        if (uri !in held) releaseGrant(uri)
    }

    companion object {
        /**
         * The folder grants that unfinished transfers depend on: a document URI built under a tree
         * (`content://…/tree/<id>/document/<id>`) belongs to the tree before `/document/`. Failed rows count, since
         * Resume reopens the same document; done and cancelled ones don't.
         */
        fun treesInUse(queue: List<QueuedTransfer>): Set<String> =
            queue.asSequence()
                .filter { it.state != TransferState.Done && it.state != TransferState.Cancelled }
                .mapNotNull { t -> t.localUri.takeIf { "/tree/" in it && "/document/" in it }?.substringBefore("/document/") }
                .toSet()

        /** The joined list, pure so the order and flags are unit-tested. */
        fun merge(shared: Set<String>, bookmarks: Set<String>): List<PhoneFolder> =
            shared.sorted().map { PhoneFolder(it, isBookmark = it in bookmarks, isShared = true) } +
                (bookmarks - shared).sorted().map { PhoneFolder(it, isBookmark = true, isShared = false) }

        /** The app's repository: [SettingsManager]'s two lists, releasing grants through [resolver]. */
        fun create(settings: SettingsManager, resolver: ContentResolver): PhoneBookmarksRepository =
            PhoneBookmarksRepository(
                object : PhoneFolderStore {
                    override val sharedUris = settings.sharedFolderUrisFlow
                    override val bookmarkUris = settings.phoneBookmarkUrisFlow
                    override val otherHeldUris =
                        combine(settings.fullBrowseRootUriFlow, FileTransferEngine.queue) { fullBrowse, queue ->
                            setOfNotNull(fullBrowse) + treesInUse(queue)
                        }
                    override suspend fun setShared(uri: String, shared: Boolean) =
                        if (shared) settings.addSharedFolderUri(uri) else settings.removeSharedFolderUri(uri)
                    override suspend fun setBookmarked(uri: String, bookmarked: Boolean) =
                        settings.setPhoneBookmark(uri, bookmarked)
                },
                releaseGrant = { uri ->
                    try {
                        resolver.releasePersistableUriPermission(
                            Uri.parse(uri),
                            Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
                        )
                    } catch (e: SecurityException) {
                        // Not held (already released, or granted read-only): nothing to give back.
                        Log.i("PhoneBookmarks", "No grant to release for a removed folder", e)
                    }
                },
            )
    }
}
