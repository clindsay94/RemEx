package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Help's row chevrons point the reading direction, so they flip in a right-to-left layout (R10). */
class HelpScreenIconTest {
    private val source = java.io.File("src/main/java/com/clindsay94/remex/ui/screens/HelpScreen.kt").readText()

    @Test
    fun `row chevrons are auto-mirrored`() {
        assertTrue(source.contains("Icons.AutoMirrored.Filled.KeyboardArrowRight"))
        assertFalse(
                "a fixed right chevron points backwards in RTL",
                Regex("""Icons\.(Default|Filled|Outlined|Rounded)\.(ChevronRight|KeyboardArrowRight)\b""").containsMatchIn(source),
        )
    }
}
