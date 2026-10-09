package com.clindsay94.remex.ui.screens

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins docs/REGRESSION-GUARDS.md "Android — H.264 decoder": synchronous mode only, deferred SPS/PPS
 * configure, and the forbidden/required MediaFormat keys. Each break here once shipped as a silent
 * black stream.
 */
class H264DecoderGuardTest {
    private val relative = "com/clindsay94/remex/ui/screens/H264StreamDecoder.kt"

    private val raw: String by lazy {
        val roots = buildList {
            System.getProperty("remex.repoRoot")?.let { add(File(it, "remex.android/app/src/main/java")) }
            add(File("src/main/java"))
            add(File("app/src/main/java"))
            add(File("remex.android/app/src/main/java"))
        }
        val file = roots.map { it.resolve(relative) }.firstOrNull { it.isFile }
        requireNotNull(file) { "H264StreamDecoder.kt not found under any of $roots" }.readText()
    }

    private val code: String by lazy { stripComments(raw) }

    @Test
    fun `async setCallback is never used - only the KDoc mentions it`() {
        assertTrue("anti-vacuity: the KDoc rationale should still name setCallback", raw.contains("setCallback"))
        assertFalse("MediaCodec.setCallback must not be reintroduced", code.contains("setCallback"))
    }

    @Test
    fun `init only starts the decode thread - configure is deferred to the first SPS+PPS`() {
        val init = Regex("""\binit\s*\{([^}]*)\}""").find(code)?.groupValues?.get(1)
        assertNotNull("init block not found", init)
        assertTrue(init!!.contains("decodeThread.start()"))
        assertFalse("configure must not run on construction", init.contains("configure("))
        assertEquals("init should hold exactly one statement", 1, init.lines().count { it.isNotBlank() })
    }

    @Test
    fun `forbidden MediaFormat keys for a Surface-output decoder are absent`() {
        for (key in listOf("KEY_COLOR_FORMAT", "KEY_LOW_LATENCY", "KEY_OPERATING_RATE", "COLOR_FormatSurface")) {
            assertFalse("$key must never be set", code.contains(key))
        }
    }

    @Test
    fun `required MediaFormat keys are set - max input size, adaptive max dims, csd-0 and csd-1`() {
        assertTrue(code.contains("setInteger(MediaFormat.KEY_MAX_INPUT_SIZE"))
        assertTrue(code.contains("setInteger(MediaFormat.KEY_MAX_WIDTH"))
        assertTrue(code.contains("setInteger(MediaFormat.KEY_MAX_HEIGHT"))
        assertTrue(code.contains("setByteBuffer(\"csd-0\""))
        assertTrue(code.contains("setByteBuffer(\"csd-1\""))
    }

    @Test
    fun `input backlog is bounded at 6`() {
        val value = Regex("""const\s+val\s+MAX_INPUT_BACKLOG\s*=\s*(\d+)""").find(code)?.groupValues?.get(1)
        assertEquals("6", value)
    }

    private fun stripComments(src: String): String {
        val out = StringBuilder(src.length)
        var i = 0
        while (i < src.length) {
            val c = src[i]
            val next = if (i + 1 < src.length) src[i + 1] else '\u0000'
            when {
                c == '"' -> {
                    val end = stringEnd(src, i)
                    out.append(src, i, end)
                    i = end
                }
                c == '/' && next == '/' -> {
                    while (i < src.length && src[i] != '\n') i++
                }
                c == '/' && next == '*' -> {
                    val close = src.indexOf("*/", i + 2)
                    i = if (close < 0) src.length else close + 2
                    out.append(' ')
                }
                else -> {
                    out.append(c)
                    i++
                }
            }
        }
        return out.toString()
    }

    private fun stringEnd(src: String, start: Int): Int {
        if (src.startsWith("\"\"\"", start)) {
            val close = src.indexOf("\"\"\"", start + 3)
            return if (close < 0) src.length else close + 3
        }
        var i = start + 1
        while (i < src.length) {
            when (src[i]) {
                '\\' -> i += 2
                '"' -> return i + 1
                '\n' -> return i
                else -> i++
            }
        }
        return src.length
    }
}
