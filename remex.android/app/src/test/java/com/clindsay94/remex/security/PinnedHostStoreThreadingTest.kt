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
        // a guard that says "at least nine" happily guards nine of ten. Eleven since RemEx-v3bd:
        // aead() became suspend when its monitor became a Mutex, so it is now one of the functions
        // this loop checks rather than something checked separately.
        assertEquals(
            "the store's suspend function count changed. If that is deliberate, update this number " +
                "— it is what stops a function being deleted or added without a hop.",
            11,
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
    fun `the keyset build hops for itself, behind a suspending lock`() {
        // aead() is the blocking part, and since RemEx-v3bd it hops on its own account instead of
        // inheriting whichever dispatcher its caller was on. Two properties hold that together and
        // both are asserted here: it stays suspend, and its initialisation stays behind the
        // coroutine Mutex rather than a monitor. Those are the same property from two sides —
        // `synchronized` cannot contain a suspend call, so a monitor here forces the DataStore
        // clear back into runBlocking, which is exactly the shape this bead removed.
        //
        // The per-call-site loop this test used to run is gone with the premise it rested on: the
        // dispatcher is no longer decided by the caller. `every suspend function moves itself to a
        // background dispatcher` still covers the functions that call aead(), and now covers aead()
        // itself. No count of those callers is given here on purpose — the comment this replaced
        // was written because an earlier one enumerated callers the code never checked, and the
        // first draft of this one promptly said "ten", which was the store's suspend-function count
        // BEFORE this bead — eleven now, per the assertion above — and not its aead() caller count
        // (five, at the time of writing).
        val code = code()

        val declarations = declarationsIn(code)
        val index = declarations.indexOfFirst { it.value.endsWith(" aead") }
        assertTrue("expected to find aead() declared", index >= 0)

        assertTrue(
            "aead() must stay suspend. The corrupted-keyset recovery inside it clears a DataStore, " +
                "and a non-suspend aead() can only reach that by parking a thread in runBlocking " +
                "(RemEx-v3bd).",
            declarations[index].value.contains("suspend fun"),
        )

        assertTrue(
            "aead() must hop to Dispatchers.IO in its own declaration. It is the only genuinely " +
                "blocking work in this file — an Android Keystore round trip, and a key GENERATION " +
                "in the TEE on first run — and where that lands must not depend on which caller " +
                "reached it first (RemEx-7257).",
            Regex("""withContext(<\w+>)?\(\s*Dispatchers\.IO\s*\)""")
                .containsMatchIn(headerOf(code, declarations, index)),
        )

        assertFalse(
            "the AEAD initialisation must stay behind the coroutine Mutex. `synchronized` cannot " +
                "hold a suspend call, so reintroducing one means the DataStore clear went back to " +
                "runBlocking.",
            "synchronized" in code,
        )

        // ANCHORED TO aead()'s OWN REGION, because the file-wide `"withLock" in code` this started
        // as is satisfied by a Mutex guarding anything else anywhere in the file — while its message
        // claimed something guards the KEYSET BUILD. Same bound as headerOf uses: this declaration
        // to the next one.
        val aeadBody =
            code.substring(
                declarations[index].range.first,
                declarations.getOrNull(index + 1)?.range?.first ?: code.length,
            )
        assertTrue(
            "the keyset build must stay behind aeadMutex.withLock. Without it the assertion above " +
                "only proves the monitor is gone, not that anything guards the build — and two " +
                "coroutines racing a corrupt keyset would each wipe the DataStore.",
            Regex("""aeadMutex\.withLock""").containsMatchIn(aeadBody),
        )
        assertTrue("expected the Mutex it locks to still be declared", "Mutex()" in code)

        // THE TWO INVARIANTS THE BEAD'S "care needed" CLAUSE NAMED, both of which compile away
        // silently if deleted and neither of which any assertion above reaches. Review asked for
        // these, and it was right to: this bead rewrote the file that guards this class and
        // introduced a new invariant in the same commit, which is exactly when a guard gets
        // forgotten.
        //
        // NonCancellable first. The SharedPreferences clear is not suspending and lands at once;
        // the DataStore clear is the first suspension point after it. Cancel between them — a
        // viewModelScope dying on navigation will do — and the keyset is destroyed while the rows
        // encrypted under it survive. The next call then builds a fresh keyset SUCCESSFULLY, so the
        // recovery never runs again and those rows are undecryptable forever. Nothing throws and
        // nothing logs: every read treats an undecryptable row as absent. Silence is the whole
        // reason this is asserted rather than trusted.
        assertTrue(
            "the corrupted-keyset recovery must stay inside withContext(NonCancellable). Without " +
                "it a cancellation landing between the two clears destroys the keyset but not the " +
                "rows encrypted under it, and the recovery never fires again to finish the job.",
            Regex("""withContext\(\s*NonCancellable\s*\)""").containsMatchIn(aeadBody),
        )

        // And the second half of the double-check. The @Volatile read OUTSIDE the lock is a fast
        // path and losing it costs only speed; the read INSIDE it is a correctness property, and it
        // is the one that keeps the recovery once-per-corruption. Delete it and three callers racing
        // a corrupt keyset each take the lock in turn and each wipe the DataStore.
        assertTrue(
            "the second aeadInstance read, inside the lock, is what makes the recovery run once " +
                "per corruption rather than once per waiter. Without it every coroutine queued on " +
                "the mutex rebuilds — and on the recovery path, re-wipes.",
            Regex("""withLock\s*\{\s*aeadInstance\s*\?:""").containsMatchIn(aeadBody),
        )
    }

    @Test
    fun `no runBlocking survives anywhere in the store`() {
        // This replaces `runBlocking stays confined to the corrupted-keyset recovery`, which guarded
        // the single one that used to sit in aead()'s catch. A `synchronized` block cannot suspend,
        // so clearing the unreadable DataStore had no other shape; the Mutex gave it one, and the
        // runBlocking is gone rather than relocated (RemEx-v3bd). That left the confinement guard
        // asserting that a thing still exists which no longer does — its own comment said to delete
        // it in exactly that event. What is worth guarding now is stronger and simpler.
        val code = code()

        val uses = Regex("""\brunBlocking\s*[({]""").findAll(code).map { it.value }.toList()
        assertEquals(
            "runBlocking parks whatever thread reaches it for as long as the work takes. Nothing " +
                "here is a non-suspend context that has to invoke suspending work: every function " +
                "that reaches a suspending call is itself suspend, and the one lock in this file " +
                "suspends too. (Not 'every function that touches I/O' — buildAead is non-suspend " +
                "and does a SharedPreferences read and a Keystore round trip. It needs no " +
                "runBlocking because nothing it calls suspends, which is the distinction that " +
                "actually carries this guard.)",
            emptyList<String>(),
            uses,
        )
    }

    /**
     * The corrupted-keyset recovery clears BOTH encrypted stores (RemEx-oxcu).
     *
     * It cleared only `pinnedHostDataStore`, leaving the reconnect secrets behind holding PAIR-1
     * material encrypted under the keyset just destroyed. That was survivable, but only by accident
     * of encryption: no surviving key can decrypt them, so they read back as null and force a
     * re-pair. Nothing broke and nothing said why — and `REGRESSION-GUARDS.md` already described the
     * intended behaviour, so the code was what had drifted.
     *
     * A source-text check for the same reason as the guards above: every function here takes a
     * `Context` and reaches the Android Keystore, and this project has no Robolectric. Anchored on
     * the two store names rather than on a line number, which the bead noted had already moved once.
     */
    @Test
    fun `the corrupted keyset recovery clears both encrypted stores`() {
        val code = storeSource()
        val recovery =
            code.substringAfter("Clearing corrupted keyset and retrying", "")
                .substringBefore("// Retry initialization", "")

        assertTrue("the recovery block moved; re-anchor this guard", recovery.isNotBlank())
        assertTrue(
            "the recovery must clear the pinned-host store",
            recovery.contains("pinnedHostDataStore.edit { it.clear() }"),
        )
        assertTrue(
            "the recovery must clear the reconnect secrets too — they are encrypted under the " +
                "keyset it just destroyed, and leaving them is unreadable residue (RemEx-oxcu)",
            recovery.contains("reconnectSecretDataStore.edit { it.clear() }"),
        )
    }
}
