package com.clindsay94.remex

import android.app.NotificationManager
import com.clindsay94.remex.service.FileTransferNotificationManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Each notification channel is created in exactly one place (RemEx-9gbzc).
 *
 * The transfer channel (now `remex_file_transfer_v2`) was declared twice, identically, by FileTransferNotificationManager and
 * FileTransferJobService.
 *
 * **IDENTICAL IS WHAT MADE IT DANGEROUS RATHER THAN UNTIDY.** `createNotificationChannel` cannot
 * raise the importance of a channel that already exists — Android ignores the change — so the first
 * declaration to run on a fresh install fixes it permanently. A later edit to only one of two copies
 * does nothing for existing users and picks non-deterministically for new ones, while looking
 * perfectly correct in review. RemEx-ttum item 1 proposes raising transfer alerts above
 * IMPORTANCE_LOW, which is exactly that edit.
 *
 * A source scan because the alternative is instrumenting NotificationManager on a device, which is
 * much heavier than the thing it guards. It counts CONSTRUCTIONS rather than mentions, so delegating
 * to the owner does not look like a second declaration.
 */
class NotificationChannelOwnershipTest {

    private fun mainSources(): List<java.io.File> {
        val roots = listOf(java.io.File("src/main/java"), java.io.File("app/src/main/java"))
        val root = roots.firstOrNull { it.isDirectory }
        assertTrue("Android main sources not found - tried " + roots.joinToString { it.path }, root != null)
        return root!!.walkTopDown().filter { it.isFile && it.extension == "kt" }.toList()
    }

    @Test
    fun `each channel id is constructed exactly once`() {
        // THE NEGATIVE LOOKBEHIND IS LOAD-BEARING, and its absence failed the first version of this
        // test: createNotificationChannel( CONTAINS NotificationChannel(, so a naive count scores
        // (deleteNotificationChannel( too, since RemEx-pp4cm.8 retired a channel id)
        // every registration as a second construction and reported eight where there are three. Only
        // the constructor declares a channel's properties; the create call just hands it over.
        val constructor = Regex("""(?<!create)(?<!delete)NotificationChannel\(""")

        val constructions = mainSources().sumOf { file ->
            constructor.findAll(file.readText()).count()
        }

        val declaringFiles = mainSources().filter {
            constructor.containsMatchIn(it.readText())
        }.map { it.name }.sorted()

        // Nine channels, nine constructions: connection, file transfer, file consent, the four
        // routine channels (progress, results, messages; RemEx-pp0rt.5; countdown, RemEx-pp0rt.17),
        // which RoutineNotifications.kt constructs one call each so this count keeps meaning "one
        // construction per channel", and the two PC alert channels (critical and warning, because
        // importance belongs to a channel; RemEx-pp4cm.12), constructed one call each in PcAlertNotifications.kt.
        assertEquals(
            "each channel should be constructed once; found constructions in $declaringFiles",
            9,
            constructions
        )
    }

    @Test
    fun `the transfer channel is a new id above low importance and never audible`() {
        // RemEx-pp4cm.8. The first channel, remex_file_transfer, was IMPORTANCE_LOW, which One UI
        // files under Silent with no status-bar icon: downloads WERE notified and people saw nothing.
        // Importance cannot be raised on an existing channel, so the fix is a new id. Reusing the old
        // id would silently do nothing for every existing install.
        assertNotEquals(
            "the id must differ from the retired LOW channel",
            FileTransferNotificationManager.LEGACY_CHANNEL_ID,
            FileTransferNotificationManager.CHANNEL_ID
        )
        assertEquals("remex_file_transfer", FileTransferNotificationManager.LEGACY_CHANNEL_ID)
        assertTrue(
            "transfer channel must be at least IMPORTANCE_DEFAULT",
            FileTransferNotificationManager.CHANNEL_IMPORTANCE >= NotificationManager.IMPORTANCE_DEFAULT
        )

        val source = mainSources().single { it.name == "FileTransferNotificationManager.kt" }.readText()
        assertTrue("channel must have no sound", source.contains("setSound(null, null)"))
        assertTrue("channel must not vibrate", source.contains("enableVibration(false)"))
        assertTrue(
            "the retired channel must be deleted",
            source.contains("deleteNotificationChannel(LEGACY_CHANNEL_ID)")
        )
    }

    @Test
    fun `the job service posts to the same transfer channel id`() {
        val jobService = mainSources().single { it.name == "FileTransferJobService.kt" }.readText()
        assertTrue(
            "FileTransferJobService must take its channel id from FileTransferNotificationManager",
            jobService.contains("CHANNEL_ID = FileTransferNotificationManager.CHANNEL_ID")
        )
        assertTrue(
            "no notification here may post to the retired channel id",
            !jobService.contains("\"remex_file_transfer\"")
        )
    }

    @Test
    fun `every transfer notification is silent so default importance never dings`() {
        val source = mainSources().single { it.name == "FileTransferNotificationManager.kt" }.readText()
        // Public-version builders carry no alert of their own, so they are excluded: they are built
        // INSIDE a parent that is silent.
        val posting = Regex("""NotificationCompat\.Builder\(context, CHANNEL_ID\)""").findAll(source).count()
        val silent = Regex("""\.setSilent\(true\)""").findAll(source).count()
        assertTrue("found no transfer notification builders - the scan is blind", posting > 0)
        // One builder is a nested public version, which is why the comparison is >= posting - 1.
        assertTrue("$silent setSilent calls for $posting builders", silent >= posting - 1)
    }

    @Test
    fun `the job service does not declare the transfer channel itself`() {
        // The specific regression, named. It delegated to the manager rather than keeping a copy, and
        // a future edit that "inlines it for clarity" is the thing this stops.
        val jobService = mainSources().single { it.name == "FileTransferJobService.kt" }.readText()

        assertTrue(
            "FileTransferJobService should ask FileTransferNotificationManager for the channel",
            jobService.contains("FileTransferNotificationManager.ensureTransferChannel")
        )
        assertTrue(
            "FileTransferJobService is constructing a NotificationChannel again",
            !Regex("""(?<!create)(?<!delete)NotificationChannel\(""").containsMatchIn(jobService)
        )
    }
}
