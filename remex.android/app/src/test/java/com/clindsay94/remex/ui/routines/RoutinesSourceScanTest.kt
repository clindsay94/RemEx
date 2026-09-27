package com.clindsay94.remex.ui.routines

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Structural scans over the Routines UI sources (routines spec 13.7; RemEx-pp0rt.6):
 * RoutinesReducedMotionScanTest (R-UX-52), RoutinesColorLiteralScanTest (R-UX-53) and
 * RoutinesNoConcatScanTest (R-UX-56), kept in one class because they read the same files.
 */
class RoutinesSourceScanTest {
    private val sources: List<File> by lazy {
        val relative = "src/main/java/com/clindsay94/remex/ui/routines"
        val dir =
            listOf(File(relative), File("app/$relative"), File("remex.android/app/$relative")).firstOrNull { it.isDirectory }
                ?: error("ui/routines not found from ${File(".").absolutePath}")
        dir.listFiles { f -> f.extension == "kt" }.orEmpty().sortedBy { it.name }.also {
            assertTrue("no routines UI sources found", it.isNotEmpty())
        }
    }

    /** Code only: line comments and KDoc are allowed to name the things the rules forbid. */
    private fun code(file: File): List<Pair<Int, String>> =
        file.readLines().mapIndexed { i, line -> i + 1 to line.substringBefore("//") }
            .filterNot { (_, line) -> line.trimStart().startsWith("*") || line.trimStart().startsWith("/*") }

    @Test
    fun `RoutinesReducedMotionScanTest - self-driven motion is gated on LocalReducedMotion`() {
        // Anything that animates on its own (loops, choreography, loading shapes, the wave) must read
        // LocalReducedMotion in the same file; everything else comes from MaterialTheme.motionScheme,
        // which the theme already swaps to Standard at animator scale 0 (spec 3.1).
        val selfDriven = Regex("""infiniteRepeatable|rememberInfiniteTransition|RemexLoadingIndicator\(|LoadingIndicator\(|RemexLinearWavyProgress\(|Animatable\(""")
        val offenders =
            sources.filter { file -> code(file).any { (_, l) -> selfDriven.containsMatchIn(l) } && !file.readText().contains("LocalReducedMotion.current") }
        assertTrue("Self-driven motion without a LocalReducedMotion gate: ${offenders.map { it.name }}", offenders.isEmpty())
    }

    @Test
    fun `RoutinesReducedMotionScanTest - no literal springs or tweens`() {
        val literal = Regex("""\btween\(|\bspring\(|keyframes\s*\{|\bsnap\(\d""")
        val hits = sources.flatMap { f -> code(f).filter { (_, l) -> literal.containsMatchIn(l) }.map { "${f.name}:${it.first}" } }
        assertTrue("Use MaterialTheme.motionScheme tokens (spec 3.2): $hits", hits.isEmpty())
    }

    @Test
    fun `RoutinesColorLiteralScanTest - colours come from roles only`() {
        val literal = Regex("""Color\(0x|Color\(\s*\d|Color\.(Red|Green|Blue|Black|White|Gray|Yellow|Cyan|Magenta|DarkGray|LightGray)\b|"#[0-9A-Fa-f]{6}""")
        val hits = sources.flatMap { f -> code(f).filter { (_, l) -> literal.containsMatchIn(l) }.map { "${f.name}:${it.first}" } }
        assertTrue("Colour literals in routines UI (R-UX-53): $hits", hits.isEmpty())
    }

    @Test
    fun `RoutinesNoConcatScanTest - no sentence is built by gluing strings`() {
        // stringResource(...) + ..., ... + stringResource(...), getString(...) + ..., and string
        // templates around a resource all build sentences nine locales cannot reorder (R-UX-56).
        val glue =
            Regex(
                """(stringResource|getString|pluralStringResource|getQuantityString)\([^()]*(\([^()]*\))?[^()]*\)\s*\+|\+\s*(stringResource|getString|pluralStringResource)\(|"\$\{(stringResource|context\.getString)""",
            )
        val hits = sources.flatMap { f -> code(f).filter { (_, l) -> glue.containsMatchIn(l) }.map { "${f.name}:${it.first}" } }
        assertTrue("String concatenation in routines UI: $hits", hits.isEmpty())
    }

    @Test
    fun `RoutinesNoConcatScanTest - When and Then are headers, never words in a sentence`() {
        val res = listOf(File("src/main/res/values/strings.xml"), File("app/src/main/res/values/strings.xml")).first { it.isFile }
        val xml = res.readText()
        assertTrue(xml.contains("""<string name="routines_when">When</string>"""))
        assertTrue(xml.contains("""<string name="routines_then">Then</string>"""))
        val usages = sources.flatMap { f -> code(f).filter { (_, l) -> l.contains("R.string.routines_when") || l.contains("R.string.routines_then") } }
        assertTrue(usages.all { (_, l) -> l.contains("SectionLabel(") })
    }
}
