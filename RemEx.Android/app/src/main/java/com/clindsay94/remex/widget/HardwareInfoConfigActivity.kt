package com.clindsay94.remex.widget

import android.appwidget.AppWidgetManager
import android.content.Intent
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.foundation.clickable
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
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateSetOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.state.updateAppWidgetState
import androidx.glance.state.PreferencesGlanceStateDefinition
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import org.json.JSONObject

data class ConfigSensor(val id: String, val name: String, val category: String)

class HardwareInfoConfigActivity : ComponentActivity() {

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

        val availableSensors = loadAvailableSensors()

        setContent {
            RemExTheme {
                HardwareInfoConfigScreen(
                    availableSensors = availableSensors,
                    onDone = { selected ->
                        lifecycleScope.launch {
                            saveAndUpdate(selected)
                        }
                    }
                )
            }
        }
    }

    private fun loadAvailableSensors(): List<ConfigSensor> {
        val json = WidgetDataCache.getTelemetryJson(this)
        val defaultSensors = listOf(
            ConfigSensor("sensor:cpu", getString(R.string.sensor_cpu_usage), "CPU"),
            ConfigSensor("sensor:gpu", getString(R.string.sensor_gpu_usage), "GPU"),
            ConfigSensor("sensor:ram", getString(R.string.sensor_ram_usage), getString(R.string.sensor_memory)),
            ConfigSensor("sensor:cpu_temp", getString(R.string.sensor_cpu_temp), "CPU"),
            ConfigSensor("sensor:gpu_temp", getString(R.string.sensor_gpu_temp), "GPU"),
        )
        if (json.isNullOrBlank()) return defaultSensors

        return try {
            val root = JSONObject(json)
            val sensors = root.optJSONArray("sensors") ?: return defaultSensors
            val parsed = mutableListOf<ConfigSensor>()
            for (i in 0 until sensors.length()) {
                val obj = sensors.getJSONObject(i)
                parsed.add(
                    ConfigSensor(
                        id = obj.optString("id", "sensor_$i"),
                        name = obj.optString("name", getString(R.string.sensor_fallback_label, i)),
                        category = obj.optString("category", "")
                    )
                )
            }
            parsed.ifEmpty { defaultSensors }
        } catch (e: Exception) {
            Log.w("HWConfigActivity", "Failed to parse cached telemetry", e)
            defaultSensors
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

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HardwareInfoConfigScreen(
    availableSensors: List<ConfigSensor>,
    onDone: (Set<String>) -> Unit
) {
    val selected = remember { mutableStateSetOf<String>() }

    Scaffold(
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.widget_config_hardware_info_title)
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

            PollIntervalSection()

            HorizontalDivider(modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))

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
private fun HardwareInfoConfigScreenPreview() {
    RemExTheme {
        HardwareInfoConfigScreen(
            availableSensors = listOf(
                ConfigSensor("sensor:cpu", "CPU Usage", "CPU"),
                ConfigSensor("sensor:gpu", "GPU Usage", "GPU"),
                ConfigSensor("sensor:ram", "RAM Usage", "Memory")
            ),
            onDone = {}
        )
    }
}

@Composable
private fun PollIntervalSection() {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    val pollSeconds by settingsManager.widgetTelemetryPollSecondsFlow
        .collectAsState(initial = SettingsManager.WIDGET_TELEMETRY_POLL_DEFAULT)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 4.dp)
    ) {
        Text(
            text = String.format(
                stringResource(R.string.widget_config_poll_interval_label),
                pollSeconds
            ),
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.SemiBold
        )
        Text(
            text = stringResource(R.string.widget_config_poll_interval_hint),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Slider(
            value = pollSeconds.toFloat(),
            onValueChange = { scope.launch { settingsManager.saveWidgetTelemetryPollSeconds(it.toInt()) } },
            valueRange = SettingsManager.WIDGET_TELEMETRY_POLL_MIN.toFloat()..
                SettingsManager.WIDGET_TELEMETRY_POLL_MAX.toFloat()
        )
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
