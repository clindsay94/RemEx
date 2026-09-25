package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit batch P3-B4 (P3-11 .. P3-15): Compose / Glance / pointer-input shapes.
 *
 * SOURCE-SCANNED, THE ESTABLISHED PRECEDENT FOR THIS MODULE (see
 * `AndroidFileTransferHostJobTrackingTest`, `SendDispatcherDeclarationOrderTest`): this module has no
 * Robolectric and no Compose UI test harness, so composition structure, Glance composition and the
 * pointer-input coroutine cannot be driven here. Each test pins the specific shape the fix put in
 * place, so a later edit that quietly reverts it fails loudly. P3-16 (decoder) is covered
 * behaviourally by `ui.screens.H264NalScanTest`.
 */
class PerfP3B4SourceShapeTest {

    private fun source(relative: String): String {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { generateSequence(it) { p -> p.parentFile }
                .firstOrNull { File(it, "remex.android").isDirectory } }
            ?: error("could not locate the repository root")
        val file = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/$relative")
        assertTrue("expected to find $relative at ${file.path}", file.isFile)
        return file.readText()
    }

    @Test
    fun `P3-11 the root theme has exactly one call site, so prefs arriving never tears the tree down`() {
        val src = source("MainActivity.kt")
        val start = src.indexOf("setContent {")
        assertTrue("setContent block not found", start >= 0)
        val end = src.indexOf("\n    }", start)
        val body = src.substring(start, end)

        assertEquals(
            "setContent must call RemExTheme exactly once; two branches (prefs vs null) put AppNavigation " +
                "under two call sites and the first DataStore emission rebuilt the whole tree",
            1,
            Regex("""RemExTheme\s*[({]""").findAll(body).count(),
        )
        assertEquals("AppNavigation must be composed from one call site", 1, Regex("""AppNavigation\(\)""").findAll(body).count())
        assertTrue("the null (not yet loaded) state must fall back to defaults, not a separate branch",
            body.contains("personalization ?: DefaultPersonalization"))
    }

    @Test
    fun `P3-12 widget icons decode lazily, not for every launcher entry at parse time`() {
        val src = source("widget/AppLauncherWidget.kt")
        val parseStart = src.indexOf("private fun parseLauncherEntries")
        assertTrue("parseLauncherEntries not found", parseStart >= 0)
        val parseBody = src.substring(parseStart, src.indexOf("\n    }", parseStart))
        assertFalse("parseLauncherEntries must not decode bitmaps (every entry, selected or not)",
            parseBody.contains("BitmapFactory") || parseBody.contains("Base64.decode"))
        assertTrue("WidgetAppEntry.icon should decode on first access", src.contains("val icon: Bitmap? by lazy"))
    }

    @Test
    fun `P3-13 thumbnails decode off the main thread through a cache that outlives the row`() {
        val src = source("ui/components/FileManagerFileItem.kt")
        val fnStart = src.indexOf("internal fun rememberThumbnail(")
        assertTrue("rememberThumbnail not found", fnStart >= 0)
        val fnBody = src.substring(fnStart, src.indexOf("\n}", fnStart))

        assertTrue("a miss must decode on a background dispatcher", fnBody.contains("withContext(Dispatchers.Default)"))
        assertTrue("a hit must be served from the cross-composition cache", fnBody.contains("ThumbnailBitmapCache.get("))
        assertFalse("decoding inside remember { } runs on the main thread during composition",
            Regex("""remember\s*\(""").containsMatchIn(fnBody))
        assertTrue("the cache must be byte-bounded", src.contains("LruCache<String, ImageBitmap>"))
    }

    @Test
    fun `P3-14 inertia sends on the live move cadence, not every 16 ms`() {
        val src = source("ui/screens/RemoteDesktopScreen.kt")
        assertTrue("INERTIA_FRAME_MS must equal MOVE_THROTTLE_MS",
            src.contains("private const val INERTIA_FRAME_MS = MOVE_THROTTLE_MS"))
        assertFalse("the old 16 ms send interval is back",
            Regex("""INERTIA_FRAME_MS\s*=\s*16L""").containsMatchIn(src))
        assertTrue("each inertia send must coalesce its physics sub-frames",
            src.contains("for (sub in 0 until INERTIA_SUBFRAMES_PER_SEND)"))
    }

    @Test
    fun `P3-15 the gesture loop does not allocate a filtered pointer list per event`() {
        val src = source("ui/screens/RemoteDesktopScreen.kt")
        val loop = src.indexOf("awaitPointerEvent()", src.indexOf("val recentDeltas"))
        assertTrue("gesture loop not found", loop >= 0)
        val window = src.substring(loop, loop + 2500)
        assertFalse("event.changes.filter { it.pressed } allocates a list on every touch event",
            Regex("""event\.changes\s*\.filter""").containsMatchIn(window))
        assertTrue("pressed pointers should be counted with an index loop",
            window.contains("for (ci in changes.indices)"))
    }
}
