package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.DisplayTargetOption
import com.clindsay94.remex.ui.screens.matchesRememberedTarget
import com.clindsay94.remex.ui.screens.rememberedTargetFor
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers what the app stores for "the monitor I was watching last time" (RemEx-ynur).
 *
 * The app remembered a display by its `token`, which embeds `displayId` — the identifier the host
 * documents as renumbering whenever monitors are added, removed or replugged. So after a re-plug the
 * stored value still resolved, just to a DIFFERENT physical screen, and the user silently got someone
 * else's monitor with no error anywhere. RemEx-zftu gave the host a stable `persistentDisplayKey`;
 * this is the half that actually stores it.
 *
 * The migration is the part worth testing hardest. Existing installs hold `monitor:<displayId>`
 * strings, and the one thing that must never happen is reading one of those as a persistent key —
 * that would reintroduce the same wrong-screen bug through the new code path.
 */
class RememberedDisplayTargetTest {

    private fun monitor(
        displayId: String,
        persistentKey: String?,
        isPrimary: Boolean = false,
    ) = DisplayTargetOption(
        token = "monitor:$displayId",
        label = displayId,
        captureMode = "Monitor",
        displayId = displayId,
        isPrimary = isPrimary,
        persistentKey = persistentKey,
    )

    private val virtual = DisplayTargetOption(
        token = "virtual",
        label = "Both screens",
        captureMode = "VirtualDesktop",
        displayId = null,
        isPrimary = false,
    )

    private companion object {
        /** A real Windows monitor device interface path, as RemEx-zftu made the host emit. */
        const val LeftPanel =
            """\\?\DISPLAY#GSM5B09#5&1a2b3c4d&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"""
        const val RightPanel =
            """\\?\DISPLAY#DEL41A8#5&9f8e7d6c&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"""
    }

    @Test
    fun `a monitor is stored by its stable key, never by its session id`() {
        val stored = rememberedTargetFor(monitor(displayId = """\\.\DISPLAY1""", persistentKey = LeftPanel))

        assertEquals("monitorkey:$LeftPanel", stored)
        assertFalse(
            "the stored value must not embed the session-scoped display id",
            stored.contains("""\\.\DISPLAY1"""),
        )
    }

    @Test
    fun `the same panel is recognised after the host renumbers it`() {
        // THE WHOLE POINT. Unplugging another monitor renumbers this one from DISPLAY2 to DISPLAY1;
        // the panel is the same and the user's choice should survive.
        val stored = rememberedTargetFor(monitor("""\\.\DISPLAY2""", LeftPanel))

        val afterReplug = monitor("""\\.\DISPLAY1""", LeftPanel)

        assertTrue(matchesRememberedTarget(afterReplug, stored))
    }

    @Test
    fun `a different panel that inherited the old session id is NOT matched`() {
        // The failure being fixed, stated directly: the other monitor now answers to DISPLAY1. Under
        // the old token-based storage this matched and the user silently got the wrong screen.
        val stored = rememberedTargetFor(monitor("""\\.\DISPLAY1""", LeftPanel))

        val differentPanelSameId = monitor("""\\.\DISPLAY1""", RightPanel)

        assertFalse(matchesRememberedTarget(differentPanelSameId, stored))
    }

    @Test
    fun `a legacy stored value is ignored rather than reinterpreted`() {
        // THE MIGRATION GUARD. Every existing install holds one of these. Treating it as a persistent
        // key would reintroduce the wrong-screen bug through the new path, so it must simply not
        // match — the selection then falls through to the primary display, which is the honest
        // handling of "we no longer know which monitor you meant".
        val legacy = """monitor:\\.\DISPLAY1"""

        assertFalse(matchesRememberedTarget(monitor("""\\.\DISPLAY1""", LeftPanel), legacy))
        assertFalse(matchesRememberedTarget(monitor("""\\.\DISPLAY1""", null), legacy))
        assertFalse(matchesRememberedTarget(virtual, legacy))
    }

    @Test
    fun `the combined view still round-trips`() {
        val stored = rememberedTargetFor(virtual)

        assertEquals("virtual", stored)
        assertTrue(matchesRememberedTarget(virtual, stored))
        assertFalse(matchesRememberedTarget(monitor("""\\.\DISPLAY1""", LeftPanel), stored))
    }

    @Test
    fun `an older host with no stable key remembers nothing rather than the wrong thing`() {
        // Storing the session id as a fallback would be worse than not remembering: the user would
        // get a monitor they did not pick, silently, which is the bug. Losing the preference is
        // visible and harmless.
        val stored = rememberedTargetFor(monitor("""\\.\DISPLAY1""", persistentKey = null))

        assertEquals("", stored)
        assertFalse(matchesRememberedTarget(monitor("""\\.\DISPLAY1""", null), stored))
    }

    @Test
    fun `a blank or unrecognised stored value matches nothing`() {
        val candidates = listOf(monitor("""\\.\DISPLAY1""", LeftPanel), virtual)

        for (stored in listOf("", "   ", "monitorkey:", "nonsense", "monitor:")) {
            for (option in candidates) {
                assertFalse(
                    "stored value '$stored' must not match ${option.token}",
                    matchesRememberedTarget(option, stored),
                )
            }
        }
    }
}
