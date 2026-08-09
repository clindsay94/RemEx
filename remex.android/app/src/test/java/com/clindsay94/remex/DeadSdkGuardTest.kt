package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * No `SDK_INT` comparison names an API level the app cannot run below (RemEx-jcl4p).
 *
 * **FIFTEEN OF THESE WERE DEAD WHEN THIS TEST WAS WRITTEN**, across eight files, and they were not
 * harmless. Each one described a second behaviour that no device ever takes — a legacy NSD resolve
 * serialised behind a mutex, an `AppCompatDelegate` locale path, a notification manager that
 * returned early "on pre-O". A reader debugging locale switching had two implementations to
 * consider and one that runs; three of them carried KDoc promising the fallback as a feature, and
 * two left whole private functions stranded behind them.
 *
 * The rule is mechanical, which is why it is a test and not a review note: at `minSdk = 34`,
 * `SDK_INT >= 34` is a constant `true` and `SDK_INT < 34` a constant `false`. The compiler will not
 * say so — these are runtime `Int` comparisons, not `const` — so nothing fails and the dead branch
 * stays.
 *
 * **MINSDK IS READ FROM `build.gradle.kts`, NOT HARDCODED.** Hardcoding 34 would make this test
 * quietly wrong the day minSdk moves, which is precisely the day it has the most to say: a bump to
 * 35 turns every `>= VANILLA_ICE_CREAM` in the tree into a new dead branch, and this should be what
 * reports them. That is also why the test task declares the scanned sources as Gradle inputs — see
 * `app/build.gradle.kts`. A minSdk bump need not change a single `.class` byte, so without that
 * declaration the run that matters most is the one Gradle is most likely to skip as UP-TO-DATE.
 *
 * What it deliberately does NOT flag: comparisons against levels ABOVE minSdk (`>= 36`, `>= 37`),
 * which are genuinely conditional and must stay. Guards above minSdk are the entire legitimate use
 * of `SDK_INT`.
 *
 * **KNOWN BOUNDARY, STATED SO THE NEXT READER KNOWS IT IS DELIBERATE:** this reads literals only. A
 * level reached through a named constant (`private const val MIN_FOO = 33`), through `@RequiresApi`,
 * or through `SdkExtensions.getExtensionVersion` is not detected. Those are rarer and much harder to
 * judge textually; the literal forms are the ones that actually accumulated here.
 */
class DeadSdkGuardTest {

    private val repoRoot: File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    /**
     * The whole main source set, not one package.
     *
     * Scoping this to `…/java/com/clindsay94/remex` would leave a new top-level package unscanned,
     * and the size floor below would not notice because the existing tree clears it comfortably.
     */
    private val sourceRoots =
        listOf("remex.android/app/src/main/java", "remex.android/app/src/main/kotlin")
            .map { File(repoRoot, it) }
            .filter { it.isDirectory }

    /**
     * API level for each codename this repo might name.
     *
     * An unrecognised codename FAILS rather than being skipped — a scan that silently ignores what
     * it cannot parse is how a guard goes quiet, and a new codename is exactly when a dead
     * comparison is most likely to be introduced.
     */
    private val apiLevels =
        mapOf(
            "LOLLIPOP" to 21,
            "LOLLIPOP_MR1" to 22,
            "M" to 23,
            "N" to 24,
            "N_MR1" to 25,
            "O" to 26,
            "O_MR1" to 27,
            "P" to 28,
            "Q" to 29,
            "R" to 30,
            "S" to 31,
            "S_V2" to 32,
            "TIRAMISU" to 33,
            "UPSIDE_DOWN_CAKE" to 34,
            "VANILLA_ICE_CREAM" to 35,
            "BAKLAVA" to 36,
        )

    private val level = """(?:(?:android\.os\.)?Build\.VERSION_CODES\.([A-Z_0-9]+)|(\d+))"""
    /**
     * `SDK_INT`, however qualified.
     *
     * **THE `android.os.` PREFIX HAS TO BE OPTIONAL HERE, NOT JUST ON THE LEVEL.** When `SDK_INT` is
     * on the left it does not matter — the regex simply starts matching part-way into the qualified
     * name. When it is on the right of a reversed comparison it is pinned by the preceding `\s*`,
     * and `android.os.Build.VERSION.SDK_INT` then fails to match at all. A reversed mutation caught
     * this; the reversed branch was inert against exactly the spelling this repo already uses.
     */
    private val sdkInt = """(?:(?:android\.os\.)?Build\.VERSION\.)?SDK_INT"""
    private val op = """(>=|<=|==|!=|>|<)"""

    /**
     * `SDK_INT op LEVEL`, matched against whole-file text rather than per line.
     *
     * **PER-LINE MATCHING WOULD MISS A WRAPPED GUARD**, and this codebase wraps conditions at these
     * indentation depths already. `\s*` spans the newline once the scan is not line-bounded, so
     * `SDK_INT >=\n    Build.VERSION_CODES.UPSIDE_DOWN_CAKE` is caught like any other.
     */
    private val forward = Regex("""$sdkInt\s*$op\s*$level""")

    /**
     * `LEVEL op SDK_INT` — the same comparison written the other way round.
     *
     * Legal Kotlin, and invisible to a pattern anchored on `SDK_INT` being on the left.
     */
    private val reversed = Regex("""$level\s*$op\s*$sdkInt""")

    /** `<`/`>` and `<=`/`>=` swap when the operands do; `==`/`!=` are symmetric. */
    private fun flip(op: String) =
        when (op) {
            ">=" -> "<="
            "<=" -> ">="
            ">" -> "<"
            "<" -> ">"
            else -> op
        }

    private fun minSdk(): Int {
        val gradle = File(repoRoot, "remex.android/app/build.gradle.kts")
        assertTrue("build.gradle.kts moved or was renamed", gradle.isFile)
        val match = Regex("""minSdk\s*=\s*(\d+)""").find(gradle.readText())
        return requireNotNull(match) { "no minSdk assignment in app/build.gradle.kts" }
            .groupValues[1]
            .toInt()
    }

    private fun sources(): List<File> =
        sourceRoots.flatMap { root ->
            root.walkTopDown().filter { it.isFile && it.extension == "kt" }
        }

    /**
     * Blanks comments and string literals, preserving every newline so offsets still map to lines.
     *
     * **PROSE ABOUT A REMOVED GUARD MUST NOT FAIL THIS TEST.** `HapticModifier`'s KDoc already
     * describes three guards this bead deleted, and escapes the regex by one token — it writes the
     * bare codename rather than the qualified one. Had it been more precise, this test would fail on
     * correct committed code with a message ("delete the branch") naming a branch that does not
     * exist, and the predictable fix would be an allowlist. AGENTS.md is explicit about what those
     * cost: a list that can absorb a false positive will absorb a real one.
     *
     * The trade is right way round: commented-out code does not run, so it is not a dead *branch*,
     * and a branch no device takes is the entire harm this test exists to prevent.
     *
     * A character walk, not a regex. A regex for this went wrong earlier in this same effort — a
     * character-class negation matched newlines and silently blanked 22 lines of a different scan.
     */
    private fun stripCommentsAndStrings(text: String): String {
        val out = StringBuilder(text.length)
        var i = 0
        while (i < text.length) {
            val c = text[i]
            val rest = text.length - i
            when {
                rest >= 2 && c == '/' && text[i + 1] == '/' -> {
                    while (i < text.length && text[i] != '\n') { out.append(' '); i++ }
                }
                rest >= 2 && c == '/' && text[i + 1] == '*' -> {
                    val end = text.indexOf("*/", i + 2).let { if (it < 0) text.length else it + 2 }
                    while (i < end) { out.append(if (text[i] == '\n') '\n' else ' '); i++ }
                }
                rest >= 3 && text.startsWith("\"\"\"", i) -> {
                    val end = text.indexOf("\"\"\"", i + 3).let { if (it < 0) text.length else it + 3 }
                    while (i < end) { out.append(if (text[i] == '\n') '\n' else ' '); i++ }
                }
                c == '"' -> {
                    out.append(' ')
                    i++
                    while (i < text.length && text[i] != '"' && text[i] != '\n') {
                        if (text[i] == '\\' && i + 1 < text.length) { out.append(' '); i++ }
                        out.append(' ')
                        i++
                    }
                    if (i < text.length && text[i] == '"') { out.append(' '); i++ }
                }
                else -> {
                    out.append(c)
                    i++
                }
            }
        }
        return out.toString()
    }

    private data class Site(val file: String, val line: Int, val op: String, val level: Int)

    private fun sites(): List<Site> =
        sources().flatMap { file ->
            val code = stripCommentsAndStrings(file.readText())

            fun resolve(codename: String, literal: String, at: Int): Int =
                if (literal.isNotEmpty()) {
                    literal.toInt()
                } else {
                    apiLevels[codename]
                        ?: error(
                            "unknown API codename '$codename' at ${file.name}:$at — add it to " +
                                "apiLevels so this guard can judge it, rather than letting it pass " +
                                "unchecked"
                        )
                }

            fun lineOf(offset: Int) = code.take(offset).count { it == '\n' } + 1

            val f =
                forward.findAll(code).map { m ->
                    val line = lineOf(m.range.first)
                    val (op, codename, literal) = m.destructured
                    Site(file.name, line, op, resolve(codename, literal, line))
                }
            val r =
                reversed.findAll(code).map { m ->
                    val line = lineOf(m.range.first)
                    val (codename, literal, op) = m.destructured
                    Site(file.name, line, flip(op), resolve(codename, literal, line))
                }
            (f + r).toList()
        }

    @Test
    fun `no SDK_INT comparison names a level at or below minSdk`() {
        val min = minSdk()

        // `>= X` and `< X` are decided when X is minSdk itself; the rest need X strictly below it.
        // `>= minSdk` is the single most common dead form — eleven of the fifteen found — so
        // getting this boundary wrong in the lax direction would defeat most of the test. In the
        // other direction, `> minSdk`, `<= minSdk` and `== minSdk` are all genuinely conditional
        // (they distinguish a device ON minSdk from one above it) and must not be flagged.
        val dead =
            sites().filter { site ->
                when (site.op) {
                    ">=", "<" -> site.level <= min
                    else -> site.level < min
                }
            }

        assertEquals(
            dead.joinToString("\n") { "  ${it.file}:${it.line}  SDK_INT ${it.op} ${it.level}" }.let {
                "these SDK_INT comparisons are decided at minSdk=$min and the branch behind them " +
                    "cannot run — delete the branch, do not suppress this:\n$it"
            },
            emptyList<Site>(),
            dead,
        )
    }

    @Test
    fun `the scan finds the live guards rather than passing on an empty set`() {
        // THE ANTI-VACUITY CHECK, and it has already earned its place: pointing the scan at a
        // package that does not exist made the test above pass while only this one failed. A wrong
        // path, a renamed package or a regex that stops matching all produce the same silent pass.
        val found = sites()
        assertTrue("the scan matched no SDK_INT comparison at all", found.isNotEmpty())
        assertTrue(
            "expected the scan to still see guards above minSdk; found ${found.map { it.level }}",
            found.any { it.level > minSdk() },
        )
        assertTrue("no Kotlin sources under $sourceRoots", sources().size > 100)
    }

    @Test
    fun `stripping blanks comments without moving line numbers`() {
        // The stripper is the part most likely to fail open - blank too much and every scan goes
        // quiet, blank too little and prose fails the build. Both directions are checked here, and
        // line alignment is checked because every reported location depends on it.
        val sample =
            """
            val a = 1 // SDK_INT >= Build.VERSION_CODES.O
            /* SDK_INT >= Build.VERSION_CODES.R
               still inside the block */
            val b = "SDK_INT >= Build.VERSION_CODES.Q"
            val real = Build.VERSION.SDK_INT >= Build.VERSION_CODES.BAKLAVA
            """
                .trimIndent()

        val stripped = stripCommentsAndStrings(sample)

        assertEquals(
            "the stripper moved line numbers",
            sample.count { it == '\n' },
            stripped.count { it == '\n' },
        )
        assertEquals("a commented level survived stripping", 1, forward.findAll(stripped).count())
        assertEquals(
            "the surviving match is not the real one",
            "BAKLAVA",
            forward.find(stripped)!!.groupValues[2],
        )
        assertTrue("real code was blanked", stripped.contains("val real"))
    }
}
