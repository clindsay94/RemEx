package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.components.TransferNotificationAsk
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * RemEx-pp4cm.8: a phone that never granted POST_NOTIFICATIONS got no transfer notification of any
 * kind, and a failed download posted none either, so "downloads show nothing" had no way out.
 */
class TransferNotificationsSourceShapeTest {

    @Before fun fresh() = TransferNotificationAsk.reset()

    private val root: File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: generateSequence(File(".").absoluteFile) { it.parentFile }
                .first { File(it, "remex.android").isDirectory }

    private fun source(relative: String): String =
        File(root, "remex.android/app/src/main/java/com/clindsay94/remex/$relative").readText()

    @Test
    fun thePermissionIsAskedOnceWhenMissing_neverWhenGranted() {
        assertFalse("already granted: nothing to ask", TransferNotificationAsk.shouldAsk(granted = true))
        assertTrue("missing: ask", TransferNotificationAsk.shouldAsk(granted = false))
        assertFalse("asked this run already: do not nag", TransferNotificationAsk.shouldAsk(granted = false))
    }

    @Test
    fun theFilesScreen_asksForTheNotificationPermission() {
        assertTrue(source("ui/screens/FileTransferScreen.kt").contains("AskForTransferNotifications()"))
    }

    @Test
    fun everyDownloadFailurePath_goesThroughTheAnnouncingHelper() {
        val engine = source("service/FileTransferEngine.kt")
        val helper = engine.substring(engine.indexOf("private fun abandonDownload("))
        assertTrue(helper.take(900).contains("file_transfer_notification_download_failed"))

        // The five places a download can end without a file: no channel, an exception, no ready,
        // a decline, a failed verification. None may go back to the silent cleanup.
        assertEquals(5, Regex("""abandonDownload\(t\)""").findAll(engine).count())
        val silent = Regex("""(?<!fun )discardEmptyDownloadTarget\(t\)""").findAll(engine).count()
        assertEquals("only abandonDownload may call the silent cleanup directly", 1, silent)
    }
}
