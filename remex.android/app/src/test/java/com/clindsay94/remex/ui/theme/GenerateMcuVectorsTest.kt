package com.clindsay94.remex.ui.theme

import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File

/**
 * Writes mcu-vectors.json into BOTH repos from Google's classes (RemEx-4kv0g.6). Skipped in every
 * ordinary run; the committed file is the artefact. Regenerate only when material is bumped:
 *   ./gradlew testReleaseUnitTest --tests 'com.clindsay94.remex.ui.theme.GenerateMcuVectorsTest' -Premex.generateMcuVectors=true --rerun-tasks
 */
class GenerateMcuVectorsTest {
    @Test
    fun `write the vector file into both committed locations`() {
        assumeTrue("pass -Premex.generateMcuVectors=true to regenerate", System.getProperty("remex.generateMcuVectors") == "true")
        val repoRoot = File(System.getProperty("remex.repoRoot") ?: error("remex.repoRoot system property not set — see the unitTests block in app/build.gradle.kts"))
        val text = McuVectors.render()
        for (rel in McuVectors.COMMITTED_COPIES) {
            val target = File(repoRoot, rel)
            target.parentFile.mkdirs()
            target.writeBytes(text.toByteArray(Charsets.UTF_8))
            println("wrote ${target.absolutePath} (${text.length} chars)")
        }
    }
}
