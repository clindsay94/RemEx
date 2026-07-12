package com.clindsay94.remex.share

import org.junit.Assert.assertEquals
import org.junit.Test

/** Verifies MIME inference used for open-after-download ACTION_VIEW (plan WP8). */
class ShareMimeInferenceTest {

    @Test
    fun infers_common_types_from_extension() {
        assertEquals("image/jpeg", ShareMimeInference.inferMimeType("photo.jpg"))
        assertEquals("image/jpeg", ShareMimeInference.inferMimeType("photo.jpeg"))
        assertEquals("image/png", ShareMimeInference.inferMimeType("diagram.png"))
        assertEquals("application/pdf", ShareMimeInference.inferMimeType("report.pdf"))
        assertEquals("video/mp4", ShareMimeInference.inferMimeType("clip.mp4"))
        assertEquals("audio/mpeg", ShareMimeInference.inferMimeType("song.mp3"))
        assertEquals("application/zip", ShareMimeInference.inferMimeType("bundle.zip"))
        assertEquals(
            "application/vnd.android.package-archive",
            ShareMimeInference.inferMimeType("app.apk"),
        )
    }

    @Test
    fun extension_match_is_case_insensitive() {
        assertEquals("image/jpeg", ShareMimeInference.inferMimeType("PHOTO.JPG"))
        assertEquals("video/mp4", ShareMimeInference.inferMimeType("Movie.MP4"))
    }

    @Test
    fun uses_last_extension_for_multi_dot_names() {
        // archive.tar.gz -> gz -> gzip (not tar)
        assertEquals("application/gzip", ShareMimeInference.inferMimeType("archive.tar.gz"))
    }

    @Test
    fun unknown_or_missing_extension_falls_back() {
        assertEquals(ShareMimeInference.FALLBACK, ShareMimeInference.inferMimeType("mystery.qzx"))
        assertEquals(ShareMimeInference.FALLBACK, ShareMimeInference.inferMimeType("README"))
        assertEquals(ShareMimeInference.FALLBACK, ShareMimeInference.inferMimeType("trailingdot."))
    }

    @Test
    fun extensionOf_extracts_lowercased_suffix() {
        assertEquals("c", ShareMimeInference.extensionOf("a.b.c"))
        assertEquals("txt", ShareMimeInference.extensionOf("Notes.TXT"))
        assertEquals("", ShareMimeInference.extensionOf("noextension"))
        assertEquals("", ShareMimeInference.extensionOf("endsWithDot."))
    }
}
