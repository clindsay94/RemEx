package com.clindsay94.remex.ui.routines

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Error
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.HourglassTop
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Monitor
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material.icons.filled.PowerOff
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.RemoveCircleOutline
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material.icons.filled.Route
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Science
import androidx.compose.material.icons.filled.Timer
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.vectorResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.model.RoutineMediaActions
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

// Shared pieces of the Routines UI (routines spec 1.1, 2.5; RemEx-pp0rt.6). Colours are roles only
// (RoutinesColorLiteralScanTest); every outcome shows an icon AND text (R-UX-50).

val RoutineIcon: ImageVector get() = Icons.Default.Route

/**
 * A trigger's glyph (spec 1.1). Composable because the NFC and leave-home glyphs are the app's own
 * vector drawables rather than new material-icons-extended usages (RemEx-owdk).
 */
@Composable
fun triggerIcon(type: String?): ImageVector =
    when (type) {
        RoutineTriggerTypes.MANUAL -> Icons.Default.PlayCircle
        RoutineTriggerTypes.PC_SENSOR -> com.clindsay94.remex.ui.components.ThermostatGlyph
        RoutineTriggerTypes.NFC_TAP -> ImageVector.vectorResource(R.drawable.ic_routine_nfc)
        RoutineTriggerTypes.HOME_ARRIVE -> Icons.Default.Home
        RoutineTriggerTypes.HOME_LEAVE -> ImageVector.vectorResource(R.drawable.ic_routine_leave)
        RoutineTriggerTypes.PC_IDLE -> Icons.Default.Schedule
        RoutineTriggerTypes.PC_SESSION -> Icons.Default.Lock
        else -> Icons.Default.Route
    }

fun stepIcon(step: RoutineStep?): ImageVector =
    when (step?.type) {
        RoutineStepTypes.WAKE -> Icons.Default.PowerSettingsNew
        RoutineStepTypes.WAIT_ONLINE -> Icons.Default.HourglassTop
        RoutineStepTypes.DELAY -> Icons.Default.Timer
        RoutineStepTypes.POWER -> powerIcon(step.verb)
        RoutineStepTypes.LAUNCH_APP -> Icons.AutoMirrored.Filled.Launch
        RoutineStepTypes.MEDIA -> Icons.Default.PlayArrow
        RoutineStepTypes.NOTIFY -> Icons.AutoMirrored.Filled.Chat
        else -> Icons.Default.Route
    }

/** The same glyphs Remote Control's power cards use. */
fun powerIcon(verb: String?): ImageVector =
    when (verb) {
        RoutinePowerVerbs.LOCK -> Icons.Default.Lock
        RoutinePowerVerbs.MONITOR_OFF -> Icons.Default.Monitor
        RoutinePowerVerbs.SLEEP, RoutinePowerVerbs.HIBERNATE -> Icons.Default.Bedtime
        RoutinePowerVerbs.SIGN_OUT -> Icons.AutoMirrored.Filled.Logout
        RoutinePowerVerbs.FORCE_SHUTDOWN -> Icons.Default.PowerOff
        RoutinePowerVerbs.RESTART -> Icons.Default.RestartAlt
        RoutinePowerVerbs.FORCE_RESTART -> Icons.Default.Warning
        RoutinePowerVerbs.RESTART_TO_UEFI -> Icons.Default.Refresh
        else -> Icons.Default.PowerSettingsNew
    }

/** "your PC" when the PC has no nickname; never an address (spec T10). */
@Composable
fun pcLabel(name: String?): String = name?.takeIf { it.isNotBlank() } ?: stringResource(R.string.routine_pc_fallback_name)

@Composable
fun triggerChipLabel(type: String?): String = stringResource(RoutineTriggerText.chip(type))

/** A step's short label, as in the chip chain and history (spec 1.1). */
@Composable
fun stepChipLabel(step: RoutineStep?): String {
    val app = step?.appLabel
    return if (step?.type == RoutineStepTypes.LAUNCH_APP && !app.isNullOrBlank()) {
        stringResource(R.string.routines_step_launch_chip_app, app)
    } else {
        stringResource(RoutineStepText.chip(step))
    }
}

/**
 * One display-only token of a chip chain (spec 2.5: `Surface` tokens, not `AssistChip`, because
 * they are not individually actionable). A step that discards work is tinted with the error role.
 */
@Composable
fun ChainToken(
    label: String,
    icon: ImageVector?,
    modifier: Modifier = Modifier,
    discardsWork: Boolean = false,
    highlighted: Boolean = false,
) {
    val scheme = MaterialTheme.colorScheme
    val (container, content) =
        when {
            highlighted -> scheme.primary to scheme.onPrimary
            discardsWork -> scheme.errorContainer to scheme.onErrorContainer
            else -> scheme.secondaryContainer to scheme.onSecondaryContainer
        }
    Surface(color = container, contentColor = content, shape = MaterialTheme.shapes.small, modifier = modifier) {
        Row(
            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            if (icon != null) Icon(icon, contentDescription = null, modifier = Modifier.size(16.dp))
            Text(label, style = MaterialTheme.typography.labelMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

/**
 * The chain "[trigger] › [step] › [step] +N" (spec A1). Wraps with [FlowRow] at large text
 * (spec 2.4); TalkBack reads one merged sentence with the step count as a plural (R-UX-45).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun RoutineChipChain(
    triggerType: String?,
    steps: List<RoutineStep?>,
    modifier: Modifier = Modifier,
    maxSteps: Int = 3,
    highlightIndex: Int? = null,
) {
    val trigger = triggerChipLabel(triggerType)
    val description = pluralStringResource(R.plurals.routines_chain_description, steps.size, trigger, steps.size)
    FlowRow(
        modifier = modifier.clearAndSetSemantics { contentDescription = description },
        horizontalArrangement = Arrangement.spacedBy(4.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
        itemVerticalAlignment = Alignment.CenterVertically,
    ) {
        ChainToken(trigger, triggerIcon(triggerType))
        steps.take(maxSteps).forEachIndexed { i, step ->
            Chevron()
            ChainToken(
                label = stepChipLabel(step),
                icon = null,
                discardsWork = RoutineStepText.discardsWork(step),
                highlighted = highlightIndex == i,
            )
        }
        val hidden = steps.size - maxSteps
        if (hidden > 0) {
            Text(
                stringResource(R.string.routines_chain_more, hidden),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun Chevron() {
    Icon(
        Icons.AutoMirrored.Filled.KeyboardArrowRight,
        contentDescription = null,
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.size(16.dp),
    )
}

/** Look of a run or step outcome. Icon + colour role + text; never colour alone (R-UX-50). */
@Immutable
data class OutcomeLook(val icon: ImageVector, val tint: Color)

@Composable
fun runOutcomeLook(outcome: String?, testRun: Boolean = false): OutcomeLook {
    val scheme = MaterialTheme.colorScheme
    return when (outcome) {
        RoutineRunOutcomes.SUCCEEDED -> OutcomeLook(if (testRun) Icons.Default.Science else Icons.Default.CheckCircle, scheme.primary)
        RoutineRunOutcomes.FAILED, RoutineRunOutcomes.INTERRUPTED -> OutcomeLook(Icons.Default.Error, scheme.error)
        RoutineRunOutcomes.CANCELLED -> OutcomeLook(Icons.Default.Cancel, scheme.onSurfaceVariant)
        RoutineRunOutcomes.SKIPPED -> OutcomeLook(Icons.Default.RemoveCircleOutline, scheme.onSurfaceVariant)
        else -> OutcomeLook(Icons.Default.Schedule, scheme.tertiary)
    }
}

@Composable
fun stepOutcomeLook(status: String?): OutcomeLook {
    val scheme = MaterialTheme.colorScheme
    return when (status) {
        RoutineStepStatuses.SUCCEEDED -> OutcomeLook(Icons.Default.CheckCircle, scheme.primary)
        RoutineStepStatuses.SIMULATED -> OutcomeLook(Icons.Default.Science, scheme.primary)
        RoutineStepStatuses.FAILED -> OutcomeLook(Icons.Default.Error, scheme.error)
        RoutineStepStatuses.CANCELLED, RoutineStepStatuses.EXPIRED -> OutcomeLook(Icons.Default.Cancel, scheme.onSurfaceVariant)
        RoutineStepStatuses.SKIPPED -> OutcomeLook(Icons.Default.RemoveCircleOutline, scheme.onSurfaceVariant)
        RoutineStepStatuses.RUNNING -> OutcomeLook(Icons.Default.Schedule, scheme.tertiary)
        else -> OutcomeLook(Icons.Default.RemoveCircleOutline, scheme.outline)
    }
}

/** The short outcome text of a run: its history label (spec 10.1), "Running" while it runs. */
@Composable
fun runOutcomeText(run: RoutineRun, pcName: String?): String {
    val context = LocalContext.current
    return when (run.outcome) {
        RoutineRunOutcomes.RUNNING -> stringResource(R.string.routines_outcome_running)
        RoutineRunOutcomes.SUCCEEDED ->
            if (run.testRun) {
                stringResource(R.string.routines_outcome_test_done)
            } else {
                stringResource(R.string.routines_outcome_steps_done, RoutineRunViews.succeededSteps(run), RoutineRunViews.totalSteps(run))
            }
        else -> RoutineReasonText.history(context, run.reasonCode, (run.reasonArgs ?: RoutineReasonArgs()).withPc(pcName))
    }
}

/** Fills `{pc}` from the phone's own nickname when the record carries none. */
fun RoutineReasonArgs.withPc(name: String?): RoutineReasonArgs = if (pc.isNullOrBlank() && name != null) copy(pc = name) else this

/** Outcome icon + text in one row, for list cards and history rows. */
@Composable
fun OutcomeLine(look: OutcomeLook, text: String, modifier: Modifier = Modifier) {
    Row(modifier = modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Icon(look.icon, contentDescription = null, tint = look.tint, modifier = Modifier.size(18.dp))
        Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Times in the app's locale: today as a time, otherwise date and time. */
object RoutineTimeText {
    fun time(millis: Long, zone: ZoneId = ZoneId.systemDefault(), locale: java.util.Locale): String {
        val at = Instant.ofEpochMilli(millis).atZone(zone)
        val today = LocalDate.now(zone)
        val formatter =
            if (at.toLocalDate() == today) {
                DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)
            } else {
                DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
            }
        return formatter.withLocale(locale).format(at)
    }

    fun clock(millis: Long, zone: ZoneId = ZoneId.systemDefault(), locale: java.util.Locale): String =
        DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT).withLocale(locale).format(Instant.ofEpochMilli(millis).atZone(zone))

    fun date(date: LocalDate, locale: java.util.Locale): String =
        DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withLocale(locale).format(date)
}

@Composable
fun appLocale(): java.util.Locale = androidx.compose.ui.platform.LocalConfiguration.current.locales[0]

/** A step's second line in the editor (spec A4). */
@Composable
fun stepSummary(step: RoutineStep, pcName: String?): String? {
    val context = LocalContext.current
    return when (step.type) {
        RoutineStepTypes.WAKE -> pcLabel(pcName)
        RoutineStepTypes.WAIT_ONLINE ->
            stringResource(
                R.string.routines_step_wait_online_summary,
                RoutineReasonText.formatDuration(context, (step.timeoutSeconds ?: 300).toString()),
            )
        RoutineStepTypes.DELAY -> RoutineReasonText.formatDuration(context, (step.seconds ?: 0).toString())
        RoutineStepTypes.POWER -> stringResource(RoutineStepText.powerVerb(step.verb))
        RoutineStepTypes.LAUNCH_APP -> step.appLabel?.takeIf { it.isNotBlank() }
        RoutineStepTypes.MEDIA -> stringResource(RoutineStepText.mediaAction(step.mediaAction ?: RoutineMediaActions.PLAY_PAUSE))
        RoutineStepTypes.NOTIFY ->
            step.title?.takeIf { it.isNotBlank() }?.let { title ->
                if (step.target == com.clindsay94.remex.routines.model.RoutineNotifyTargets.PC) {
                    stringResource(R.string.routines_step_notify_summary_pc, title)
                } else {
                    stringResource(R.string.routines_step_notify_summary_phone, title)
                }
            }
        else -> null
    }
}
