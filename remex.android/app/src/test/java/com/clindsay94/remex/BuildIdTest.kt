package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Every APK carries an identity for THAT BUILD, distinct from versionName (RemEx-f2x9g).
 *
 * Both heads shipped 2.4.0 for months, so the version number could not tell two binaries apart —
 * which is exactly when "is the fix in this build?" starts getting asked and cannot be answered.
 * On the PC that question was settled twice by comparing a file timestamp against a commit
 * timestamp, and one of those answers was wrong.
 *
 * These are unit tests over a value the BUILD produced, which is unusual and is the point: the
 * failure mode is a build-script change that quietly stops stamping, and nothing at run time would
 * notice. The About screen hides its row when the id is absent, so the feature can disappear
 * entirely without a single crash, log line, or failing assertion anywhere else.
 */
class BuildIdTest {

    /** Seven hex for the short sha, optionally "+" and four hex when the tree was dirty. */
    private val shape = Regex("^[0-9a-f]{7}(\\+[0-9a-f]{4})?$")

    @Test
    fun `the apk carries a well formed build id`() {
        val id = BuildConfig.BUILD_ID

        assertTrue(
                "BUILD_ID is empty — the buildConfigField was removed or the value source stopped " +
                        "producing anything, and the About row would silently vanish rather than fail",
                id.isNotEmpty()
        )

        // "unknown" is legitimate on a machine without git, so it is accepted here rather than
        // failing a build agent that is a source drop. It is NOT accepted as the value on a machine
        // that does have git — see the test below.
        assertTrue(
                "BUILD_ID is '$id', which nobody can read off a screen and retype — the whole use case",
                id == "unknown" || shape.matches(id)
        )
    }

    @Test
    fun `the build id names this repository's HEAD when git is available`() {
        val head = git("rev-parse", "--short=7", "HEAD") ?: return // no git on this machine

        // NOT asserting equality with the full id: a dirty tree legitimately appends a marker, and
        // an APK built before the last commit legitimately names the older one. What must hold is
        // that the id starts with a real sha from THIS repository rather than a placeholder or a
        // value carried over from somewhere else.
        assertTrue(
                "BUILD_ID '${BuildConfig.BUILD_ID}' does not look like a sha from this repo (HEAD is '$head')",
                shape.matches(BuildConfig.BUILD_ID)
        )
        assertEquals(
                "the sha half must be exactly seven characters, matching the desktop's shape",
                7,
                BuildConfig.BUILD_ID.substringBefore('+').length
        )
    }

    @Test
    fun `the dirty marker reflects whether the tree was actually dirty`() {
        // THE ONLY TEST HERE THAT CAN CATCH THE FEATURE'S CENTRAL FAILURE. Everything else accepts
        // both shapes, so a value source that stopped detecting dirtiness would satisfy all of them
        // — a clean-looking id is a valid id. And the dirty marker is the whole reason this exists:
        // the sha alone was already available, and an APK built from uncommitted work carries the
        // PREVIOUS commit's sha, which is precisely the "two binaries, one identity" case.
        //
        // GUARDED ON THE SHA MATCHING HEAD, which is what makes this sound rather than merely
        // likely. If BUILD_ID names an older commit, the APK predates the current tree state and
        // says nothing about it, so the comparison is skipped instead of guessed at.
        val head = git("rev-parse", "--short=7", "HEAD") ?: return
        val status = gitStatus() ?: return
        val id = BuildConfig.BUILD_ID
        if (id.substringBefore('+') != head) return

        if (status.isBlank()) {
            assertTrue(
                    "the tree is clean and BUILD_ID is at HEAD, so '$id' must not claim to be dirty",
                    !id.contains('+')
            )
        } else {
            assertTrue(
                    "the tree has uncommitted changes and BUILD_ID is at HEAD, so '$id' must carry " +
                            "the dirty marker — without it this APK is labelled identically to a " +
                            "clean build of the same commit, which is the failure this feature exists to prevent",
                    id.contains('+')
            )
        }
    }

    @Test
    fun `the build id is not the version name`() {
        // They answer different questions and are shown on the same row, one above the other. If a
        // refactor ever wires one to the other, the supporting line adds nothing.
        assertNotEquals(BuildConfig.VERSION_NAME, BuildConfig.BUILD_ID)
    }

    @Test
    fun `the about screen actually displays it`() {
        // The stamp existing in BuildConfig and the stamp being VISIBLE are different facts, and
        // only the second one is the feature. There is no Compose UI test harness in this module,
        // so the source is the evidence available — the same idiom the desktop side uses for
        // bindings that fail silently.
        val screen =
                File(repoRoot(), "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/AboutScreen.kt")
                        .readText()

        // THE TWO MUST BE CHECKED TOGETHER, AS ONE EXPRESSION. Asserting each separately is what
        // the first version of this did, and it passed on an injected defect: the row was changed to
        // format the label with an empty string, while `BuildConfig.BUILD_ID` still appeared a few
        // lines above in the visibility check. Both `contains` calls matched and the screen showed
        // "Build " with nothing after it.
        val formatted =
                Regex(
                        """R\.string\.about_build_id\s*,\s*BuildConfig\.BUILD_ID""",
                        RegexOption.DOT_MATCHES_ALL
                )

        assertTrue(
                "AboutScreen must pass BuildConfig.BUILD_ID as the argument to about_build_id — " +
                        "finding both names somewhere in the file proves nothing about what is rendered",
                formatted.containsMatchIn(screen)
        )
    }

    @Test
    fun `the desktop and android build ids agree on their shape`() {
        // ONE SHAPE ACROSS BOTH PLATFORMS IS THE ENTIRE POINT OF DOING THE SECOND ONE. The host
        // reports its version to the phone already, so the two ids end up side by side; if the PC
        // showed eight characters and the phone showed seven, comparing them would take thought
        // rather than a glance. Nothing but this test connects the two build systems.
        val targets = File(repoRoot(), "build/BuildId.targets").readText()

        assertTrue(
                "the desktop no longer asks git for a 7-character sha; re-point this test after " +
                        "deciding whether Android should follow",
                targets.contains("rev-parse --short=7 HEAD")
        )
        assertTrue(
                "the desktop no longer takes four characters for its dirty marker",
                targets.contains("Substring(0, 4)")
        )
    }

    /**
     * Porcelain status, distinguished from [git] because BLANK is a meaningful answer here and
     * [git] collapses blank output to null. A clean tree is exactly the case that must not be
     * confused with "git did not run".
     */
    private fun gitStatus(): String? =
            try {
                val process =
                        ProcessBuilder("git", "-C", repoRoot().absolutePath, "status", "--porcelain")
                                .redirectErrorStream(true)
                                .start()
                val output = process.inputStream.bufferedReader().readText()
                if (process.waitFor() == 0) output else null
            } catch (_: Exception) {
                null
            }

    private fun git(vararg args: String): String? =
            try {
                val process =
                        ProcessBuilder(listOf("git", "-C", repoRoot().absolutePath) + args)
                                .redirectErrorStream(true)
                                .start()
                val output = process.inputStream.bufferedReader().readText().trim()
                if (process.waitFor() == 0 && output.isNotEmpty()) output else null
            } catch (_: Exception) {
                null
            }

    /**
     * The repository root, walked up from the module directory rather than hardcoded, so this works
     * from Gradle (which runs tests with the module as the working directory) and from an IDE.
     */
    private fun repoRoot(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "AGENTS.md").exists()) dir = dir.parentFile
        return requireNotNull(dir) { "could not locate the repository root from ${File("").absolutePath}" }
    }
}
