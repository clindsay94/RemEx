package com.clindsay94.remex.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The rules the phone's file host applies to a root id and path the PC names (RemEx-xt0af): only a
 * folder the person shares right now, and only real names under it.
 */
class SharedPathPolicyTest {
    private val shared = setOf(
        "content://com.android.externalstorage.documents/tree/primary%3ADCIM",
        "content://com.android.externalstorage.documents/tree/primary%3ADownload",
    )
    private val dcim = shared.first()

    @Test
    fun aSharedRootAndAPlainPath_resolveToTheirNames() {
        assertEquals(listOf("Camera", "IMG_1.jpg"), SharedPathPolicy.legacySegments(dcim, "Camera/IMG_1.jpg", shared))
    }

    @Test
    fun theRootItself_isAnEmptyWalk() {
        assertEquals(emptyList<String>(), SharedPathPolicy.legacySegments(dcim, "", shared))
        assertEquals(emptyList<String>(), SharedPathPolicy.legacySegments(dcim, "/", shared))
        assertEquals(emptyList<String>(), SharedPathPolicy.legacySegments(dcim, null, shared))
    }

    @Test
    fun oneSurroundingSlash_isTolerated() {
        assertEquals(listOf("Camera"), SharedPathPolicy.legacySegments(dcim, "/Camera/", shared))
    }

    @Test
    fun aRootThePersonDoesNotShare_isRefused() {
        val elsewhere = "content://com.android.externalstorage.documents/tree/primary%3A"
        assertNull(SharedPathPolicy.legacySegments(elsewhere, "Camera", shared))
        assertNull(SharedPathPolicy.legacySegments("", "Camera", shared))
        assertNull(SharedPathPolicy.legacySegments(null, "Camera", shared))
    }

    @Test
    fun aRootIsMatchedExactly_notByPrefix() {
        // A longer tree URI that merely STARTS with a shared one is a different folder.
        assertNull(SharedPathPolicy.legacySegments("$dcim%2FCamera", "", shared))
        assertFalse(SharedPathPolicy.isAllowedRoot(dcim.uppercase(), shared))
    }

    @Test
    fun dotAndDotDot_refuseTheWholePath() {
        assertNull(SharedPathPolicy.legacySegments(dcim, "..", shared))
        assertNull(SharedPathPolicy.legacySegments(dcim, "../Download/x", shared))
        assertNull(SharedPathPolicy.legacySegments(dcim, "Camera/../../x", shared))
        assertNull(SharedPathPolicy.legacySegments(dcim, ".", shared))
        assertNull(SharedPathPolicy.legacySegments(dcim, "Camera/./IMG_1.jpg", shared))
    }

    @Test
    fun anEmptyName_refusesTheWholePath() {
        assertNull(SharedPathPolicy.legacySegments(dcim, "Camera//IMG_1.jpg", shared))
    }

    @Test
    fun backslashNulAndOverlongNames_areRefused() {
        assertNull(SharedPathPolicy.legacySegments(dcim, "Camera\\..\\x", shared))
        assertNull(SharedPathPolicy.legacySegments(dcim, "Camera/a\u0000b", shared))
        assertNull(SharedPathPolicy.legacySegments(dcim, "a".repeat(SharedPathPolicy.MAX_SEGMENT_LENGTH + 1), shared))
        assertEquals(
            listOf("a".repeat(SharedPathPolicy.MAX_SEGMENT_LENGTH)),
            SharedPathPolicy.legacySegments(dcim, "a".repeat(SharedPathPolicy.MAX_SEGMENT_LENGTH), shared),
        )
    }

    @Test
    fun isSafeName_refusesWhatCannotBeOneName() {
        assertTrue(SharedPathPolicy.isSafeName("report.pdf"))
        assertTrue(SharedPathPolicy.isSafeName("..hidden"))
        assertFalse(SharedPathPolicy.isSafeName(""))
        assertFalse(SharedPathPolicy.isSafeName("   "))
        assertFalse(SharedPathPolicy.isSafeName(".."))
        assertFalse(SharedPathPolicy.isSafeName("a/b"))
        assertFalse(SharedPathPolicy.isSafeName("a\\b"))
    }

    @Test
    fun allowedRootIds_defaultAdmitsSharedFoldersAndTheWholeDeviceFolderOnlyWhileGranted() {
        val provider = object : SharedRootsProvider {
            var granted = false
            override fun sharedRoots() = listOf(RootDescriptor(dcim, "DCIM", true, true, true, true, false))
            override fun fullBrowseVolumes() =
                if (granted) listOf(VolumeDescriptor("tree:all", "Device", "tree:all", 0, 0, "root")) else emptyList()
            override fun isFullBrowseGranted() = granted
        }

        assertEquals(setOf(dcim), provider.allowedRootIds())
        provider.granted = true
        assertEquals(setOf(dcim, "tree:all"), provider.allowedRootIds())
    }

    // ── The v3 resolver's walk (SafFileSystemFacade.resolve) ─────────────────────

    @Test
    fun resolveSegments_aSharedRootAndAPlainPath_resolveToTheirNames() {
        assertEquals(listOf("Camera", "IMG_1.jpg"), SharedPathPolicy.resolveSegments(dcim, "Camera/IMG_1.jpg", shared))
        assertEquals(emptyList<String>(), SharedPathPolicy.resolveSegments(dcim, "", shared))
        assertEquals(emptyList<String>(), SharedPathPolicy.resolveSegments(dcim, "/", shared))
        assertEquals(emptyList<String>(), SharedPathPolicy.resolveSegments(dcim, null, shared))
    }

    @Test
    fun resolveSegments_skipsEmptyNamesAsTheResolverAlwaysDid() {
        assertEquals(listOf("Camera", "IMG_1.jpg"), SharedPathPolicy.resolveSegments(dcim, "/Camera//IMG_1.jpg/", shared))
    }

    @Test
    fun resolveSegments_refusesARootThatIsNotSharedRightNow() {
        assertNull(SharedPathPolicy.resolveSegments("content://com.android.externalstorage.documents/tree/primary%3A", "x", shared))
        assertNull(SharedPathPolicy.resolveSegments(null, "x", shared))
        assertNull(SharedPathPolicy.resolveSegments("", "x", shared))
        assertNull(SharedPathPolicy.resolveSegments(dcim, "x", emptySet()))
    }

    @Test
    fun theNameCap_countsUtf8Bytes_notCharacters() {
        // NAME_MAX limits BYTES: 127 two-byte characters are 254 bytes, 128 are 256.
        assertTrue(SharedPathPolicy.isSafeName("é".repeat(127)))
        assertFalse(SharedPathPolicy.isSafeName("é".repeat(128)))
        assertTrue(SharedPathPolicy.isSafeName("x".repeat(SharedPathPolicy.MAX_SEGMENT_LENGTH)))
        assertFalse(SharedPathPolicy.isSafeName("x".repeat(SharedPathPolicy.MAX_SEGMENT_LENGTH + 1)))
    }

    @Test
    fun aNewName_refusesInvisibleDirectionChangingAndControlCharacters() {
        for (name in listOf("evil‮fdp.exe", "a​b", "x⁦y", "a\u0007b", "a\u0085b")) {
            assertFalse("'$name' must not be accepted as a NEW name", SharedPathPolicy.isSafeNewName(name))
        }
        assertTrue(SharedPathPolicy.isSafeNewName("Holiday photos (2026).jpg"))
        assertTrue(SharedPathPolicy.isSafeNewName("été 日本.txt"))
    }

    @Test
    fun anExistingName_withAJoiner_isStillResolvable() {
        // isSafeName is what browsing and deleting use; it must not strand a file whose name holds U+200D.
        assertTrue(SharedPathPolicy.isSafeName("family‍.txt"))
        assertFalse(SharedPathPolicy.isSafeNewName("family‍.txt"))
    }

    @Test
    fun resolveSegments_refusesEveryUnsafeName() {
        for (path in listOf(
            ".", "..", "Camera/..", "Camera/../Download", "./Camera", "Camera/./IMG_1.jpg",
            "Camera\\..\\x", "nul\u0000byte", "Camera/" + "x".repeat(SharedPathPolicy.MAX_SEGMENT_LENGTH + 1),
        )) {
            assertNull("'$path' must be refused", SharedPathPolicy.resolveSegments(dcim, path, shared))
        }
    }
}
