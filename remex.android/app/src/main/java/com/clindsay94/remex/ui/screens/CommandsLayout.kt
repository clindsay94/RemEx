package com.clindsay94.remex.ui.screens

import androidx.annotation.StringRes
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.model.RoutinePowerVerbs

/**
 * The groups of the Commands screen, top to bottom (RemEx-kq10x.3, cohesion spec decision 5).
 *
 * [STANDARD] and [FORCED] are the PC Commands page's two groups (remex.desktop/Views/RemoteView.axaml):
 * the same actions, in the same order, under the same names, so a person who learns one app has
 * learned the other. Everything else is phone-only and sits around them: [WAKE] first and unlabelled,
 * because it is the one action that works while the PC is off, and the extras after.
 *
 * The docked media mini-player is phone-only too, but it is not a grid group: it stays on the nav bar
 * where the thumb lands (RemEx-vtorl.5).
 */
internal enum class CommandGroup(@param:StringRes val labelRes: Int?) {
    WAKE(null),
    STANDARD(R.string.rc_group_standard),
    FORCED(R.string.rc_group_forced),
    DISPLAY(R.string.rc_group_display),
    PC_TOOLS(R.string.rc_group_tools)
}

/** What the Commands screen shows, in order, and which of it the connected PC can actually do. Pure JVM. */
internal object CommandsLayout {

    /**
     * Every action on the screen, by group, in display order. One list, so the order lives in one
     * place: the screen draws exactly this, and CommandsLayoutTest pins it against the PC page.
     */
    val groups: List<Pair<CommandGroup, List<String>>> =
            listOf(
                    CommandGroup.WAKE to listOf("WakeOnLan"),
                    CommandGroup.STANDARD to listOf("Lock", "SignOut", "Shutdown", "Restart", "Sleep", "Hibernate"),
                    CommandGroup.FORCED to listOf("ForceShutdown", "ForceRestart", "RestartToUefi"),
                    CommandGroup.DISPLAY to listOf("MonitorOff"),
                    CommandGroup.PC_TOOLS to listOf("Screenshot", "SendClipboard", "FetchClipboard")
            )

    /**
     * The `routinePowerVerbs` name the PC advertises for [action], or null for an action that is not
     * a PC power verb: Wake runs on the phone, and the screenshot and clipboard actions are separate
     * features with their own capability handling.
     */
    fun powerVerbOf(action: String): String? =
            when (action) {
                "Lock" -> RoutinePowerVerbs.LOCK
                "SignOut" -> RoutinePowerVerbs.SIGN_OUT
                "Shutdown" -> RoutinePowerVerbs.SHUTDOWN
                "Restart" -> RoutinePowerVerbs.RESTART
                "Sleep" -> RoutinePowerVerbs.SLEEP
                "Hibernate" -> RoutinePowerVerbs.HIBERNATE
                "ForceShutdown" -> RoutinePowerVerbs.FORCE_SHUTDOWN
                "ForceRestart" -> RoutinePowerVerbs.FORCE_RESTART
                "RestartToUefi" -> RoutinePowerVerbs.RESTART_TO_UEFI
                "MonitorOff" -> RoutinePowerVerbs.MONITOR_OFF
                else -> null
            }

    /**
     * Whether to show [action] for a PC that advertised [advertised]. Null means the PC did not say
     * (an older PC, or nothing has arrived yet), and then everything shows: hiding a button the PC
     * might well support is worse than showing one it refuses.
     */
    fun isOffered(action: String, advertised: List<String>?): Boolean {
        val verb = powerVerbOf(action) ?: return true
        return advertised == null || verb in advertised
    }

    /** [groups] with the actions [advertised] rules out removed, and any group left empty dropped. */
    fun visibleGroups(advertised: List<String>?): List<Pair<CommandGroup, List<String>>> =
            groups.map { (group, actions) -> group to actions.filter { isOffered(it, advertised) } }
                    .filter { (_, actions) -> actions.isNotEmpty() }
}
