package com.clindsay94.remex.share

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The share target's rules (RemEx-172ba): which shared items it takes, its phases, and recent folders. */
class ShareTargetLogicTest {

    // Strings stand in for Uris: the rule only ever asks an item for its scheme.
    private fun scheme(s: String) = s.substringBefore("://", missingDelimiterValue = "").ifEmpty { null }

    private fun parse(action: String?, single: String? = null, multiple: List<String?>? = null) =
        ShareIntentParsing.sharedItems(action, { single }, { multiple }, ::scheme)

    @Test
    fun `a single share yields its one item`() {
        assertEquals(listOf("content://a/1"), parse(ShareIntentParsing.ACTION_SEND, single = "content://a/1"))
    }

    @Test
    fun `a multiple share yields every item, without nulls or repeats`() {
        assertEquals(
            listOf("content://a/1", "content://a/2"),
            parse(
                ShareIntentParsing.ACTION_SEND_MULTIPLE,
                multiple = listOf("content://a/1", null, "content://a/2", "content://a/1"),
            ),
        )
    }

    @Test
    fun `only content uris are taken, so no storage permission is ever needed`() {
        assertEquals(
            listOf("CONTENT://a/3"),
            parse(
                ShareIntentParsing.ACTION_SEND_MULTIPLE,
                multiple = listOf("file:///sdcard/x.jpg", "https://example.com/y", "CONTENT://a/3", "no-scheme"),
            ),
        )
        assertEquals(emptyList<String>(), parse(ShareIntentParsing.ACTION_SEND, single = "file:///sdcard/x.jpg"))
    }

    @Test
    fun `an empty or unknown share yields nothing`() {
        assertEquals(emptyList<String>(), parse(ShareIntentParsing.ACTION_SEND, single = null))
        assertEquals(emptyList<String>(), parse(ShareIntentParsing.ACTION_SEND_MULTIPLE, multiple = null))
        assertEquals(emptyList<String>(), parse("android.intent.action.VIEW", single = "content://a/1"))
        assertEquals(emptyList<String>(), parse(null, single = "content://a/1"))
    }

    @Test
    fun `the action names match the platform's`() {
        assertEquals("android.intent.action.SEND", ShareIntentParsing.ACTION_SEND)
        assertEquals("android.intent.action.SEND_MULTIPLE", ShareIntentParsing.ACTION_SEND_MULTIPLE)
    }

    // ── Phases ──

    private fun phase(
        filesResolved: Boolean = true,
        fileCount: Int = 1,
        pairedPcsLoaded: Boolean = true,
        hasPairedPc: Boolean = true,
        targetChosen: Boolean = true,
        rootsLoaded: Boolean = false,
        connectedOrConnecting: Boolean = false,
    ) = SharePhaseRules.phase(filesResolved, fileCount, pairedPcsLoaded, hasPairedPc, targetChosen, rootsLoaded, connectedOrConnecting)

    @Test
    fun `nothing readable to send says so before anything else`() {
        assertEquals(SharePhase.EMPTY, phase(fileCount = 0, hasPairedPc = false))
    }

    @Test
    fun `a cold launch waits for the pairings instead of flashing the setup screen`() {
        assertEquals(SharePhase.CONNECTING, phase(pairedPcsLoaded = false, hasPairedPc = false))
    }

    @Test
    fun `nothing paired explains and offers the PCs page`() {
        assertEquals(SharePhase.NEEDS_SETUP, phase(hasPairedPc = false))
    }

    @Test
    fun `paired with no live target offers the paired PCs`() {
        assertEquals(SharePhase.CHOOSE_PC, phase(targetChosen = false))
        // Disconnected and not trying: offer the PCs rather than spin forever.
        assertEquals(SharePhase.CHOOSE_PC, phase(connectedOrConnecting = false))
    }

    @Test
    fun `connecting to the target waits, and its folders make it ready`() {
        assertEquals(SharePhase.CONNECTING, phase(connectedOrConnecting = true))
        assertEquals(SharePhase.READY, phase(rootsLoaded = true, connectedOrConnecting = true))
    }

    // ── Recent folders ──

    private fun folder(host: String, path: String, root: String = "r1") = ShareRecentFolder(host, root, "Docs", path)

    @Test
    fun `recent folders are newest first, deduplicated, and a few per PC`() {
        var list = emptyList<ShareRecentFolder>()
        list = ShareRecentFolders.record(list, folder("pc", "/a"))
        list = ShareRecentFolders.record(list, folder("pc", "/b"))
        list = ShareRecentFolders.record(list, folder("other", "/x"))
        list = ShareRecentFolders.record(list, folder("pc", "a/")) // the same folder as "/a"
        assertEquals(listOf("/a", "/x", "/b"), list.map { it.path })

        list = ShareRecentFolders.record(list, folder("pc", "/c"))
        list = ShareRecentFolders.record(list, folder("pc", "/d"))
        val pcOnly = list.filter { it.host == "pc" }.map { it.path }
        assertEquals(listOf("/d", "/c", "/a"), pcOnly)
        assertEquals(ShareRecentFolders.MAX_PER_PC, pcOnly.size)
        assertTrue(list.any { it.host == "other" })
    }

    @Test
    fun `only the current PC's folders on roots it still shares are offered`() {
        val all = listOf(folder("pc", "/a"), folder("PC", "/b", root = "gone"), folder("other", "/x"))
        assertEquals(listOf("/a"), ShareRecentFolders.forPc(all, "pc", setOf("r1")).map { it.path })
    }

    @Test
    fun `recent folders survive a round trip and ignore junk`() {
        val list = listOf(folder("pc", "/a"), folder("other", "/"))
        assertEquals(list, ShareRecentFolders.parse(ShareRecentFolders.encode(list)))
        assertEquals(emptyList<ShareRecentFolder>(), ShareRecentFolders.parse("not json"))
        assertEquals(emptyList<ShareRecentFolder>(), ShareRecentFolders.parse(null))
        assertEquals(emptyList<ShareRecentFolder>(), ShareRecentFolders.parse("""[{"host":"","rootId":"r"}]"""))
    }
}
