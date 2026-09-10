package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the promise that Remote Desktop's two toolbars offer the same actions in the same order
 * (RemEx-klq, extended by RemEx-byij).
 *
 * The screen draws its controls TWICE — once in the windowed `TopAppBar`, once as a floating row over
 * the video in fullscreen — and the two are two thousand lines apart. Nothing but a comment has ever
 * connected them, so adding a button to one and forgetting the other is invisible: both variants
 * compile, `lintVitalRelease` passes, and the only symptom is that an action a user found in one mode
 * is missing, or has moved, in the other. That comment has already gone stale once, when the
 * screenshot button was added.
 *
 * Reading the source is the technique `SendDispatcherDeclarationOrderTest` and
 * `PairingErrorCodesCoverageTest` already use here: `RemoteDesktopScreenContent` is a `@Composable`
 * needing a real `Application` and DataStore behind it, and this module has no Robolectric or Compose
 * UI test rule, so composing it is not available.
 */
class RemoteDesktopToolbarParityTest {

    /**
     * The actions BOTH bars are expected to carry, in the order both must present them.
     *
     * Deliberately not "every action in either bar". Each surface has one legitimately exclusive
     * control — `cd_reset_input` is windowed-only, the FPS overlay toggle is fullscreen-only — and
     * requiring those of both would be asserting a rule the screen does not follow.
     *
     * THE LIMIT THIS BUYS, STATED SO NOBODY TRUSTS THE TEST FURTHER THAN IT GOES: an action added
     * under a name that is not in this list is filtered out of both halves and passes, however badly
     * it is placed. A future shared action must be added HERE as well as to the two bars, or it is
     * unguarded. What the test does catch is any drift in the seven it knows about — which is every
     * action the two bars SHARE today, not every action the screen has: the two exclusive controls
     * named above are deliberately outside it.
     */
    private val sharedActions =
        listOf(
            "cd_show_keyboard",
            "cd_show_pc_keys",
            "action_take_screenshot",
            "cd_settings",
            FULLSCREEN_TOGGLE,
            "cd_stop",
            "button_start_streaming",
        )

    private fun screenSource(): String {
        val root =
            System.getProperty("remex.repoRoot")?.let(::File)
                ?: File(".").absoluteFile.let { start ->
                    generateSequence(start) { it.parentFile }
                        .firstOrNull { File(it, "remex.android").isDirectory }
                }
                ?: error("could not locate the repository root")

        val file =
            File(
                root,
                "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt",
            )
        assertTrue("expected to find RemoteDesktopScreen at ${file.path}", file.isFile)
        return file.readText().replace("\r\n", "\n")
    }

    /**
     * The tooltip labels of one bar, in source order, reduced to the shared vocabulary.
     *
     * The two bars name the fullscreen control differently ON PURPOSE — one enters, the other exits —
     * so both collapse to a single logical slot. Comparing the raw names would report a difference
     * that is correct behaviour.
     *
     * Filtering to [sharedActions] is also what bounds the fullscreen half: tooltips further down the
     * file that belong to neither bar (the settings sheet's close button, the extra-keys grid) use
     * names outside this vocabulary and drop out on their own, so no brace-matching is needed.
     */
    private fun actionsIn(region: String): List<String> =
        TOOLTIP.findAll(region)
            .map { it.groupValues[1] }
            .map { if (it in FULLSCREEN_NAMES) FULLSCREEN_TOGGLE else it }
            .filter { it in sharedActions }
            .toList()

    @Test
    fun `both toolbars offer the shared actions in the same order`() {
        val source = screenSource()

        val split = source.indexOf(ORDER_COMMENT)
        assertTrue(
            "expected the fullscreen control row to still be introduced by the comment beginning " +
                "\"$ORDER_COMMENT\" — this test splits the file on it, and without it the two bars " +
                "cannot be told apart",
            split >= 0,
        )

        val windowed = actionsIn(source.substring(0, split))
        val fullscreen = actionsIn(source.substring(split))

        // Both compared against the expected list rather than only against each other: two bars that
        // had drifted the SAME way would agree with each other and still be wrong.
        assertEquals("windowed TopAppBar actions", sharedActions, windowed)
        assertEquals("fullscreen control row actions", sharedActions, fullscreen)
    }

    @Test
    fun `the screenshot action is reachable from both toolbars`() {
        // Called out separately from the order test because it is the one a user notices: the
        // fullscreen row is where somebody actually watching the PC's screen is looking, and an
        // action present only in the windowed bar would be missing exactly when it is wanted.
        val source = screenSource()
        val split = source.indexOf(ORDER_COMMENT)
        assertTrue("the fullscreen control row's introducing comment is gone", split >= 0)

        assertTrue(
            "the windowed TopAppBar has no screenshot button",
            "action_take_screenshot" in actionsIn(source.substring(0, split)),
        )
        assertTrue(
            "the fullscreen control row has no screenshot button",
            "action_take_screenshot" in actionsIn(source.substring(split)),
        )
    }

    @Test
    fun `both screenshot buttons are wired to a handler`() {
        // PRESENCE IS NOT FUNCTION. Everything else here reads labels, so a button whose onClick was
        // emptied — during a refactor, or by a merge that dropped the callback — stays correctly
        // placed, correctly labelled, and completely inert, and no other check in this module would
        // notice. That is a realistic way for this feature to die silently, because the screen gives
        // no other sign: tapping a dead button looks exactly like tapping a live one whose PC is not
        // listening.
        val source = screenSource()

        // Matched INSIDE an onClick lambda, not merely "somewhere nearby": `[^}]*` cannot leave the
        // lambda, so a mention of onTakeScreenshot() in one of this file's explanatory comments
        // cannot stand in for the call itself.
        val wired =
            Regex("""R\.string\.action_take_screenshot\)\s*\)\s*\{[\s\S]*?onClick\s*=\s*\{[^}]*onTakeScreenshot\(\)""")
                .findAll(source)
                .count()

        assertEquals(
            "expected both screenshot buttons to call onTakeScreenshot(); found $wired wired",
            2,
            wired,
        )
    }

    private companion object {
        val TOOLTIP = Regex("""RemexTooltip\(\s*stringResource\(\s*R\.string\.([A-Za-z0-9_]+)""")

        const val ORDER_COMMENT = "Action order mirrors the windowed TopAppBar"

        /** The one slot the two bars fill with different strings, by design. */
        const val FULLSCREEN_TOGGLE = "<fullscreen toggle>"
        val FULLSCREEN_NAMES = setOf("cd_enter_fullscreen", "cd_exit_fullscreen")
    }
}
