package com.clindsay94.remex

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Each notification channel is created in exactly one place (RemEx-9gbzc).
 *
 * `remex_file_transfer` was declared twice, identically, by FileTransferNotificationManager and
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
        // every registration as a second construction and reported eight where there are three. Only
        // the constructor declares a channel's properties; the create call just hands it over.
        val constructor = Regex("""(?<!create)NotificationChannel\(""")

        val constructions = mainSources().sumOf { file ->
            constructor.findAll(file.readText()).count()
        }

        val declaringFiles = mainSources().filter {
            constructor.containsMatchIn(it.readText())
        }.map { it.name }.sorted()

        // Three channels, three constructions: connection, file transfer, file consent.
        assertEquals(
            "each channel should be constructed once; found constructions in $declaringFiles",
            3,
            constructions
        )
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
            !Regex("""(?<!create)NotificationChannel\(""").containsMatchIn(jobService)
        )
    }
}
