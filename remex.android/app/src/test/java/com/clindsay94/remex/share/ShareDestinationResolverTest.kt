package com.clindsay94.remex.share

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Verifies destination resolution for share-to-PC push offers (plan WP8): name sanitisation defeats
 * path traversal + illegal characters, and path combination matches the file-manager semantics.
 */
class ShareDestinationResolverTest {

    private val fb = ShareDestinationResolver.DEFAULT_FALLBACK

    @Test
    fun sanitize_keeps_a_normal_name() {
        assertEquals("photo.jpg", ShareDestinationResolver.sanitizeFileName("photo.jpg"))
        assertEquals("my report.pdf", ShareDestinationResolver.sanitizeFileName("  my report.pdf  "))
        assertEquals(".gitignore", ShareDestinationResolver.sanitizeFileName(".gitignore"))
    }

    @Test
    fun sanitize_drops_directory_components() {
        assertEquals("passwd", ShareDestinationResolver.sanitizeFileName("../../etc/passwd"))
        assertEquals("c.dat", ShareDestinationResolver.sanitizeFileName("a\\b\\c.dat"))
    }

    @Test
    fun sanitize_strips_illegal_characters() {
        assertEquals("badname.txt", ShareDestinationResolver.sanitizeFileName("bad:name?.txt"))
        assertEquals("clean.bin", ShareDestinationResolver.sanitizeFileName("cl*ea|n.bin"))
    }

    @Test
    fun sanitize_falls_back_for_empty_null_or_all_dots() {
        assertEquals(fb, ShareDestinationResolver.sanitizeFileName(""))
        assertEquals(fb, ShareDestinationResolver.sanitizeFileName(null))
        assertEquals(fb, ShareDestinationResolver.sanitizeFileName("."))
        assertEquals(fb, ShareDestinationResolver.sanitizeFileName(".."))
        assertEquals("safe", ShareDestinationResolver.sanitizeFileName("..", "safe"))
    }

    @Test
    fun resolve_at_root_yields_bare_name() {
        assertEquals("photo.jpg", ShareDestinationResolver.resolveDestRelativePath("/", "photo.jpg"))
        assertEquals("photo.jpg", ShareDestinationResolver.resolveDestRelativePath("", "photo.jpg"))
    }

    @Test
    fun resolve_in_subfolder_joins_with_forward_slash() {
        assertEquals(
            "sub/dir/photo.jpg",
            ShareDestinationResolver.resolveDestRelativePath("sub/dir", "photo.jpg"),
        )
        // Trailing slash on the folder is normalised away.
        assertEquals(
            "sub/dir/photo.jpg",
            ShareDestinationResolver.resolveDestRelativePath("sub/dir/", "photo.jpg"),
        )
        // Backslash folder separators are normalised to forward slashes.
        assertEquals(
            "sub/dir/photo.jpg",
            ShareDestinationResolver.resolveDestRelativePath("sub\\dir", "photo.jpg"),
        )
    }

    @Test
    fun resolve_defeats_traversal_in_the_file_name() {
        assertEquals(
            "docs/secret.txt",
            ShareDestinationResolver.resolveDestRelativePath("docs", "../../secret.txt"),
        )
    }

    @Test
    fun resolve_preserves_multi_dot_extension() {
        assertEquals(
            "docs/archive.tar.gz",
            ShareDestinationResolver.resolveDestRelativePath("docs", "archive.tar.gz"),
        )
    }
}
