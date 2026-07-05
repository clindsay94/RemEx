package com.clindsay94.remex.widget

import android.appwidget.AppWidgetManager
import android.content.Intent
import android.graphics.BitmapFactory
import android.os.Bundle
import android.util.Base64
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateSetOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.state.updateAppWidgetState
import androidx.glance.state.PreferencesGlanceStateDefinition
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import org.json.JSONArray

data class ConfigAppEntry(
    val name: String,
    val path: String,
    val iconBase64: String?
)

class AppLauncherConfigActivity : ComponentActivity() {

    private var appWidgetId = AppWidgetManager.INVALID_APPWIDGET_ID

    @OptIn(ExperimentalMaterial3Api::class)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        appWidgetId = intent?.extras?.getInt(
            AppWidgetManager.EXTRA_APPWIDGET_ID,
            AppWidgetManager.INVALID_APPWIDGET_ID
        ) ?: AppWidgetManager.INVALID_APPWIDGET_ID

        if (appWidgetId == AppWidgetManager.INVALID_APPWIDGET_ID) {
            finish()
            return
        }

        setResult(RESULT_CANCELED)

        val availableApps = loadAvailableApps()

        setContent {
            RemExTheme {
                AppLauncherConfigScreen(
                    availableApps = availableApps,
                    onDone = { selected ->
                        lifecycleScope.launch {
                            saveAndUpdate(selected)
                        }
                    }
                )
            }
        }
    }

    private fun loadAvailableApps(): List<ConfigAppEntry> {
        val json = WidgetDataCache.getLauncherJson(this)
        if (json.isNullOrBlank()) return emptyList()

        return try {
            val array = JSONArray(json)
            List(array.length()) { i ->
                val obj = array.getJSONObject(i)
                ConfigAppEntry(
                    name = obj.optString("displayName", "App"),
                    path = obj.optString("targetPath", ""),
                    iconBase64 = obj.optString("iconBase64").takeIf { it.isNotEmpty() }
                )
            }.distinctBy { it.path }
        } catch (e: Exception) {
            Log.w("AppLauncherConfig", "Failed to parse cached launcher data", e)
            emptyList()
        }
    }

    private suspend fun saveAndUpdate(selectedPaths: Set<String>) {
        val glanceId = GlanceAppWidgetManager(this).getGlanceIdBy(appWidgetId)
        updateAppWidgetState(this, PreferencesGlanceStateDefinition, glanceId) { prefs ->
            prefs.toMutablePreferences().apply {
                // Use newline separator since paths may contain commas
                this[SELECTED_APPS_KEY] = selectedPaths.joinToString("\n")
            }
        }
        AppLauncherWidget().update(this, glanceId)

        val result = Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, appWidgetId)
        setResult(RESULT_OK, result)
        finish()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppLauncherConfigScreen(
    availableApps: List<ConfigAppEntry>,
    onDone: (Set<String>) -> Unit
) {
    val selected = remember { mutableStateSetOf<String>() }

    Scaffold(
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.widget_config_app_launcher_title)
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            Text(
                stringResource(R.string.widget_config_app_launcher_hint),
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            if (availableApps.isEmpty()) {
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxWidth(),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                            stringResource(R.string.widget_config_no_apps),
                            style = MaterialTheme.typography.bodyLarge
                        )
                        Text(
                            stringResource(R.string.widget_config_no_apps_hint),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            } else {
                LazyColumn(
                    modifier = Modifier.weight(1f),
                    verticalArrangement = Arrangement.spacedBy(2.dp)
                ) {
                    items(availableApps) { app ->
                        val checked = app.path in selected
                        AppCheckRow(
                            app = app,
                            checked = checked,
                            onToggle = {
                                if (checked) selected.remove(app.path)
                                else selected.add(app.path)
                            }
                        )
                    }
                }
            }

            Button(
                onClick = { onDone(selected.toSet()) },
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(16.dp),
                enabled = selected.isNotEmpty()
            ) {
                Text(stringResource(R.string.button_done))
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun AppLauncherConfigScreenPreview() {
    RemExTheme {
        AppLauncherConfigScreen(
            availableApps = listOf(
                ConfigAppEntry("Browser", "C:\\Path", null),
                ConfigAppEntry("Editor", "D:\\Path", null)
            ),
            onDone = {}
        )
    }
}

@Composable
private fun AppCheckRow(
    app: ConfigAppEntry,
    checked: Boolean,
    onToggle: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onToggle)
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Checkbox(checked = checked, onCheckedChange = { onToggle() })

        if (app.iconBase64 != null) {
            val bitmap = remember(app.iconBase64) {
                try {
                    val bytes = Base64.decode(app.iconBase64, Base64.DEFAULT)
                    BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                } catch (_: Exception) {
                    null
                }
            }
            if (bitmap != null) {
                Image(
                    bitmap = bitmap.asImageBitmap(),
                    contentDescription = app.name,
                    modifier = Modifier
                        .size(40.dp)
                        .padding(start = 8.dp)
                        .clip(RoundedCornerShape(8.dp))
                )
            }
        }

        Text(
            text = app.name,
            style = MaterialTheme.typography.bodyLarge,
            modifier = Modifier.padding(start = 12.dp)
        )
    }
}
