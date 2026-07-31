package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the declaration position of the `sendDispatcher` properties (RemEx-7rq3).
 *
 * Kotlin runs property initializers and `init` blocks in DECLARATION ORDER. `RemoteDesktopViewModel`
 * has an `init` block that reaches `requestDisplayCatalog()` synchronously — `viewModelScope` is
 * `Dispatchers.Main.immediate`, so its collector starts undispatched and runs inline in the
 * constructor, and a `StateFlow` hands over its current value before suspending. If `sendDispatcher`
 * is declared BELOW that `init`, it is still null when `launch(sendDispatcher)` is reached and the
 * screen dies with a null-parameter exception inside its own constructor, on the ordinary path of a
 * connected user opening Remote Desktop.
 *
 * This was a real defect in the first draft of RemEx-7rq3, caught in review. It is worth a test
 * because nothing else can see it: the old code used `Dispatchers.IO`, an object, so the hazard did
 * not exist; `assembleRelease` and `lintVitalRelease` both pass; and the failure is construction
 * order, which no compiler check models.
 *
 * A test that CONSTRUCTED the view model would be the direct check and is not available here —
 * `RemoteDesktopViewModel` is an `AndroidViewModel` needing a real `Application` and DataStore, and
 * this module has no Robolectric. Reading the source is the same technique
 * `PairingErrorCodesCoverageTest` and `ProcessKillErrorCodesCoverageTest` already use, and it fails
 * loudly rather than silently if the file moves.
 */
class SendDispatcherDeclarationOrderTest {

    private fun viewModelSource(name: String): Pair<String, String> {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { generateSequence(it) { p -> p.parentFile }
                .firstOrNull { File(it, "remex.android").isDirectory } }
            ?: error("could not locate the repository root")

        val file = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/$name.kt")
        assertTrue("expected to find $name at ${file.path}", file.isFile)
        return name to file.readText()
    }

    @Test
    fun `sendDispatcher is declared before any init block`() {
        for (name in listOf("RemoteDesktopViewModel", "RemoteControlViewModel")) {
            val (label, source) = viewModelSource(name)

            val declaration = source.indexOf("private val sendDispatcher")
            assertTrue("$label should declare a sendDispatcher", declaration >= 0)

            val init = Regex("""^\s*init\s*\{""", RegexOption.MULTILINE).find(source)?.range?.first
            if (init == null) {
                // No init block, so no initialization-order hazard to guard. RemoteControlViewModel is
                // in this position today; asserting anyway keeps it covered if one is ever added.
                continue
            }

            assertTrue(
                "$label declares sendDispatcher AFTER its init block. Kotlin initializes in " +
                    "declaration order, and init reaches a launch(sendDispatcher) synchronously, so " +
                    "the dispatcher is null there and the view model throws in its constructor. " +
                    "Move the declaration above init. (RemEx-7rq3)",
                declaration < init
            )
        }
    }

    @Test
    fun `every send in RemoteDesktopViewModel uses the serialised dispatcher`() {
        // The fix is only as good as its coverage. A new `launch(Dispatchers.IO)` carrying a send
        // would race the serialised ones and silently reopen the reordering the bead closed — a
        // modifier keyUp overtaking its keyDown leaves Ctrl physically held down on the user's PC.
        val (_, source) = viewModelSource("RemoteDesktopViewModel")

        // Scans CODE, not prose. Checking the raw text would flag the KDoc above the declaration,
        // which explains what Dispatchers.IO used to do here — a guard that fails on its own
        // documentation gets deleted rather than obeyed. Comments are stripped first, then any
        // remaining Dispatchers.IO that is not the one wrapped in limitedParallelism is a stray:
        // broader than checking a single launch form, since a send under a bare `launch { }` or
        // under Dispatchers.Default races the serialised ones just the same.
        val code = source
            .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
            .replace(Regex("""//.*"""), "")

        val strayIoUses = Regex("""Dispatchers\.IO""").findAll(code)
            .filterNot { code.startsWith("Dispatchers.IO.limitedParallelism(1)", it.range.first) }
            .count()

        assertTrue(
            "RemoteDesktopViewModel references Dispatchers.IO outside the sendDispatcher " +
                "declaration ($strayIoUses time(s)). A send dispatched there races the serialised " +
                "ones and silently reopens the reordering this bead closed — a modifier keyUp " +
                "overtaking its keyDown leaves Ctrl physically held down on the user's PC. " +
                "(RemEx-7rq3)",
            strayIoUses == 0
        )
    }
}
