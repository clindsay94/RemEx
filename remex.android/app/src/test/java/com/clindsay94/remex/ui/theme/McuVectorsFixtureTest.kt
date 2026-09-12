package com.clindsay94.remex.ui.theme

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.security.MessageDigest

/** The committed oracle is complete, is what Google's classes produce today, and is the same bytes the PC tests read. */
class McuVectorsFixtureTest {
    private fun sha256(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    @Test
    fun `the committed file covers the whole grid with every role`() {
        val doc = JSONObject(McuVectors.resourceText())
        assertEquals(McuVectors.ROLES.map { it.first }, (0 until doc.getJSONArray("roles").length()).map { doc.getJSONArray("roles").getString(it) })
        val vectors = doc.getJSONArray("vectors")
        assertEquals(8 * 9 * 2 * 5, vectors.length())
        val controlHighlightIndex = McuVectors.ROLES.indexOfFirst { it.first == "controlHighlight" }
        for (i in 0 until vectors.length()) {
            val vector = vectors.getJSONObject(i)
            val argb = vector.getJSONArray("argb")
            assertEquals("vector $i", McuVectors.ROLES.size, argb.length())
            // Every role is always FF alpha EXCEPT controlHighlight, a fixed-opacity ripple/overlay
            // token by Google's own definition (RestrictedApi MaterialDynamicColors), not a bug here.
            for (j in 0 until argb.length()) assertTrue(argb.getString(j), Regex("^#[0-9A-F]{8}$").matches(argb.getString(j)))
            for (j in 0 until argb.length()) {
                if (j == controlHighlightIndex) continue
                assertTrue("vector $i role ${McuVectors.ROLES[j].first}: ${argb.getString(j)}", argb.getString(j).startsWith("#FF"))
            }
            val dark = vector.getBoolean("dark")
            val expectedControlHighlight = if (dark) "#33FFFFFF" else "#1F000000"
            assertEquals("vector $i controlHighlight", expectedControlHighlight, argb.getString(controlHighlightIndex))
        }
    }

    @Test
    fun `the committed file is exactly what Google's classes produce now`() {
        // A material bump that changes any colour fails here first, with the regenerate command in GenerateMcuVectorsTest.
        assertEquals(McuVectors.render(), McuVectors.resourceText())
    }

    @Test
    fun `the PC copy is byte-identical`() {
        val repoRoot = File(System.getProperty("remex.repoRoot") ?: error("remex.repoRoot system property not set"))
        val pcCopy = File(repoRoot, McuVectors.COMMITTED_COPIES[1])
        assertTrue("missing ${pcCopy.absolutePath}", pcCopy.isFile)
        val mine = McuVectors::class.java.classLoader!!.getResourceAsStream("mcu-vectors.json")!!.readBytes()
        assertEquals(sha256(mine), sha256(pcCopy.readBytes()))
    }
}
