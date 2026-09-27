package com.clindsay94.remex.routines

import java.io.File
import org.json.JSONException
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Routine log redaction through the `RoutineLog` wrapper (spec §9 T11, R-SEC-11; RemEx-pp0rt.5). */
class RoutineLogRedactionTest {
    private val lines = mutableListOf<Pair<String, Throwable?>>()

    @After
    fun restore() {
        RoutineLog.resetSinkForTests()
    }

    private fun capture() {
        RoutineLog.sink = RoutineLog.Sink { _, message, error -> lines += message to error }
    }

    @Test
    fun `a MAC keeps only its vendor half`() {
        assertEquals("0A:1B:2C:**:**:**", RoutineLog.mac("0a:1b:2c:3d:4e:5f"))
        assertEquals("0A:1B:2C:**:**:**", RoutineLog.mac("0A-1B-2C-3D-4E-5F"))
        assertEquals("**:**:**:**:**:**", RoutineLog.mac("not a mac"))
        assertEquals("**:**:**:**:**:**", RoutineLog.mac(null))
    }

    @Test
    fun `routine names are cut to 16 characters and ids and hosts shortened`() {
        assertEquals("\"Wake the gaming …\"", RoutineLog.name("Wake the gaming rig and start Steam"))
        assertEquals("\"Short\"", RoutineLog.name("Short"))
        assertEquals("3f0c2a4e", RoutineLog.id("3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10"))
        assertEquals("9f2c…", RoutineLog.host("9f2c4be07a1d33e5"))
    }

    @Test
    fun `an exception is logged with its type and stack but never its message`() {
        capture()
        val secret = JSONException("Unterminated string at 40 [character 41 line 1] {\"body\":\"door code 4471\"}")
        RoutineLog.e("A history document did not parse.", secret)
        val (message, error) = lines.single()
        assertTrue(message.contains("org.json.JSONException"))
        assertFalse(message.contains("4471"))
        val logged = checkNotNull(error)
        assertFalse(logged.toString().contains("4471"))
        assertFalse(logged.message.orEmpty().contains("4471"))
        assertEquals(secret.stackTrace.toList(), logged.stackTrace.toList())
    }

    @Test
    fun `nothing in the routines packages calls android util Log directly`() {
        val roots = listOf(File("src/main/java/com/clindsay94/remex/routines"), File("app/src/main/java/com/clindsay94/remex/routines"))
        val root = roots.firstOrNull { it.isDirectory } ?: error("routines sources not found - tried $roots")
        val direct = Regex("""\bLog\.[vdiwe]\(|import android\.util\.Log\b""")
        val offenders =
            root.walkTopDown()
                .filter { it.isFile && it.extension == "kt" && it.name != "RoutineLog.kt" }
                .filter { direct.containsMatchIn(it.readText()) }
                .map { it.name }
                .toList()
        assertEquals("route routine logging through RoutineLog so it is redacted", emptyList<String>(), offenders)
    }
}
