package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the one thing that keeps `RemexCoreClient.SendCommand` off the main thread (RemEx-66rf).
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
