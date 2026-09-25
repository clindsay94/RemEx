package com.clindsay94.remex.service

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit P3-10: `start()` used to launch four coroutines (the file-transfer message collector,
 * two DataStore config-flow collectors, and an orphan sweep) but only ever assigned the FIRST one
 * anywhere `stop()` could reach it - the other three ran forever, uncancelled, and every service
 * re-creation added three more on top of whatever the last one leaked.
 *
 * SOURCE-SCANNED, THE ESTABLISHED PRECEDENT FOR THIS SHAPE OF DEFECT (see
 * `SendDispatcherDeclarationOrderTest`): `AndroidFileTransferHost` is a Kotlin `object` with
 * `lateinit var context: Context` and real `SettingsManager`/DataStore dependencies, and this
 * module has no Robolectric, so `start()`/`stop()` cannot be driven behaviourally here - the defect
 * is coroutine lifetime, which reading the emitted bytecode or a mocked scope cannot see either.
 */
class AndroidFileTransferHostJobTrackingTest {

    private fun source(): String {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { generateSequence(it) { p -> p.parentFile }
                .firstOrNull { File(it, "remex.android").isDirectory } }
            ?: error("could not locate the repository root")

        val file = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/service/AndroidFileTransferHost.kt")
        assertTrue("expected to find AndroidFileTransferHost.kt at ${file.path}", file.isFile)
        return file.readText()
    }

    /** From `fun start(` to the next top-level `fun `, at 4-space class-member indentation. */
    private fun startBody(source: String): String {
        val start = source.indexOf("fun start(ctx: Context)")
        assertTrue("start(ctx: Context) not found - it moved or was renamed", start >= 0)
        val end = source.indexOf("\n    fun ", start + 1)
        assertTrue("could not find the end of start()", end > start)
        return source.substring(start, end)
    }

    /** From `fun stop(` to the next top-level `fun `/`private fun `, at 4-space indentation. */
    private fun stopBody(source: String): String {
        val start = source.indexOf("fun stop()")
        assertTrue("stop() not found - it moved or was renamed", start >= 0)
        val end = source.indexOf("\n    ", start).let { firstIndent ->
            // Skip past every line indented deeper than 4 spaces (the function's own body) to the
            // next 4-space-indented declaration - same heuristic as startBody's "\n    fun " search,
            // generalized since stop() is followed by a private function, not another `fun `.
            var i = firstIndent
            while (i >= 0 && i < source.length && source.startsWith("        ", i + 1)) {
                i = source.indexOf("\n    ", i + 1)
            }
            i
        }
        assertTrue("could not find the end of stop()", end > start)
        return source.substring(start, end)
    }

    @Test
    fun `every coroutine start launches is tracked in the jobs list`() {
        val body = startBody(source())

        // Four launches today: the message collector, the two config-flow collectors, and the
        // orphan sweep. This count is the actual regression guard - a fifth `scope.launch` added
        // later without a matching `jobs +=` would pass every OTHER assertion here and still leak.
        val launchCount = Regex("""scope\.launch""").findAll(body).count()
        val trackedCount = Regex("""jobs\s*\+=\s*scope\.launch""").findAll(body).count()

        assertTrue("start() should launch at least one coroutine - it moved or this file drifted", launchCount > 0)
        assertEquals(
            "every scope.launch(...) in start() must be tracked via `jobs += scope.launch { ... }`, " +
                "or stop() cannot cancel it and it leaks across every service re-creation",
            launchCount,
            trackedCount,
        )
    }

    @Test
    fun `stop cancels every tracked job, not just one`() {
        val body = stopBody(source())

        assertTrue(
            "stop() must cancel every job in the tracked list (jobs.forEach { it.cancel() }), " +
                "not a single field - that was the original defect",
            body.contains("jobs.forEach") && body.contains(".cancel()"),
        )
        assertTrue("stop() should clear the tracked list after cancelling", body.contains("jobs.clear()"))
    }

    @Test
    fun `start clears out whatever a prior start left running before launching a fresh set`() {
        // Guards the re-entrancy case stop() alone can't cover: start() called again without an
        // intervening stop() (a service re-created without a clean teardown) must not just ADD to
        // the existing set of live collectors.
        val body = startBody(source())

        val jobsClearIndex = body.indexOf("jobs.clear()")
        val firstLaunchIndex = body.indexOf("scope.launch")
        assertTrue("start() should cancel and clear any jobs left by a prior start() before launching new ones",
            jobsClearIndex in 0 until firstLaunchIndex)
    }
}
