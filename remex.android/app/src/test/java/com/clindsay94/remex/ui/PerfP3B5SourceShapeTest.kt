package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit batch P3-B5 (P3-17 .. P3-21): Compose recomposition-scope shapes.
 *
 * SOURCE-SCANNED, THE ESTABLISHED PRECEDENT FOR THIS MODULE (see `PerfP3B4SourceShapeTest`,
 * `AndroidFileTransferHostJobTrackingTest`): this module has no Robolectric and no Compose UI
 * test harness, so actual recomposition counts cannot be measured here. Each test pins the
 * specific shape the fix put in place — a shared decode cache, a hot value moved out of a
 * composable's own body and into a graphicsLayer/leaf-composable boundary, or a poll call site —
 * so a later edit that quietly reverts the shape fails loudly.
 */
class PerfP3B5SourceShapeTest {

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
    fun `P3-17 the grid and the recent carousel share one decoded icon cache`() {
        val src = source("ui/screens/AppLauncherScreen.kt")
        assertTrue("a byte-bounded icon cache must exist", src.contains("LruCache<String, Bitmap>"))
        val fnStart = src.indexOf("private fun rememberAppIconBitmap(")
        assertTrue("rememberAppIconBitmap not found", fnStart >= 0)
        val fnBody = src.substring(fnStart, src.indexOf("\n}", fnStart))
        assertTrue("a cache hit must return before decoding again",
            fnBody.contains("AppIconBitmapCache.get("))
        assertTrue("a miss must decode off the main thread",
            fnBody.contains("withContext(Dispatchers.Default)"))
        assertTrue("a fresh decode must be stored back into the shared cache",
            fnBody.contains("AppIconBitmapCache.put("))
    }

    @Test
    fun `P3-18 the mini-player's ticking progress lives in its own leaf composable`() {
        val src = source("ui/components/MediaMiniPlayer.kt")
        val fnStart = src.indexOf("fun MediaMiniPlayer(")
        assertTrue("MediaMiniPlayer not found", fnStart >= 0)
        val fnBody = src.substring(fnStart, src.indexOf("\n}", fnStart))
        assertFalse("the once-a-second tick must not live in MediaMiniPlayer's own body " +
            "(it used to force the whole bar to recompose every second while playing)",
            fnBody.contains("var progress by remember"))
        assertTrue("the bar must delegate the ticking progress to its own leaf composable",
            fnBody.contains("MiniPlayerWavyProgress("))

        val leafStart = src.indexOf("private fun MiniPlayerWavyProgress(")
        assertTrue("MiniPlayerWavyProgress not found", leafStart >= 0)
        val leafBody = src.substring(leafStart, src.indexOf("\n}", leafStart))
        assertTrue("the leaf must own the ticking state", leafBody.contains("var progress by remember"))
    }

    @Test
    fun `P3-18 the now-playing sheet's ticking seek row lives in its own leaf composable`() {
        val src = source("ui/components/MediaNowPlayingSheet.kt")
        val fnStart = src.indexOf("fun MediaNowPlayingSheet(")
        assertTrue("MediaNowPlayingSheet not found", fnStart >= 0)
        val fnBody = src.substring(fnStart, src.indexOf("\n}", fnStart))
        assertFalse("the once-a-second tick must not live in MediaNowPlayingSheet's own body " +
            "(it used to force artwork/title/artist/MediaControlSection to recompose every second)",
            fnBody.contains("var nowElapsedMs by remember"))
        assertTrue("the sheet must delegate the seek row to its own leaf composable",
            fnBody.contains("SeekSection("))

        val leafStart = src.indexOf("private fun SeekSection(")
        assertTrue("SeekSection not found", leafStart >= 0)
        val leafBody = src.substring(leafStart, src.indexOf("\n}", leafStart))
        assertTrue("the leaf must own the ticking state", leafBody.contains("var nowElapsedMs by remember"))
    }

    @Test
    fun `P3-19 the tutorial pager reads currentPageOffsetFraction only inside graphicsLayer`() {
        val src = source("ui/screens/TutorialScreen.kt")
        val pagerStart = src.indexOf("HorizontalPager(")
        assertTrue("HorizontalPager not found", pagerStart >= 0)
        val graphicsLayerStart = src.indexOf("graphicsLayer {", pagerStart)
        assertTrue("graphicsLayer block not found", graphicsLayerStart >= 0)
        val graphicsLayerEnd = src.indexOf("\n                }", graphicsLayerStart)
        assertTrue("graphicsLayer block's closing brace not found", graphicsLayerEnd > graphicsLayerStart)
        // Search from the *code*, not the explanatory comment above it, which itself names
        // currentPageOffsetFraction and would otherwise satisfy this by accident.
        val offsetStart = src.indexOf("currentPageOffsetFraction", graphicsLayerStart)
        assertTrue("currentPageOffsetFraction read not found inside the graphicsLayer lambda",
            offsetStart in graphicsLayerStart..graphicsLayerEnd)
        assertFalse("a page-offset val must not be computed outside graphicsLayer any more",
            Regex("""val pageOffset =\s*\n\s*\(pagerState\.currentPage - page\)\s*\+\s*pagerState\.currentPageOffsetFraction\s*\n\s*TutorialPageContent""")
                .containsMatchIn(src))
    }

    @Test
    fun `P3-20 the coach overlay's selection-ring alpha is read inside graphicsLayer, not fed into border()`() {
        val src = source("ui/screens/DashboardCoachOverlay.kt")
        assertFalse("an Animatable's .value must not be passed straight into .border()'s color " +
            "(that argument is evaluated at composition time, forcing a recompose every frame)",
            src.contains(".copy(alpha = selected)") || src.contains(".copy(alpha = sel.value)"))

        val cardStart = src.indexOf("private fun androidx.compose.foundation.layout.BoxScope.MiniSelectCard(")
        assertTrue("MiniSelectCard not found", cardStart >= 0)
        val cardSignatureEnd = src.indexOf(") {", cardStart)
        val signature = src.substring(cardStart, cardSignatureEnd)
        assertTrue("MiniSelectCard must take the raw Animatables, not their dereferenced .value, " +
            "so its caller's composition body never reads a per-frame value",
            signature.contains("selected: Animatable<Float") &&
                signature.contains("lift: Animatable<Float") &&
                signature.contains("dragFraction: Animatable<Float"))

        val groupSelectStart = src.indexOf("private fun GroupSelectDemo(")
        assertTrue("GroupSelectDemo not found", groupSelectStart >= 0)
        val groupSelectBody = src.substring(groupSelectStart, src.indexOf("\n}", groupSelectStart))
        assertFalse("GroupSelectDemo's own body must not dereference the Animatables it hands to MiniSelectCard",
            Regex("""MiniSelectCard\([^)]*\.value""").containsMatchIn(groupSelectBody))
    }

    @Test
    fun `P3-21 the background auto-poll refreshes silently, not through the visible spinner`() {
        val src = source("ui/screens/TaskManagerViewModel.kt")
        assertTrue("refreshProcesses must accept a showSpinner flag defaulting to true " +
            "(user-initiated and initial-load refreshes still show the spinner)",
            src.contains("fun refreshProcesses(showSpinner: Boolean = true)"))

        val autoRefreshStart = src.indexOf("private fun startAutoRefresh()")
        assertTrue("startAutoRefresh not found", autoRefreshStart >= 0)
        val autoRefreshBody = src.substring(autoRefreshStart, src.indexOf("\n    }", src.indexOf("while (isActive)", autoRefreshStart)))
        assertTrue("the steady-state 4s poll must call refreshProcesses(showSpinner = false), " +
            "so it can't animate the pull-to-refresh indicator while the user isn't asking for a refresh",
            autoRefreshBody.contains("refreshProcesses(showSpinner = false)"))

        val initialLoadWindow = src.substring(autoRefreshStart, autoRefreshStart + 400)
        assertTrue("the initial two-shot load must still default to showing the spinner",
            Regex("""delay\(500\)\s*\n\s*refreshProcesses\(\)""").containsMatchIn(initialLoadWindow))
    }
}
