package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the release-build shape the home-screen widgets depend on (live-check A1, A3).
 *
 * SOURCE-SCANNED, like `ui.PerfP3B4SourceShapeTest`: this module has no Robolectric and no Glance
 * harness, and the failure it guards only exists after R8 has run.
 *
 * A1/A3: every widget tap did nothing in the RELEASE build. Glance runs an `actionRunCallback<T>()`
 * by class name and instantiates it reflectively with `getDeclaredConstructor().newInstance()`. The
 * library's consumer rule is `-keep public class * extends ActionCallback` with no member spec, and
 * R8 full mode (the AGP 8+ default) does not treat that as keeping the no-arg constructor. Nothing
 * else calls it, so R8 removed `<init>()` from every callback (release `usage.txt` listed it) and the
 * reflective call threw inside Glance's broadcast receiver, which logs and swallows it.
 */
class WidgetReleaseShapeTest {

    private fun repoFile(relative: String): File {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { generateSequence(it) { p -> p.parentFile }
                .firstOrNull { File(it, "remex.android").isDirectory } }
            ?: error("could not locate the repository root")
        val file = File(root, relative)
        assertTrue("expected to find $relative at ${file.path}", file.isFile)
        return file
    }

    @Test
    fun `release keeps the no-arg constructor of every Glance ActionCallback`() {
        val rules = repoFile("remex.android/app/proguard-rules.pro").readText()
        val rule = Regex(
            """-keep\s+class\s+\*\s+implements\s+androidx\.glance\.appwidget\.action\.ActionCallback\s*\{[^}]*<init>\(\);[^}]*\}"""
        )
        assertTrue(
            "proguard-rules.pro must keep <init>() on ActionCallback implementations, or every widget tap is a no-op in release",
            rule.containsMatchIn(rules),
        )
    }

    @Test
    fun `widget grids scroll instead of dropping what does not fit`() {
        val dir = "remex.android/app/src/main/java/com/clindsay94/remex/widget/"
        for (name in listOf("RemoteControlWidget.kt", "AppLauncherWidget.kt", "HardwareInfoWidget.kt")) {
            val src = repoFile(dir + name).readText()
            assertTrue("$name must lay out through LazyVerticalGrid", src.contains("LazyVerticalGrid("))
            assertFalse(
                "$name must not truncate the selection to what fits; the grid scrolls",
                Regex("""\.take\(\s*maxItems\s*\)""").containsMatchIn(src),
            )
        }
    }
}
