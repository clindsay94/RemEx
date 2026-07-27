package com.clindsay94.remex.widget

import android.appwidget.AppWidgetManager
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.runtime.mutableStateSetOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import androidx.compose.runtime.Composable
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.state.updateAppWidgetState
import androidx.glance.state.PreferencesGlanceStateDefinition
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch

class RemoteControlConfigActivity : ComponentActivity() {

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

        setContent {
            RemExTheme {
                RemoteControlConfigScreen(
                    onDone = { selected ->
                        lifecycleScope.launch {
                            saveAndUpdate(selected)
                        }
                    }
                )
            }
        }
    }

    private suspend fun saveAndUpdate(selectedIds: Set<String>) {
        val glanceId = GlanceAppWidgetManager(this).getGlanceIdBy(appWidgetId)
        updateAppWidgetState(this, PreferencesGlanceStateDefinition, glanceId) { prefs ->
            prefs.toMutablePreferences().apply {
                this[SELECTED_COMMANDS_KEY] = selectedIds.joinToString(",")
            }
        }
        RemoteControlWidget().update(this, glanceId)

        val result = Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, appWidgetId)
        setResult(RESULT_OK, result)
        finish()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteControlConfigScreen(
    onDone: (Set<String>) -> Unit
) {
    val selected = remember { mutableStateSetOf<String>() }

    Scaffold(
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.widget_config_remote_control_title)
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            Text(
                stringResource(R.string.widget_config_remote_control_hint),
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            LazyColumn(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(2.dp)
            ) {
                items(WIDGET_REMOTE_COMMANDS) { cmd ->
                    val checked = cmd.id in selected
                    CommandCheckRow(
                        title = stringResource(cmd.titleRes),
                        checked = checked,
                        onToggle = {
                            if (checked) selected.remove(cmd.id)
                            else selected.add(cmd.id)
                        }
                    )
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
private fun RemoteControlConfigScreenPreview() {
    RemExTheme {
        RemoteControlConfigScreen(onDone = {})
    }
}

@Composable
private fun CommandCheckRow(title: String, checked: Boolean, onToggle: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onToggle)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Checkbox(checked = checked, onCheckedChange = { onToggle() })
        Spacer(modifier = Modifier.height(8.dp))
        Text(
            text = title,
            style = MaterialTheme.typography.bodyLarge,
            modifier = Modifier.padding(start = 8.dp)
        )
    }
}
