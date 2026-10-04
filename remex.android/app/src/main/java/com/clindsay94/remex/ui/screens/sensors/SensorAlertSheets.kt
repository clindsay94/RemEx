package com.clindsay94.remex.ui.screens.sensors

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SheetValue
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertRule
import com.clindsay94.remex.data.SensorAlertSeverity
import com.clindsay94.remex.data.SensorAlerts
import com.clindsay94.remex.data.SensorAlertsState
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.RemexHaptics
import com.clindsay94.remex.ui.components.RemexSegmentedSwitch
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes

/** The sensor an "Alert me..." sheet is about: the PC's name for it, what to call it, and its latest reading. */
data class SensorAlertTarget(val sensorName: String, val displayName: String, val unit: String?, val currentValue: Double?)

private val Directions = listOf(SensorAlertDirection.ABOVE, SensorAlertDirection.BELOW)
private val Severities = listOf(SensorAlertSeverity.WARNING, SensorAlertSeverity.CRITICAL)

private fun directionLabel(direction: SensorAlertDirection): Int =
        if (direction == SensorAlertDirection.ABOVE) R.string.sensor_alert_direction_above else R.string.sensor_alert_direction_below

private fun severityLabel(severity: SensorAlertSeverity): Int =
        if (severity == SensorAlertSeverity.CRITICAL) R.string.sensor_alert_severity_critical else R.string.sensor_alert_severity_warning

private fun blockedText(block: SensorAlertBlock): Int =
        when (block) {
                SensorAlertBlock.NOT_CONNECTED -> R.string.sensor_alert_blocked_not_connected
                SensorAlertBlock.PC_TOO_OLD -> R.string.sensor_alert_blocked_pc_too_old
                SensorAlertBlock.TOO_MANY -> R.string.sensor_alert_blocked_too_many
        }

/**
 * The "Alert me..." sheet for one sensor (RemEx-pp4cm.12): Above or Below, a threshold in the sensor's
 * unit, a severity, and Save / Remove. The PC owns the rule: Save sends it, and the PC's next list is
 * what confirms it, so the sheet closes at once and the card's bell follows.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SensorAlertEditorSheet(
        target: SensorAlertTarget,
        state: SensorAlertsState,
        onSave: (threshold: Double, direction: SensorAlertDirection, severity: SensorAlertSeverity) -> Unit,
        onRemove: () -> Unit,
        onDismiss: () -> Unit,
) {
        val haptics = rememberRemexHaptics()
        val existing = state.ruleFor(target.sensorName)
        var draft by remember(target.sensorName) { mutableStateOf(SensorAlertEditorLogic.draftFor(existing, target.currentValue)) }
        val blocked = SensorAlertEditorLogic.blockedReason(state, existing)
        val canSave = SensorAlertEditorLogic.canSave(draft, state, existing)
        val unit = target.unit?.trim().orEmpty()

        ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberBottomSheetState(SheetValue.Hidden)) {
                Column(modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                                stringResource(R.string.sensor_alert_sheet_title, target.displayName),
                                style = MaterialTheme.typography.titleMedium,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)
                        )
                        target.currentValue?.let { value ->
                                Text(
                                        stringResource(R.string.sensor_alert_now, SensorAlerts.formatValue(value, target.unit)),
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        modifier = Modifier.padding(horizontal = 16.dp)
                                )
                        }
                        RemexSegmentedSwitch(
                                options = Directions,
                                selected = draft.direction,
                                labelRes = ::directionLabel,
                                onSelect = { draft = draft.copy(direction = it) },
                        )
                        OutlinedTextField(
                                value = draft.thresholdText,
                                onValueChange = { draft = draft.copy(thresholdText = it) },
                                label = {
                                        Text(
                                                if (unit.isEmpty()) stringResource(R.string.sensor_alert_threshold_label)
                                                else stringResource(R.string.sensor_alert_threshold_label_unit, unit)
                                        )
                                },
                                isError = draft.showsError,
                                supportingText = { if (draft.showsError) Text(stringResource(R.string.sensor_alert_threshold_error)) },
                                singleLine = true,
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp)
                        )
                        RemexSegmentedSwitch(
                                options = Severities,
                                selected = draft.severity,
                                labelRes = ::severityLabel,
                                onSelect = { draft = draft.copy(severity = it) },
                        )
                        Text(
                                stringResource(blocked?.let(::blockedText) ?: R.string.sensor_alert_note),
                                style = MaterialTheme.typography.bodySmall,
                                color = if (blocked != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(horizontal = 16.dp)
                        )
                        Row(
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                                horizontalArrangement = Arrangement.End,
                                verticalAlignment = Alignment.CenterVertically
                        ) {
                                if (existing != null) {
                                        TextButton(
                                                onClick = {
                                                        haptics.perform(RemexHapticEvent.Confirm)
                                                        onRemove()
                                                },
                                                enabled = state.canEdit,
                                                shapes = rememberRemexButtonShapes(),
                                                contentPadding = ButtonDefaults.TextButtonContentPadding,
                                        ) {
                                                Text(stringResource(R.string.sensor_alert_remove), color = MaterialTheme.colorScheme.error)
                                        }
                                }
                                Button(
                                        onClick = {
                                                val threshold = draft.threshold ?: return@Button
                                                haptics.perform(RemexHapticEvent.Confirm)
                                                onSave(threshold, draft.direction, draft.severity)
                                        },
                                        enabled = canSave,
                                        shapes = rememberRemexButtonShapes(),
                                        contentPadding = ButtonDefaults.ContentPadding,
                                ) {
                                        Text(stringResource(R.string.sensor_alert_save))
                                }
                        }
                }
        }
}

/**
 * Every alert the PC holds, with a remove button on each and the "Alerts from your PC" switch above
 * them (RemEx-pp4cm.12). Tapping a row opens that sensor's editor.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SensorAlertsListSheet(
        state: SensorAlertsState,
        pcAlertsEnabled: Boolean,
        onSetPcAlertsEnabled: (Boolean) -> Unit,
        onEdit: (SensorAlertRule) -> Unit,
        onRemove: (SensorAlertRule) -> Unit,
        onDismiss: () -> Unit,
) {
        val haptics = rememberRemexHaptics()
        ModalBottomSheet(
                onDismissRequest = onDismiss,
                sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))
        ) {
                Text(
                        stringResource(R.string.sensor_alerts_title),
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)
                )
                Row(
                        modifier =
                                Modifier.fillMaxWidth()
                                        .padding(horizontal = 16.dp, vertical = 4.dp)
                                        .toggleable(
                                                value = pcAlertsEnabled,
                                                role = Role.Switch,
                                                onValueChange = { enabled ->
                                                        haptics.perform(RemexHaptics.toggle(enabled))
                                                        onSetPcAlertsEnabled(enabled)
                                                }
                                        ),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                        Column(modifier = Modifier.weight(1f)) {
                                Text(stringResource(R.string.settings_pc_alerts_title), style = MaterialTheme.typography.bodyMedium)
                                Text(
                                        stringResource(R.string.settings_pc_alerts_desc),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                        }
                        Switch(checked = pcAlertsEnabled, onCheckedChange = null)
                }
                HorizontalDivider(modifier = Modifier.padding(vertical = 8.dp))
                if (state.rules.isEmpty()) {
                        Text(
                                stringResource(R.string.sensor_alerts_empty),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 16.dp)
                        )
                } else {
                        LazyColumn(modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp)) {
                                items(state.rules, key = { it.sensorName.lowercase() }) { rule ->
                                        SensorAlertRuleRow(
                                                rule = rule,
                                                canEdit = state.canEdit,
                                                onEdit = {
                                                        haptics.perform(RemexHapticEvent.Press)
                                                        onEdit(rule)
                                                },
                                                onRemove = {
                                                        haptics.perform(RemexHapticEvent.Confirm)
                                                        onRemove(rule)
                                                }
                                        )
                                }
                        }
                }
        }
}

@Composable
private fun SensorAlertRuleRow(rule: SensorAlertRule, canEdit: Boolean, onEdit: () -> Unit, onRemove: () -> Unit) {
        val threshold = SensorAlerts.formatValue(rule.threshold, rule.unit)
        val severity = stringResource(severityLabel(rule.severity))
        Row(
                modifier = Modifier.fillMaxWidth().clickable(enabled = canEdit, onClick = onEdit).padding(horizontal = 16.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
                Column(modifier = Modifier.weight(1f)) {
                        Text(rule.displayName, style = MaterialTheme.typography.bodyLarge)
                        Text(
                                stringResource(
                                        if (rule.direction == SensorAlertDirection.ABOVE) R.string.sensor_alerts_rule_above else R.string.sensor_alerts_rule_below,
                                        threshold,
                                        severity
                                ),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                }
                FilledTonalIconButton(onClick = onRemove, enabled = canEdit, shapes = rememberRemexIconButtonShapes()) {
                        Icon(Icons.Filled.DeleteOutline, contentDescription = stringResource(R.string.sensor_alerts_remove_cd, rule.displayName))
                }
        }
}
