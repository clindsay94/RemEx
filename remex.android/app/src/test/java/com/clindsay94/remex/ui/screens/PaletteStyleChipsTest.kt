package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** The nine wire strings are offered by the chips AND have a label branch AND a string in every locale (RemEx-4kv0g.7). */
class PaletteStyleChipsTest {
    private val styles = listOf("tonal_spot", "expressive", "fruit_salad", "rainbow", "vibrant", "neutral", "monochrome", "fidelity", "content")
    private val repoRoot = File(System.getProperty("remex.repoRoot") ?: error("remex.repoRoot system property not set"))
    private val screen = File(repoRoot, "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/PersonalizationScreen.kt").readText()

    @Test
    fun `every style is offered as a chip option and labelled`() {
        for (s in styles) {
            assertTrue("chip option \"$s\"", Regex("\"$s\",?\\s*$", RegexOption.MULTILINE).containsMatchIn(screen))
            assertTrue("label branch for $s", screen.contains("\"$s\" -> stringResource(R.string.personalization_style_$s)"))
        }
    }

    @Test
    fun `every locale carries every style string`() {
        val locales = listOf("values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk")
        for (locale in locales) {
            val xml = File(repoRoot, "remex.android/app/src/main/res/$locale/strings.xml").readText()
            for (s in styles) assertTrue("$locale: personalization_style_$s", xml.contains("<string name=\"personalization_style_$s\">"))
        }
    }
}
