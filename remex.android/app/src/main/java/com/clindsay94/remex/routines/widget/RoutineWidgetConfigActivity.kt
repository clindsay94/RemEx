package com.clindsay94.remex.routines.widget

import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.ui.Alignment
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.vectorResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineItem
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.manual.RoutineManualEntry
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.launch

/**
 * Picks the routine a "Routine" widget runs (spec A12: `manual` routines only). Exported with the
 * APPWIDGET_CONFIGURE filter, because some launchers start it directly; it can only bind a widget of
 * RemEx's own provider to a routine the person taps, and it never runs one (RoutineManifestExportTest).
 */
class RoutineWidgetConfigActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val appWidgetId = intent?.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID) ?: AppWidgetManager.INVALID_APPWIDGET_ID
        // Backing out of the launcher's configure step must cancel the placement.
        setResult(Activity.RESULT_CANCELED, Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, appWidgetId))
        // Exported (launchers start it directly), so the id must be a widget of THIS provider: another
        // app cannot use it to bind anything else. It only ever records the person's pick; it never runs a routine.
        val provider = AppWidgetManager.getInstance(this).getAppWidgetInfo(appWidgetId)?.provider
        if (appWidgetId == AppWidgetManager.INVALID_APPWIDGET_ID || provider != ComponentName(this, RoutineWidgetReceiver::class.java)) {
            finish()
            return
        }
        val repository = Routines.repository(applicationContext)
        lifecycleScope.launch { repository.load() }
        setContent {
            RemExTheme {
                val items by repository.routines.collectAsStateWithLifecycle()
                ConfigScreen(
                    routines = items.filter { RoutineManualEntry.isPinnable(it.routine) && it.verdict.isValid },
                    onPick = { routineId -> bind(appWidgetId, routineId) },
                    onClose = ::finish,
                )
            }
        }
    }

    private fun bind(appWidgetId: Int, routineId: String) {
        lifecycleScope.launch {
            Routines.secrets(applicationContext).setWidgetRoutine(appWidgetId, routineId)
            RoutineWidget.refreshAll(applicationContext)
            setResult(Activity.RESULT_OK, Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, appWidgetId))
            finish()
        }
    }

    companion object {
        const val EXTRA_FROM_WIDGET = "com.clindsay94.remex.routines.WIDGET_CONFIG_FROM_WIDGET"
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ConfigScreen(routines: List<RoutineItem>, onPick: (String) -> Unit, onClose: () -> Unit) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.widget_routine_config_title)) },
                navigationIcon = {
                    IconButton(onClick = onClose) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.button_cancel)) }
                },
            )
        },
    ) { padding ->
        if (routines.isEmpty()) {
            Column(Modifier.fillMaxSize().padding(padding).padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(stringResource(R.string.widget_routine_config_empty), style = MaterialTheme.typography.bodyLarge)
            }
            return@Scaffold
        }
        val play = ImageVector.vectorResource(R.drawable.ic_routine_play)
        LazyColumn(contentPadding = PaddingValues(top = padding.calculateTopPadding(), bottom = padding.calculateBottomPadding())) {
            items(routines, key = { it.routine.id.orEmpty() }) { item ->
                val id = item.routine.id.orEmpty()
                Row(
                    Modifier.fillMaxWidth()
                        .selectable(selected = false, role = Role.Button, onClick = { onPick(id) })
                        .heightIn(min = 56.dp)
                        .padding(horizontal = 24.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Icon(play, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                    Text(item.routine.name.orEmpty(), style = MaterialTheme.typography.bodyLarge)
                }
            }
        }
    }
}
