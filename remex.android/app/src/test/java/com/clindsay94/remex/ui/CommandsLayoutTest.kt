package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.CommandGroup
import com.clindsay94.remex.ui.screens.CommandsLayout
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The Commands screen's groups and order (RemEx-kq10x.3, cohesion spec decision 5).
 *
 * Standard and Forced must match the PC Commands page action for action and in the same order
 * (remex.desktop/Views/RemoteView.axaml), and every action the screen had before the restyle must
 * still have a home - the floating toolbar's screenshot and clipboard buttons included.
 */
class CommandsLayoutTest {

    @Test
    fun `groups and order are pinned, with the PC's two groups first after Wake`() {
        assertEquals(
                listOf(
                        CommandGroup.WAKE to listOf("WakeOnLan"),
                        CommandGroup.STANDARD to listOf("Lock", "SignOut", "Shutdown", "Restart", "Sleep", "Hibernate"),
                        CommandGroup.FORCED to listOf("ForceShutdown", "ForceRestart", "RestartToUefi"),
                        CommandGroup.DISPLAY to listOf("MonitorOff"),
                        CommandGroup.PC_TOOLS to listOf("Screenshot", "SendClipboard", "FetchClipboard")
                ),
                CommandsLayout.groups
        )
    }

    @Test
    fun `every action appears exactly once`() {
        val all = CommandsLayout.groups.flatMap { it.second }
        assertEquals("an action is listed twice: $all", all.toSet().size, all.size)
    }

    @Test
    fun `every laid-out action has a card on the screen, and every card is laid out`() {
        val source = File("src/main/java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt")
        assertTrue("Could not locate RemoteControlScreen.kt at ${source.absolutePath}", source.exists())
        val cardActions =
                Regex("""RemoteCommandCard\(\s*"[^"]+",\s*R\.string\.\w+,\s*"([^"]+)"""")
                        .findAll(source.readText())
                        .map { it.groupValues[1] }
                        .toSet()
        assertTrue("Found no RemoteCommandCard declarations - the scan no longer matches.", cardActions.isNotEmpty())
        assertEquals(CommandsLayout.groups.flatMap { it.second }.toSet(), cardActions)
    }

    @Test
    fun `a PC that does not say what it supports shows everything`() {
        assertEquals(CommandsLayout.groups, CommandsLayout.visibleGroups(null))
    }

    @Test
    fun `actions the PC leaves out are hidden, and an emptied group goes with them`() {
        // A desktop with no hibernation file, BIOS firmware and no X11 for MONITOROFF.
        val advertised = listOf("SHUTDOWN", "FORCESHUTDOWN", "RESTART", "FORCERESTART", "SIGNOUT", "SLEEP", "LOCK")

        assertEquals(
                listOf(
                        CommandGroup.WAKE to listOf("WakeOnLan"),
                        CommandGroup.STANDARD to listOf("Lock", "SignOut", "Shutdown", "Restart", "Sleep"),
                        CommandGroup.FORCED to listOf("ForceShutdown", "ForceRestart"),
                        CommandGroup.PC_TOOLS to listOf("Screenshot", "SendClipboard", "FetchClipboard")
                ),
                CommandsLayout.visibleGroups(advertised)
        )
    }

    @Test
    fun `every PC action maps to a routine power verb the PC can advertise`() {
        val pcActions = CommandsLayout.groups.flatMap { it.second } - setOf("WakeOnLan", "Screenshot", "SendClipboard", "FetchClipboard")
        for (action in pcActions) {
            val verb = CommandsLayout.powerVerbOf(action)
            assertTrue(
                    "$action has no power verb, so the capability can never hide it",
                    verb != null && verb in com.clindsay94.remex.routines.model.RoutinePowerVerbs.ALL
            )
        }
    }
}
