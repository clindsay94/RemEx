package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Every remote-control command must be explicitly classified as destructive or not, in the one
 * place that decides it.
 *
 * RemEx-awks shipped Sign Out with no confirmation prompt. The flag was a bare positional Boolean
 * among six positional arguments, and the values had been assigned by CATEGORY - every POWER card
 * true, every SESSION and ENERGY card false. Sign Out sits in SESSION beside Wake and Lock, so it
 * inherited `false` by association. A new card is written by copying a neighbour, and copying the
 * wrong neighbour is exactly how that happened (RemEx-q4sm).
 *
 * `actionDiscardsWork` now derives the answer, and its `else` branch confirms - the safe direction
 * for an action nobody classified. This test exists so that fallback stays a safety net rather than
 * somewhere cards quietly accumulate: every action a card actually uses must appear in one of the
 * two explicit branches.
 */
class RemoteControlConfirmationTests {

    private val source: String by lazy {
        // Unit tests run with the module root (remex.android/app) as the working directory, matching
        // RemexConnectionServiceContractTests.
        val file = File("src/main/java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt")
        assertTrue(
                "Could not locate RemoteControlScreen.kt at ${file.absolutePath} - test setup is " +
                        "broken, not the code under test.",
                file.exists()
        )
        file.readText()
    }

    /** The `action` string of every card in the grid. */
    private val cardActions: List<String> by lazy {
        Regex("""RemoteCommandCard\(\s*"[^"]+",\s*R\.string\.\w+,\s*"([^"]+)"""")
                .findAll(source)
                .map { it.groupValues[1] }
                .toList()
    }

    /** The two explicit arms of `actionDiscardsWork`, as (action -> confirms) pairs. */
    private val classified: Map<String, Boolean> by lazy {
        val body = Regex(
                        """private fun actionDiscardsWork\(action: String\): Boolean = when \(action\) \{(.*?)\n\}""",
                        RegexOption.DOT_MATCHES_ALL
                )
                .find(source)
                ?.groupValues
                ?.get(1)
                .orEmpty()

        assertTrue("Could not find actionDiscardsWork - this test is measuring nothing.", body.isNotEmpty())

        // Each arm is a run of quoted actions terminated by `-> true` or `-> false`. The `else`
        // branch has no quoted actions, so it contributes nothing and cannot mask a missing entry.
        Regex("""((?:\s*"[^"]+",)*\s*"[^"]+")\s*->\s*(true|false)""")
                .findAll(body)
                .flatMap { arm ->
                    val confirms = arm.groupValues[2].toBoolean()
                    Regex(""""([^"]+)"""").findAll(arm.groupValues[1]).map { it.groupValues[1] to confirms }
                }
                .toMap()
    }

    @Test
    fun `every command card action is explicitly classified`() {
        assertTrue("Found no RemoteCommandCard declarations - the scan no longer matches.", cardActions.isNotEmpty())

        val unclassified = cardActions.filterNot { classified.containsKey(it) }

        assertEquals(
                "These actions fall through to the fail-safe `else` branch of actionDiscardsWork, so " +
                        "they confirm by accident rather than by decision. Add each to the true or " +
                        "false arm: $unclassified",
                emptyList<String>(),
                unclassified
        )
    }

    /**
     * The classification itself, pinned. Guards the direction of the answer, not merely that one
     * exists - flipping Sign Out back to `false` is the original defect and must fail here.
     */
    @Test
    fun `destructive commands confirm and reversible ones do not`() {
        val mustConfirm = listOf("SignOut", "Shutdown", "ForceShutdown", "Restart", "ForceRestart", "RestartToUefi")
        val mustNotConfirm = listOf("WakeOnLan", "Lock", "Sleep", "Hibernate", "MonitorOff")

        for (action in mustConfirm) {
            assertEquals(
                    "$action closes programs or discards unsaved work, so it must require confirmation.",
                    true,
                    classified[action]
            )
        }
        for (action in mustNotConfirm) {
            assertEquals(
                    "$action is reversible and loses nothing, so prompting for it trains people to " +
                            "dismiss prompts without reading them.",
                    false,
                    classified[action]
            )
        }

        assertEquals(
                "Every card in the grid should be covered by one of the two lists above; update this " +
                        "test deliberately when a command is added.",
                cardActions.sorted(),
                (mustConfirm + mustNotConfirm).filter { it in cardActions }.sorted()
        )
    }
}
