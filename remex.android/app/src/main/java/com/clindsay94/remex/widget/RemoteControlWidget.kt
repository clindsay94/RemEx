package com.clindsay94.remex.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.LocalSize
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.action.actionStartActivity
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.currentState
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.clindsay94.remex.R
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.first

data class WidgetRemoteCommand(val id: String, val titleRes: Int, val action: String)

val WIDGET_REMOTE_COMMANDS = listOf(
    WidgetRemoteCommand("wake", R.string.rc_wake_pc, "WakeOnLan"),
    WidgetRemoteCommand("lock", R.string.rc_lock_pc, "Lock"),
    WidgetRemoteCommand("shutdown", R.string.rc_shutdown, "Shutdown"),
    WidgetRemoteCommand("restart", R.string.rc_restart, "Restart"),
    WidgetRemoteCommand("uefi", R.string.rc_reboot_uefi, "RestartToUefi"),
    WidgetRemoteCommand("force_shutdown", R.string.rc_force_shutdown, "ForceShutdown"),
    WidgetRemoteCommand("force_restart", R.string.rc_force_restart, "ForceRestart"),
    WidgetRemoteCommand("sleep", R.string.rc_sleep, "Sleep"),
    WidgetRemoteCommand("hibernate", R.string.rc_hibernate, "Hibernate"),
    WidgetRemoteCommand("monitor_off", R.string.rc_monitor_off, "MonitorOff"),
    WidgetRemoteCommand("logoff", R.string.rc_logoff, "SignOut"),
)

val SELECTED_COMMANDS_KEY = stringPreferencesKey("selected_commands")
val COMMAND_ACTION_PARAM = ActionParameters.Key<String>("command_action")

class RemoteControlWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        provideContent {
            GlanceTheme {
                RemoteControlContent()
            }
        }
    }
}

@Composable
private fun RemoteControlContent() {
    val context = androidx.glance.LocalContext.current
    val prefs = currentState<Preferences>()
    val selectedStr = prefs[SELECTED_COMMANDS_KEY] ?: ""
    val selectedIds = selectedStr.split(",").filter { it.isNotBlank() }.toSet()
    val commands = WIDGET_REMOTE_COMMANDS.filter { it.id in selectedIds }
    val size = LocalSize.current

    if (commands.isEmpty()) {
        Box(
            modifier = GlanceModifier.fillMaxSize()
                .background(GlanceTheme.colors.surface)
                .cornerRadius(16.dp)
                .clickable(actionStartActivity<MainActivity>())
                .padding(12.dp),
            contentAlignment = Alignment.Center
        ) {
            Text(
                context.getString(R.string.widget_tap_to_open),
                style = TextStyle(
                    color = GlanceTheme.colors.onSurfaceVariant,
                    fontSize = 12.sp
                )
            )
        }
        return
    }

    val outerPadding = 8.dp
    val availableWidth = (size.width - (outerPadding * 2)).coerceAtLeast(0.dp)
    val availableHeight = (size.height - (outerPadding * 2)).coerceAtLeast(0.dp)

    val buttonMinWidth = 90.dp
    val buttonMinHeight = 36.dp
    val columns = (availableWidth / (buttonMinWidth + 4.dp)).toInt().coerceIn(1, 4)

    val showTitle = size.height >= 80.dp
    val titleHeight = if (showTitle) 24.dp else 0.dp
    val contentHeight = (availableHeight - titleHeight).coerceAtLeast(0.dp)

    val maxRows = (contentHeight / (buttonMinHeight + 4.dp)).toInt().coerceAtLeast(1)
    val maxItems = (columns * maxRows).coerceAtMost(commands.size)
    val visibleCommands = commands.take(maxItems)

    val itemWidth = ((availableWidth / columns) - 4.dp).coerceAtLeast(0.dp)
    val currentRows = (visibleCommands.size + columns - 1) / columns
    val itemHeight = (contentHeight / currentRows).coerceIn(buttonMinHeight, 60.dp)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .padding(outerPadding)
    ) {
        if (showTitle) {
            Text(
                context.getString(R.string.screen_remote_control_title),
                style = TextStyle(
                    color = GlanceTheme.colors.onSurface,
                    fontWeight = FontWeight.Bold,
                    fontSize = 14.sp
                ),
                modifier = GlanceModifier.padding(bottom = 6.dp, start = 4.dp)
            )
        }

        val rows = visibleCommands.chunked(columns)
        rows.forEach { rowItems ->
            Row(
                modifier = GlanceModifier.fillMaxWidth().padding(vertical = 2.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                rowItems.forEach { cmd ->
                    Box(
                        modifier = GlanceModifier
                            .width(itemWidth)
                            .height(itemHeight)
                            .background(GlanceTheme.colors.primaryContainer)
                            .cornerRadius(12.dp)
                            .clickable(
                                actionRunCallback<RemoteCommandCallback>(
                                    actionParametersOf(COMMAND_ACTION_PARAM to cmd.action)
                                )
                            )
                            .padding(4.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            WidgetText.ellipsize(context.getString(cmd.titleRes), WidgetText.ControlLabelBudget),
                            style = TextStyle(
                                color = GlanceTheme.colors.onPrimaryContainer,
                                fontSize = if (itemWidth >= 100.dp) 12.sp else 10.sp,
                                fontWeight = FontWeight.Bold
                            ),
                            maxLines = 1
                        )
                    }
                    if (rowItems.indexOf(cmd) < rowItems.lastIndex) {
                        Spacer(modifier = GlanceModifier.width(4.dp))
                    }
                }
            }
        }
    }
}

class RemoteCommandCallback : ActionCallback {
    override suspend fun onAction(
        context: Context,
        glanceId: GlanceId,
        parameters: ActionParameters
    ) {
        val action = parameters[COMMAND_ACTION_PARAM] ?: return
        val title = WIDGET_REMOTE_COMMANDS.firstOrNull { it.action == action }?.titleRes?.let { context.getString(it) } ?: "Command"

        if (!RemexCoreClient.isLibraryLoaded) {
            widgetToast(context, context.getString(R.string.widget_toast_remex_not_ready))
            return
        }

        val settings = SettingsManager(context)
        if (action == "WakeOnLan") {
            // Wake-on-LAN is a broadcast packet — it works precisely when the PC is
            // asleep/disconnected, so it must NOT require a live connection.
            val mac = settings.macAddressFlow.first()
            val broadcast = settings.broadcastIpFlow.first()
            if (mac.isBlank()) {
                widgetToast(context, context.getString(R.string.widget_toast_set_mac))
                return
            }
            // Handed over rather than awaited, like the commands below: this is inside a goAsync()
            // broadcast window and the native wake now waits for the send (RemEx-52n0).
            sendWidgetWake(mac, broadcast, 9)
            widgetToast(context, context.getString(R.string.widget_toast_wol_sent))
        } else {
            if (!RemexClientManager.isConnected.value) {
                widgetToast(context, context.getString(R.string.widget_toast_not_connected))
                return
            }
            // Handed over rather than awaited: this runs inside a goAsync() broadcast window, and
            // SendCommand now waits for the PC's answer on a ten-second budget — the same order of
            // magnitude as the window it would have to fit inside (RemEx-66rf).
            sendWidgetCommand(action)
            widgetToast(context, context.getString(R.string.widget_toast_command_sent, title))
        }
    }
}

class RemoteControlWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = RemoteControlWidget()
}
