package com.clindsay94.remex

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Regression coverage for review-report.md Finding 3 ("foreground service tears itself
 * down on transient disconnects"). The report claimed [com.clindsay94.remex.service.RemexConnectionService]
 * at lines 67-78 called `stopSelf()` whenever `RemexClientManager.isConnected` flipped to
 * false. Verification against the current code (HEAD c79e2d4) showed that block only
 * updates the notification — `stopSelf` is only invoked when (a) the host setting goes
 * blank or (b) foreground-service startup itself fails.
 *
 * Robolectric isn't a dependency on this project and we don't want to add it just for
 * this check. Instead we treat the service as a contract surface and assert structural
 * properties of the source file. Any future edit that introduces `stopSelf()` inside
 * the `isConnected` collector — re-creating the bug described by the report — will fail
 * this test immediately.
 */
class RemexConnectionServiceContractTests {

    private val serviceSource: String by lazy {
        // Unit tests run with the module root (RemEx.Android/app) as the working directory.
        val file = File("src/main/java/com/clindsay94/remex/service/RemexConnectionService.kt")
        assertTrue(
            "Could not locate RemexConnectionService.kt at ${file.absolutePath} — test setup is broken, not the contract.",
            file.exists()
        )
        file.readText()
    }

    @Test
    fun `isConnected collector never calls stopSelf`() {
        // The notification-update block subscribes to the connection state flow and must
        // only mutate the notification — it must NEVER tear the service down on a transient
        // disconnect, otherwise the foreground service can't host the reconnect window.
        val collectorBlock = extractCollectorBlock(
            sentinel = "RemexClientManager.isConnected.collect"
        )

        assertFalse(
            "RemexConnectionService.notificationJob (the isConnected watcher) must not call " +
                "stopSelf(). A regression here re-introduces review-report.md Finding 3: the " +
                "service tears itself down on the first disconnect, breaking auto-reconnect. " +
                "Block contents:\n$collectorBlock",
            collectorBlock.contains("stopSelf")
        )
    }

    @Test
    fun `host blank check is the only legitimate stopSelf trigger in steady state`() {
        // Document the intended behavior: stopSelf is allowed when the user has cleared the
        // configured host (signed out / unpaired), and when foreground startup itself failed
        // (SecurityException, generic Exception). Anywhere else is a regression.
        val hostMonitorBlock = extractCollectorBlock(
            sentinel = "settingsManager.hostFlow.collect"
        )
        assertTrue(
            "host monitor block should call stopSelf when host is blank — that's the user " +
                "intentionally clearing their configured host. If this assertion fails, the " +
                "service has lost its legitimate teardown path. Block contents:\n$hostMonitorBlock",
            hostMonitorBlock.contains("stopSelf")
        )
    }

    @Test
    fun `service returns START_STICKY for system kill recovery`() {
        // START_STICKY is the platform contract for "restart me if you killed me." The
        // report's Finding 3 also flagged that force-stop can't be made transparent — true,
        // but START_STICKY is the right baseline for system-kill recovery. If the return
        // type ever changes to START_NOT_STICKY for non-error paths, we've lost the auto-
        // reconnect substrate.
        assertTrue(
            "RemexConnectionService.onStartCommand must return START_STICKY on the success " +
                "path so the OS restarts the service after a system kill. Removing this " +
                "re-introduces the brittleness described by review-report.md Finding 3.",
            serviceSource.contains("return START_STICKY")
        )
    }

    /**
     * Extracts the body of a Kotlin `collect {}` block following the [sentinel] string.
     * Returns the substring between the opening `{` after the sentinel and the matching
     * closing `}`, tracking brace depth to handle nested scopes.
     */
    private fun extractCollectorBlock(sentinel: String): String {
        val source = serviceSource
        val sentinelIndex = source.indexOf(sentinel)
        assertTrue(
            "Could not find sentinel '$sentinel' in RemexConnectionService.kt — the file " +
                "structure has changed and this contract test needs updating.",
            sentinelIndex >= 0
        )

        val braceStart = source.indexOf('{', startIndex = sentinelIndex)
        assertTrue(
            "Could not find opening brace after '$sentinel'.",
            braceStart >= 0
        )

        var depth = 1
        var index = braceStart + 1
        while (index < source.length && depth > 0) {
            when (source[index]) {
                '{' -> depth++
                '}' -> depth--
            }
            if (depth == 0) break
            index++
        }

        assertTrue(
            "Unbalanced braces while extracting block for sentinel '$sentinel'.",
            depth == 0
        )

        return source.substring(braceStart, index + 1)
    }
}
