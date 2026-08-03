package com.clindsay94.remex.security

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards that the pinned-host store keeps its blocking work off the caller's thread (RemEx-7257).
 *
 * A source-text check, because the thing under test cannot be constructed here: every function takes
 * a `Context` and reaches the Android Keystore, and this project has no Robolectric. The same
 * technique guards the native wrappers in `SendCommandThreadingTest`, for the same reason and against
 * the same failure.
 *
 * **WHAT ACTUALLY BLOCKS, SO THIS DOES NOT OVERCLAIM.** DataStore already performs its file I/O on
 * its own IO scope, so `data.first()` and `edit {}` never blocked a caller. The blocking work is
 * `aead()` — an `AndroidKeysetManager` build: a SharedPreferences read, an Android Keystore round
 * trip, and on first run a key generation in the TEE. `RemexClientManager` calls `getPin` from a
 * `Dispatchers.Main` scope on every Connect tap, which is how that reached the UI thread.
 *
 * Invisible at build time, which is the whole reason for this file: deleting a `withContext` compiles
 * everywhere, every caller keeps working, and the cost only shows up as jank on a cold start or a
 * slow device — the one machine no test here runs on.
 */
class PinnedHostStoreThreadingTest {

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    private fun storeSource(): String {
        val file =
            File(
                repoRoot(),
                "remex.android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt",
            )
        assertTrue("expected PinnedHostStore at ${file.path}", file.isFile)
        return file.readText().replace("\r\n", "\n")
    }

    /** Every function declaration in the file, in source order. */
    private fun declarationsIn(code: String) =
        Regex("""(?:private )?(?:suspend )?fun \w+""").findAll(code).toList()

    /**
     * The text of one declaration up to its opening brace, BOUNDED BY THE NEXT DECLARATION.
     *
     * The bound is the whole point. An unbounded `substringBefore('{')` runs past a brace-less
     * expression body into the NEXT function, so a declaration with no hop passes on its neighbour's
     * `withContext`. Review found that by writing exactly such a mutation — a one-line
     * `= dataStore.data.first()[key]` with no braces — and watching both guards stay green.
     */
    private fun headerOf(code: String, declarations: List<MatchResult>, index: Int): String {
        val start = declarations[index].range.first
        val end = declarations.getOrNull(index + 1)?.range?.first ?: code.length
        val region = code.substring(start, end)
        return region.substringBefore('{', region)
    }

    /** [storeSource] with comments removed, so prose about the rule cannot satisfy it. */
    private fun code(): String =
        storeSource()
            .replace(Regex("""/\*[\s\S]*?\*/"""), "")
            .replace(Regex("""//.*"""), "")

    @Test
    fun `every suspend function moves itself to a background dispatcher`() {
        // EVERY one, not a chosen few. The store is reached from pairing, connection, file transfer
        // and three screens, and the functions call each other — forgetHost alone fans out to
        // listPaired, removePin, removeReconnectSecret and recordedAliases. One function left on the
        // caller's thread is enough to put a Keystore round trip back on the UI thread, and which
        // one it is depends on a call graph nobody re-checks.
        val code = code()

        val all = declarationsIn(code)
        val suspendIndices = all.indices.filter { all[it].value.contains("suspend fun") }

        // Exactly the count there is, not a floor loose enough to let one be deleted silently:
        // a guard that says "at least nine" happily guards nine of ten.
        assertEquals(
            "the store's suspend function count changed. If that is deliberate, update this number " +
                "— it is what stops a function being deleted or added without a hop.",
            10,
            suspendIndices.size,
        )

        for (index in suspendIndices) {
            val name = all[index].value.substringAfterLast(' ')
            val header = headerOf(code, all, index)

            assertTrue(
                "PinnedHostStore.$name must hop to Dispatchers.IO in its own declaration. Without " +
                    "it the function inherits the caller's dispatcher, and RemexClientManager calls " +
                    "this store from a Dispatchers.Main scope on every Connect tap (RemEx-7257).",
                Regex("""withContext(<\w+>)?\(\s*Dispatchers\.IO\s*\)""").containsMatchIn(header),
            )
        }
    }

    @Test
    fun `the keyset build is never reached without that hop`() {
        // aead() is the blocking part and it is NOT suspend — it cannot be, because it runs inside a
        // `synchronized` block and Kotlin forbids suspending there. So the dispatcher it lands on is
        // decided entirely by its callers, and this asserts that each CALL SITE sits in a function
        // that hops. It does not enumerate the callers — a claim an earlier version of this comment
        // made and the code never supported.
        val code = code()

        val callers =
            Regex("""(?:private )?(suspend )?fun (\w+)[^\n]*\n""").findAll(code)
                .map { it.groupValues[2] to (it.groupValues[1].isNotBlank()) }
                .toMap()

        assertTrue("expected to find aead() declared", "aead" in callers)
        assertFalse(
            "aead() must stay non-suspend — it is called from inside a synchronized block, which " +
                "cannot suspend. If this ever becomes suspend, the lock has been restructured and " +
                "this whole file needs rereading.",
            callers["aead"] == true,
        )

        // Every use of aead(...) must sit inside a function that hops — which means finding the
        // ENCLOSING declaration, not merely some hopping declaration that happens to appear earlier
        // in the file. The first version of this test did the latter and passed while a function
        // that calls aead() had its hop removed; the mutation is what showed it up.
        val declarations = declarationsIn(code)

        // MATCHES ANY ARGUMENT NAME, and asserts there are some. Pinned to `aead(context)` this
        // loop emptied itself the moment the parameter was renamed, and an empty loop asserts
        // nothing while reporting success.
        val uses = Regex("""\baead\(\w+\)""").findAll(code).toList()
        assertTrue(
            "expected aead(...) call sites to check — an empty loop here passes without testing " +
                "anything, which is how this guard could be neutralised by a rename.",
            uses.size >= 5,
        )

        for (use in uses) {
            val index = declarations.indexOfLast { it.range.first < use.range.first }
            assertTrue("expected a declaration before the aead call", index >= 0)

            val enclosing = declarations[index]
            val header = headerOf(code, declarations, index)
            assertTrue(
                "the aead(context) call in `${enclosing.value}` is not inside a function that hops " +
                    "to Dispatchers.IO. That puts an Android Keystore round trip — a key GENERATION " +
                    "on first run — on whatever thread called in (RemEx-7257).",
                Regex("""withContext(<\w+>)?\(\s*Dispatchers\.IO\s*\)""").containsMatchIn(header),
            )
        }
    }

    @Test
    fun `runBlocking stays confined to the corrupted-keyset recovery`() {
        // There is exactly one, inside aead()'s catch: a `synchronized` block cannot suspend, so
        // clearing the unreadable DataStore has no other shape. It is tolerable only because it is
        // reached when Tink or the Keystore is ALREADY corrupt, and because the hop above means the
        // thread it parks is an IO worker rather than the UI thread. A second one, somewhere
        // ordinary, would be neither.
        val code = code()

        val uses = Regex("""\brunBlocking\s*[({]""").findAll(code).toList()
        assertTrue(
            "expected the one recovery-path runBlocking to still be there — if it is gone, good, " +
                "but delete this test rather than leaving it asserting nothing.",
            uses.size == 1,
        )

        val recoveryLog = code.indexOf("Failed to initialize Tink AEAD")
        assertTrue("expected the recovery path's log line", recoveryLog > 0)

        // BOUNDED ON BOTH SIDES. "After the log line" is satisfied by the entire rest of the file,
        // so it caught a runBlocking being ADDED but not one being MOVED onto an ordinary path —
        // which is the guard's whole stated subject. aead()'s catch ends where buildAead begins.
        val aeadEnd = code.indexOf("private fun buildAead")
        assertTrue("expected buildAead to follow aead", aeadEnd > recoveryLog)
        assertTrue(
            "the only runBlocking must be the one in the corrupted-keyset recovery. A new one on an " +
                "ordinary path parks whatever thread reaches it, for as long as the work takes.",
            uses.single().range.first in (recoveryLog + 1) until aeadEnd,
        )
    }
}
