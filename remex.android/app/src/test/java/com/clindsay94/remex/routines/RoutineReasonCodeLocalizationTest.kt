package com.clindsay94.remex.routines

import com.clindsay94.remex.R
import com.clindsay94.remex.routines.model.RoutineMediaActions
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.w3c.dom.Element

/**
 * Every reason code has a message and a history label in all nine locales, wired to the right key,
 * with the same placeholders everywhere (spec §6.11, §10.1, §13.3; RemEx-pp0rt.5).
 *
 * The placeholder check matters more than it looks: routine messages use NAMED placeholders
 * (`{pc}`), which `scripts/check-localization.ps1` does not read (it compares printf `%1$s`). A
 * translation that drops `{pc}` would silently lose the PC's name in that language only.
 */
class RoutineReasonCodeLocalizationTest {
    private val resDir: File =
        listOf(File("src/main/res"), File("app/src/main/res")).firstOrNull { it.isDirectory } ?: error("res not found")

    private val locales = listOf("values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk")

    private fun strings(folder: String): Map<String, String> {
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(File(resDir, "$folder/strings.xml"))
        val nodes = doc.getElementsByTagName("string")
        return (0 until nodes.length).associate { (nodes.item(it) as Element).let { e -> e.getAttribute("name") to e.textContent } }
    }

    private val placeholder = Regex("""\{([a-z]+)\}|%\d\$[a-z]""")

    private fun placeholders(text: String): Set<String> = placeholder.findAll(text).map { it.value }.toSet()

    private fun stringId(name: String): Int = R.string::class.java.getField(name).getInt(null)

    @Test
    fun `every reason code is mapped, and to its own keys`() {
        assertEquals(RoutineReasonCodes.ALL.toSet(), RoutineReasonText.coveredCodes)
        for (code in RoutineReasonCodes.ALL) {
            assertEquals("message key for $code", stringId("routine_reason_$code"), RoutineReasonText.messageRes(code))
            assertEquals("history key for $code", stringId("routine_history_$code"), RoutineReasonText.historyRes(code))
        }
        // A code from a newer PC reads as internal_error, never as nothing.
        assertEquals(stringId("routine_reason_internal_error"), RoutineReasonText.messageRes("a_code_from_2027"))
    }

    @Test
    fun `every action token a step can produce has a phrase`() {
        val tokens =
            RoutinePowerVerbs.ALL.toSet() +
                RoutineMediaActions.ALL.map { RoutineActionTokens.MEDIA_PREFIX + it } +
                RoutineActionTokens.LAUNCH_APP + RoutineActionTokens.NOTIFY_PC
        assertEquals(tokens, RoutineReasonText.coveredActions)
    }

    @Test
    fun `all nine locales carry every routine string with English's placeholders`() {
        val english = strings("values")
        val routineKeys = english.keys.filter { it.startsWith("routine_") }
        val required =
            RoutineReasonCodes.ALL.flatMap { listOf("routine_reason_$it", "routine_history_$it") }
        assertTrue("English is missing ${required - routineKeys.toSet()}", routineKeys.containsAll(required))

        val problems = mutableListOf<String>()
        for (folder in locales) {
            val values = strings(folder)
            for (key in routineKeys) {
                val text = values[key]
                if (text.isNullOrBlank()) {
                    problems += "$folder: $key missing"
                    continue
                }
                if (placeholders(text) != placeholders(english.getValue(key))) {
                    problems += "$folder: $key has ${placeholders(text)}, English has ${placeholders(english.getValue(key))}"
                }
                val unknown = placeholder.findAll(text).mapNotNull { it.groups[1]?.value }.filterNot { it in RoutineMessageTemplate.TOKENS }.toList()
                if (unknown.isNotEmpty()) problems += "$folder: $key uses unknown placeholders $unknown"
            }
        }
        assertEquals(emptyList<String>(), problems)
    }

    @Test
    fun `templates fill named placeholders once and leave unknown ones visible`() {
        assertEquals(
            "Asking Gaming PC to open {pc}",
            RoutineMessageTemplate.fill("Asking {pc} to {action}", mapOf("pc" to "Gaming PC", "action" to "open {pc}")),
        )
        assertEquals("{nope} x", RoutineMessageTemplate.fill("{nope} {n}", mapOf("n" to "x")))
        assertEquals(setOf("pc", "duration"), RoutineMessageTemplate.tokensIn("{pc} didn't come online within {duration}."))
    }
}
