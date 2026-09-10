package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards properties of the native command path that no compiler can see.
 *
 * Two unrelated SUBJECTS live here, both source-text checks because the failures are invisible at
 * build time: that the blocking native calls stay off the main thread (RemEx-66rf, RemEx-52n0,
 * RemEx-uach), and that no Quick Settings tile routes a Wake-on-LAN through the PC (RemEx-wgpm).
 * They share a technique rather than a subject; if a third SUBJECT arrives, split them. The
 * threading subject has since grown from one function to five, which is the same subject and belongs
 * together — the pairing three were added when RemEx-uach found them blocking the UI thread.
 *
 * On the first:
 *
 * The native export now WAITS for the PC's real answer — up to ten seconds if the host has gone
 * quiet — where it used to fire the send into a discarded task and return "dispatched" instantly.
 * That made every existing call site a potential ANR, and there were more of them than anyone had in
 * mind: seven Quick Settings tiles called it straight out of `onClick`, which runs on the main thread.
 *
 * Making the function `suspend` is what turned those into compiler errors instead of field crashes,
 * and putting the `withContext` INSIDE it is what stops the next call site from having to remember.
 *
 * **NEITHER HALF IS PROTECTED BY THE COMPILER, WHICH IS THE WHOLE REASON THIS FILE EXISTS.** Every
 * caller today sits in a coroutine, so deleting `suspend` still compiles everywhere — and the tiles
 * would quietly go back to blocking the main thread on an unreachable PC. Deleting the `withContext`
 * while keeping `suspend` compiles too, and is just as bad: a suspend function inherits its caller's
 * dispatcher, and several callers are on `Dispatchers.Main`.
 */
class SendCommandThreadingTest {

    private fun coreClientSource(): String {
        val root =
            System.getProperty("remex.repoRoot")?.let(::File)
                ?: File(".").absoluteFile.let { start ->
                    generateSequence(start) { it.parentFile }
                        .firstOrNull { File(it, "remex.android").isDirectory }
                }
                ?: error("could not locate the repository root")

        val file =
            File(root, "remex.android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt")
        assertTrue("expected to find RemexCoreClient at ${file.path}", file.isFile)
        return file.readText().replace("\r\n", "\n")
    }

    /** [coreClientSource] with comments removed, so KDoc explaining the rule cannot satisfy it. */
    private fun code(): String =
        coreClientSource()
            .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
            .replace(Regex("""//.*"""), "")

    @Test
    fun `WakePc is suspend and switches off the caller's thread`() {
        // Same contract as SendCommand and for the same reason (RemEx-52n0): the native wake now
        // waits for the send instead of discarding it. The wait is much shorter — a UDP broadcast has
        // no reply to wait for — but it is still a socket operation, and this one is reachable from a
        // dashboard button, a Quick Settings tile and a home-screen widget.
        val code = code()

        val declaration = Regex("""suspend fun WakePc\(""").find(code)
        assertTrue("RemexCoreClient.WakePc must stay a suspend function.", declaration != null)

        val afterDeclaration = code.substring(declaration!!.range.first)
        val nativeCall = afterDeclaration.indexOf("WakePcNative(")
        assertTrue("expected WakePc to still reach WakePcNative", nativeCall > 0)

        assertTrue(
            "WakePc must move itself to a background dispatcher before crossing into the native call.",
            Regex("""withContext\(\s*Dispatchers\.(IO|Default)\s*\)""")
                .containsMatchIn(afterDeclaration.substring(0, nativeCall)),
        )
    }

    /**
     * Asserts one native wrapper suspends AND hops, between its declaration and the call it guards.
     *
     * Both halves have to be checked separately because each is invisible to the compiler on its
     * own. Every caller today already sits in a coroutine, so deleting `suspend` still compiles
     * everywhere. And deleting the `withContext` while keeping `suspend` compiles too — a suspend
     * function inherits its caller's dispatcher, which is exactly how RemexClientManager's automatic
     * re-pair ran a 90-second handshake on `Dispatchers.Main` (RemEx-uach). Making those functions
     * suspend did not even produce a compiler error there, because `connect()` was already a suspend
     * function on the main dispatcher.
     */
    private fun assertHopsOffTheCallerThread(function: String, nativeCall: String, why: String) {
        val code = code()

        val declaration = Regex("""suspend fun ${Regex.escape(function)}\(""").find(code)
        assertTrue("RemexCoreClient.$function must stay a suspend function. $why", declaration != null)

        val afterDeclaration = code.substring(declaration!!.range.first)
        val native = afterDeclaration.indexOf(nativeCall)
        assertTrue(
            "expected $function to still reach $nativeCall — if it no longer does, this test is " +
                "guarding the wrong function",
            native > 0,
        )

        assertTrue(
            "$function must move itself to a background dispatcher BEFORE it crosses into the " +
                "native call. Without the withContext it inherits the caller's dispatcher. $why",
            Regex("""withContext\(\s*Dispatchers\.(IO|Default)\s*\)""")
                .containsMatchIn(afterDeclaration.substring(0, native)),
        )
    }

    @Test
    fun `the pairing handshake is suspend and switches off the caller's thread`() {
        // WORST CASE NINETY SECONDS, all of it blocking: a 10s TCP probe, a 20s TLS and WebSocket
        // upgrade, then 60s waiting for the host's PairingResponse. RemexClientManager ran that on
        // Dispatchers.Main, so an unreachable host was a guaranteed ANR (RemEx-uach).
        //
        // BE EXACT ABOUT THE PATH: toggleConnection's `managerScope.launch { connect(pin) }`, with a
        // typed six-digit PIN. NOT the connection heartbeat, which launches on Dispatchers.IO and
        // passes a null PIN — so it never reaches the pairing branch at all. The bead originally
        // claimed both; only the tap was ever affected.
        assertHopsOffTheCallerThread(
            "StartPairing",
            "StartPairingNative(",
            "Its native budgets total 90 seconds against an unreachable host.",
        )
    }

    @Test
    fun `the PIN submission is suspend and switches off the caller's thread`() {
        assertHopsOffTheCallerThread(
            "SubmitPairingPin",
            "SubmitPairingPinNative(",
            "It waits up to 30 seconds for the host to confirm the PIN.",
        )
    }

    @Test
    fun `the PIN auto-fetch is suspend and switches off the caller's thread`() {
        assertHopsOffTheCallerThread(
            "FetchPairingPin",
            "FetchPairingPinNative(",
            "It waits up to 5 seconds on the pairing socket.",
        )
    }

    @Test
    fun `the connection manager does not block its main-thread scope on a pairing call`() {
        // WHAT THIS CAN ACTUALLY CATCH, and what it deliberately does not.
        //
        // Reaching the *Native functions directly is already impossible — they are private to
        // RemexCoreClient, and a test asserting that would pass for a reason the compiler owns
        // rather than for the reason it states.
        //
        // runBlocking is the hole that stays open. The manager's scope is Dispatchers.Main, the
        // pairing wrappers now suspend, and the tidy-looking way to call a suspend function from a
        // non-suspend spot is runBlocking — which parks the main thread for the callee's full budget
        // and puts the ANR straight back (RemEx-uach). It compiles, it reads as correct, and only a
        // switched-off PC reveals it.
        val manager =
            File(repoRoot(), "remex.android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt")
                .readText()
                .replace("\r\n", "\n")
                .replace(Regex("""/\*[\s\S]*?\*/"""), "")
                .replace(Regex("""//.*"""), "")

        assertTrue(
            "RemexClientManager's scope is Dispatchers.Main — the guard below only matters while " +
                "that holds, so it is asserted rather than assumed. If this scope moves to IO, " +
                "reconsider this test rather than deleting the assertion.",
            "CoroutineScope(SupervisorJob() + Dispatchers.Main)" in manager,
        )

        assertFalse(
            "RemexClientManager uses runBlocking. On a Dispatchers.Main scope that parks the UI " +
                "thread for the whole of whatever it wraps — up to 90 seconds for a pairing " +
                "handshake against an unreachable host (RemEx-uach).",
            // As a CALL, not as a substring, so a name or a string that merely contains the word
            // is not reported as a blocking bug.
            Regex("""\brunBlocking\s*[({]""").containsMatchIn(manager),
        )
    }

    @Test
    fun `SendCommand is suspend and switches off the caller's thread`() {
        val code = code()

        val declaration =
            Regex("""suspend fun SendCommand\(\s*commandJson: String\s*\)""").find(code)
        assertTrue(
            "RemexCoreClient.SendCommand must stay a suspend function. It blocks for a network " +
                "round trip, and suspend is the only thing stopping a caller from invoking it on " +
                "the main thread — seven Quick Settings tiles did exactly that before RemEx-66rf, " +
                "and only the compiler error found them.",
            declaration != null,
        )

        // The switch must be in the declaration itself, not left to whichever dispatcher the caller
        // happened to be on: a suspend function with no withContext runs wherever it is called.
        //
        // Bounded by the native call rather than by a character count. An earlier version allowed any
        // withContext within 400 characters, which both a long validation preamble could break for no
        // reason and an unrelated neighbouring function could satisfy. The region between the
        // declaration and the JNI call it guards is the thing that actually has to contain the hop.
        val afterDeclaration = code.substring(declaration!!.range.first)
        val nativeCall = afterDeclaration.indexOf("SendCommandNative(")
        assertTrue(
            "expected SendCommand to still reach SendCommandNative — if it no longer does, this " +
                "test is guarding the wrong function",
            nativeCall > 0,
        )

        val guardedRegion = afterDeclaration.substring(0, nativeCall)
        assertTrue(
            "SendCommand must move itself to a background dispatcher BEFORE it crosses into the " +
                "native call. Without the withContext it inherits the caller's dispatcher — and " +
                "callers run on Dispatchers.Main — so a slow or unreachable PC would freeze the UI " +
                "for the full ten-second command budget.",
            Regex("""withContext\(\s*Dispatchers\.(IO|Default)\s*\)""").containsMatchIn(guardedRegion),
        )
    }

    /**
     * The Wake-on-LAN tile broadcasts from the PHONE, not via the PC (RemEx-wgpm).
     *
     * It used to send the WAKEONLAN command verb over the control socket, asking the PC to do the
     * broadcasting — for the PC's own MAC, since macAddressFlow falls back to the host's reported
     * one. So it could only fire when the PC was awake and connected, and what it then asked for was
     * that PC to wake itself. Its own comment claimed the opposite.
     *
     * Worth a guard because nothing else can see it: both spellings compile, both send something,
     * and the difference only shows up on hardware that is switched off — the one state no test here
     * can reach.
     *
     * **SCANS THE WHOLE PACKAGE, NOT JUST THE TILE.** Checking only the tile file left a hole one
     * file wide: `sendTileWake` sits directly beside `sendTileCommand` in `TileCommand.kt` and is a
     * near-copy of it, so reimplementing it through the verb would restore the exact defect with the
     * guard still green.
     */
    @Test
    fun `no tile sends its wake through the PC`() {
        val tileDir = File(repoRoot(), "remex.android/app/src/main/java/com/clindsay94/remex/tile")
        assertTrue("expected the tile package at ${tileDir.path}", tileDir.isDirectory)

        val sources = tileDir.listFiles { f: File -> f.name.endsWith(".kt") }?.toList().orEmpty()
        assertTrue("expected tile sources to scan", sources.size >= 8)

        for (source in sources) {
            val code =
                source.readText().replace("\r\n", "\n")
                    .replace(Regex("""/\*[\s\S]*?\*/"""), "")
                    .replace(Regex("""//.*"""), "")

            assertFalse(
                "${source.name} sends the WAKEONLAN command verb. That asks the PC to broadcast, so " +
                    "it needs the PC awake and connected — precisely when a wake tile is not wanted. " +
                    "Broadcast from the phone via RemexCoreClient.WakePc instead.",
                "WAKEONLAN" in code,
            )
        }

        val helper = File(tileDir, "TileCommand.kt").readText()
        assertTrue(
            "sendTileWake must reach RemexCoreClient.WakePc — the phone-side broadcast is the whole " +
                "point of the fix",
            "RemexCoreClient.WakePc(" in helper,
        )
    }

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    @Test
    fun `no tile service calls SendCommand outside a coroutine`() {
        // The tiles are the sharpest case: TileService.onClick runs on the main thread, and a tile is
        // torn down moments later. They go through sendTileCommand, which owns a scope that outlives
        // the service - a service-scoped one would cancel the command mid-flight and the tap would
        // silently do nothing. Asserting they hold no direct reference keeps that single door.
        val root =
            System.getProperty("remex.repoRoot")?.let(::File)
                ?: File(".").absoluteFile.let { start ->
                    generateSequence(start) { it.parentFile }
                        .firstOrNull { File(it, "remex.android").isDirectory }
                }
                ?: error("could not locate the repository root")

        val tileDir = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/tile")
        assertTrue("expected the tile package at ${tileDir.path}", tileDir.isDirectory)

        val services =
            tileDir.listFiles { f: File -> f.name.endsWith("TileService.kt") }?.toList().orEmpty()
        assertTrue("expected to find tile services to check", services.size >= 7)

        for (service in services) {
            val body =
                service.readText().replace("\r\n", "\n").replace(Regex("""//.*"""), "")
            assertFalse(
                "${service.name} calls RemexCoreClient.SendCommand directly. Route it through " +
                    "sendTileCommand instead: onClick is the main thread, and the command must " +
                    "outlive the tile.",
                "RemexCoreClient.SendCommand" in body,
            )
        }
    }
}
