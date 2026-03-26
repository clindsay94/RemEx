package com.clindsay94.remex.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.DpSize
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
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.clindsay94.remex.RemexCoreClient
import org.json.JSONObject

data class WidgetRemoteCommand(val id: String, val title: String, val action: String)

val WIDGET_REMOTE_COMMANDS = listOf(
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

private val SMALL = DpSize(110.dp, 40.dp)
private val MEDIUM = DpSize(180.dp, 100.dp)
private val LARGE = DpSize(250.dp, 160.dp)
private val XLARGE = DpSize(320.dp, 240.dp)

class RemoteControlWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Responsive(setOf(SMALL, MEDIUM, LARGE, XLARGE))

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
                .padding(8.dp),
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

    // Dynamic column count: fit as many ~70dp-wide buttons as possible
    val buttonMinWidth = 68.dp
    val availableWidth = size.width - 8.dp // 4dp padding each side
    val columns = ((availableWidth / buttonMinWidth).toInt()).coerceIn(1, commands.size)

    val showTitle = size.height >= 100.dp
    // Show all that fit: rows * columns
    val titleHeight = if (showTitle) 22.dp else 0.dp
    val contentHeight = size.height - 8.dp - titleHeight
    val buttonMinHeight = 30.dp
    val maxRows = (contentHeight / (buttonMinHeight + 2.dp)).toInt().coerceAtLeast(1)
    val maxItems = (columns * maxRows).coerceAtMost(commands.size)
    val visibleCommands = commands.take(maxItems)
    val rowCount = (visibleCommands.size + columns - 1) / columns
    val itemWidth = availableWidth / columns
    val itemHeight = (contentHeight / rowCount).coerceAtLeast(buttonMinHeight)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .padding(4.dp)
    ) {
        if (showTitle) {
            Text(
                "Remote Control",
                style = TextStyle(
                    color = GlanceTheme.colors.onSurface,
                    fontWeight = FontWeight.Bold,
                    fontSize = 13.sp
                ),
                modifier = GlanceModifier.padding(bottom = 2.dp, start = 2.dp)
            )
        }

        val rows = visibleCommands.chunked(columns)
        rows.forEach { rowItems ->
            Row(
                modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp)
            ) {
                rowItems.forEach { cmd ->
                    Box(
                        modifier = GlanceModifier
                            .width(itemWidth - 2.dp)
                            .height(itemHeight - 2.dp)
                            .background(GlanceTheme.colors.primaryContainer)
                            .cornerRadius(10.dp)
                            .clickable(
                                actionRunCallback<RemoteCommandCallback>(
                                    actionParametersOf(COMMAND_ACTION_PARAM to cmd.action)
                                )
                            )
                            .padding(2.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            cmd.title,
                            style = TextStyle(
                                color = GlanceTheme.colors.onPrimaryContainer,
                                fontSize = if (itemWidth >= 80.dp) 12.sp else 10.sp,
                                fontWeight = FontWeight.Medium
                            ),
                            maxLines = 1
                        )
                    }
                }
                // Fill remaining slots to keep left-alignment
                repeat(columns - rowItems.size) {
                    Box(modifier = GlanceModifier.width(itemWidth - 2.dp)) {}
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
            val request = JSONObject().apply {
                put("action", action)
                put("parameters", JSONObject())
            }
            RemexCoreClient.SendCommand(request.toString())
        }
    }
}

class RemoteControlWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = RemoteControlWidget()
}
