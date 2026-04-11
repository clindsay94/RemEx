package com.clindsay94.remex.widget

import android.appwidget.AppWidgetManager
import android.content.Intent
import android.os.Bundle
import android.util.Log
import androidx.appcompat.app.AppCompatActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateSetOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.state.updateAppWidgetState
import androidx.glance.state.PreferencesGlanceStateDefinition
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import org.json.JSONObject

data class ConfigSensor(val id: String, val name: String, val category: String)

private val DEFAULT_SENSORS = listOf(
    ConfigSensor("sensor:cpu", "CPU Usage", "CPU"),
    ConfigSensor("sensor:gpu", "GPU Usage", "GPU"),
    ConfigSensor("sensor:ram", "RAM Usage", "Memory"),
    ConfigSensor("sensor:cpu_temp", "CPU Temperature", "CPU"),
    ConfigSensor("sensor:gpu_temp", "GPU Temperature", "GPU"),
)

class HardwareInfoConfigActivity : AppCompatActivity() {

    private var appWidgetId = AppWidgetManager.INVALID_APPWIDGET_ID

    @OptIn(ExperimentalMaterial3Api::class)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        appWidgetId = intent?.extras?.getInt(
            AppWidgetManager.EXTRA_APPWIDGET_ID,
            AppWidgetManager.INVALID_APPWIDGET_ID
        ) ?: AppWidgetManager.INVALID_APPWIDGET_ID

        if (appWidgetId == AppWidgetManager.INVALID_APPWIDGET_ID) {
            finish()
            return
        }

        setResult(RESULT_CANCELED)

        val availableSensors = loadAvailableSensors()

        setContent {
            val colorScheme = if (isSystemInDarkTheme()) darkColorScheme() else lightColorScheme()
            MaterialTheme(colorScheme = colorScheme) {
                val selected = remember { mutableStateSetOf<String>() }

                Scaffold(
                    topBar = {
                        TopAppBar(
                            title = {
                                Text(
                                    stringResource(R.string.widget_config_hardware_info_title),
                                    fontWeight = FontWeight.Bold
                                )
                            }
                        )
                    }
                ) { padding ->
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(padding)
                    ) {
                        Text(
                            stringResource(R.string.widget_config_hardware_info_hint),
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )

                        if (availableSensors.isEmpty()) {
                            Box(
                                modifier = Modifier
                                    .weight(1f)
                                    .fillMaxWidth(),
                                contentAlignment = Alignment.Center
                            ) {
                                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                    Text(
                                        stringResource(R.string.widget_config_no_sensors),
                                        style = MaterialTheme.typography.bodyLarge
                                    )
                                    Text(
                                        stringResource(R.string.widget_config_no_sensors_hint),
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
                                items(availableSensors) { sensor ->
                                    val checked = sensor.id in selected
                                    SensorCheckRow(
                                        name = sensor.name,
                                        category = sensor.category,
                                        checked = checked,
                                        onToggle = {
                                            if (checked) selected.remove(sensor.id)
                                            else selected.add(sensor.id)
                                        }
                                    )
                                }
                            }
                        }

                        Button(
                            onClick = {
                                lifecycleScope.launch {
                                    saveAndUpdate(selected.toSet())
                                }
                            },
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
        }
    }

    private fun loadAvailableSensors(): List<ConfigSensor> {
        val json = WidgetDataCache.getTelemetryJson(this)
        if (json.isNullOrBlank()) return DEFAULT_SENSORS

        return try {
            val root = JSONObject(json)
            val sensors = root.optJSONArray("sensors") ?: return DEFAULT_SENSORS
            val parsed = mutableListOf<ConfigSensor>()
            for (i in 0 until sensors.length()) {
                val obj = sensors.getJSONObject(i)
                parsed.add(
                    ConfigSensor(
                        id = obj.optString("id", "sensor_$i"),
                        name = obj.optString("name", "Sensor $i"),
                        category = obj.optString("category", "")
                    )
                )
            }
            parsed.ifEmpty { DEFAULT_SENSORS }
        } catch (e: Exception) {
            Log.w("HWConfigActivity", "Failed to parse cached telemetry", e)
            DEFAULT_SENSORS
        }
    }

    private suspend fun saveAndUpdate(selectedIds: Set<String>) {
        val glanceId = GlanceAppWidgetManager(this).getGlanceIdBy(appWidgetId)
        updateAppWidgetState(this, PreferencesGlanceStateDefinition, glanceId) { prefs ->
            prefs.toMutablePreferences().apply {
                this[SELECTED_SENSORS_KEY] = selectedIds.joinToString(",")
            }
        }
        HardwareInfoWidget().update(this, glanceId)

        val result = Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, appWidgetId)
        setResult(RESULT_OK, result)
        finish()
    }
}

@Composable
private fun SensorCheckRow(
    name: String,
    category: String,
    checked: Boolean,
    onToggle: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onToggle)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Checkbox(checked = checked, onCheckedChange = { onToggle() })
        Spacer(modifier = Modifier.height(8.dp))
        Column(modifier = Modifier.padding(start = 8.dp)) {
            Text(text = name, style = MaterialTheme.typography.bodyLarge)
            if (category.isNotBlank()) {
                Text(
                    text = category,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}
