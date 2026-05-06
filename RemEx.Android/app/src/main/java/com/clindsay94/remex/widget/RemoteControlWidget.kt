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
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.first
import org.json.JSONObject

data class WidgetRemoteCommand(val id: String, val title: String, val action: String)

val WIDGET_REMOTE_COMMANDS = listOf(
    WidgetRemoteCommand("wake", "Wake PC", "WakeOnLan"),
    WidgetRemoteCommand("lock", "Lock PC", "Lock"),
    WidgetRemoteCommand("shutdown", "Shutdown", "Shutdown"),
    WidgetRemoteCommand("restart", "Restart", "Restart"),
    WidgetRemoteCommand("uefi", "UEFI Reboot", "RestartToUefi"),
    WidgetRemoteCommand("force_shutdown", "Force Off", "ForceShutdown"),
    WidgetRemoteCommand("force_restart", "Force Restart", "ForceRestart"),
    WidgetRemoteCommand("sleep", "Sleep", "Sleep"),
    WidgetRemoteCommand("hibernate", "Hibernate", "Hibernate"),
    WidgetRemoteCommand("monitor_off", "Monitor Off", "MonitorOff"),
    WidgetRemoteCommand("logoff", "Log Off", "SignOut"),
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
                .padding(12.dp),
            contentAlignment = Alignment.Center
        ) {
            Text(
                "Tap to configure",
                style = TextStyle(
                    color = GlanceTheme.colors.onSurfaceVariant,
                    fontSize = 12.sp
                )
            )
        }
        return
    }

    val outerPadding = 8.dp
    val availableWidth = size.width - (outerPadding * 2)
    val availableHeight = size.height - (outerPadding * 2)

    val buttonMinWidth = 90.dp
    val buttonMinHeight = 36.dp
    val columns = (availableWidth / (buttonMinWidth + 4.dp)).toInt().coerceIn(1, 4)
    
    val showTitle = size.height >= 80.dp
    val titleHeight = if (showTitle) 24.dp else 0.dp
    val contentHeight = availableHeight - titleHeight
    
    val maxRows = (contentHeight / (buttonMinHeight + 4.dp)).toInt().coerceAtLeast(1)
    val maxItems = (columns * maxRows).coerceAtMost(commands.size)
    val visibleCommands = commands.take(maxItems)
    
    val itemWidth = (availableWidth / columns) - 4.dp
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
                "Remote Control",
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
                            cmd.title,
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
        if (RemexCoreClient.isLibraryLoaded) {
            val settings = SettingsManager(context)
            if (action == "WakeOnLan") {
                val mac = settings.macAddressFlow.first()
                val broadcast = settings.broadcastIpFlow.first()
                if (mac.isNotBlank()) {
                    RemexCoreClient.WakePc(mac, broadcast, 9)
                }
            } else {
                val request = JSONObject().apply {
                    put("action", action)
                    put("parameters", JSONObject())
                }
                RemexCoreClient.SendCommand(request.toString())
            }
        }
    }
}

class RemoteControlWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = RemoteControlWidget()
}
