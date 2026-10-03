package com.clindsay94.remex.ui.routines

import android.content.Context
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.vectorResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.manual.RoutineManualEntry
import com.clindsay94.remex.routines.manual.RoutineManualSurfaces
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes

/** A `manual` routine shown on Remote Control (spec A14, R-UX-06). */
@Immutable
data class RemoteRoutine(val id: String, val name: String, val destructive: Boolean)

/**
 * The connected PC's `manual` routines, in the user's order, for Remote Control's "Your routines".
 * Empty when nothing is connected or none target this PC, which hides the section.
 */
@Composable
fun rememberRemoteControlRoutines(): List<RemoteRoutine> {
    val context = LocalContext.current
    val repository = remember(context) { Routines.repository(context) }
    LaunchedEffect(repository) { repository.load() }
    val items by repository.routines.collectAsStateWithLifecycle()
    val connection by RemexClientManager.authenticatedConnection.collectAsStateWithLifecycle()
    val identity by produceState<String?>(null, connection?.host) {
        val host = connection?.host?.takeIf { it.isNotBlank() }
        value = host?.let { h -> withContext(Dispatchers.IO) { runCatching { HostIdentity.keyFor(PinnedHostStore.getPin(context, h)) }.getOrNull() } }
    }
    val pc = identity ?: return emptyList()
    return items
        .filter { it.verdict.isValid && it.routine.hostIdentity == pc && RoutineManualEntry.isPinnable(it.routine) }
        .map { item ->
            RemoteRoutine(
                id = item.routine.id.orEmpty(),
                name = item.routine.name.orEmpty(),
                destructive = item.routine.steps.orEmpty().any { it?.isDestructive == true },
            )
        }
}

/**
 * Runs a routine from its Remote Control card: an in-app Run (`manual.app`). A destructive routine
 * has already passed the card's confirm face, which is the phone-side confirmation; the PC still
 * counts down (D1).
 */
suspend fun runRoutineFromRemoteControl(context: Context, routineId: String) {
    RoutineManualSurfaces.start(context, routineId, RoutineRunSources.MANUAL_APP, confirmed = true)
}

/**
 * One routine as a Remote Control command card (spec A14): same look as the POWER cards, and the
 * same confirm face for a routine with a destructive step.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemoteRoutineCard(
    routine: RemoteRoutine,
    awaitingConfirmation: Boolean,
    shape: Shape,
    onPrimaryClick: () -> Unit,
    onConfirm: () -> Unit,
    onCancel: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val motion = MaterialTheme.motionScheme
    Card(
        modifier = modifier.fillMaxWidth().animateContentSize(animationSpec = motion.fastSpatialSpec()),
        shape = shape,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(ImageVector.vectorResource(R.drawable.ic_routine_play), contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            AnimatedContent(
                targetState = awaitingConfirmation,
                transitionSpec = { fadeIn(motion.defaultEffectsSpec()) togetherWith fadeOut(motion.defaultEffectsSpec()) },
                label = "routineCardConfirm",
            ) { confirming ->
                Column(verticalArrangement = Arrangement.spacedBy(10.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        if (confirming) stringResource(R.string.remote_control_confirm_choice) else routine.name,
                        style = MaterialTheme.typography.titleSmallEmphasized,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        textAlign = TextAlign.Center,
                    )
                    if (confirming) {
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Button(
                                onClick = onConfirm,
                                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error, contentColor = MaterialTheme.colorScheme.onError),
                                modifier = Modifier.weight(1f),
                                shapes = rememberRemexButtonShapes(),
                                contentPadding = ButtonDefaults.ContentPadding,
                            ) { Text(stringResource(R.string.button_confirm)) }
                            TextButton(onClick = onCancel, modifier = Modifier.weight(1f), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.TextButtonContentPadding) { Text(stringResource(R.string.button_cancel)) }
                        }
                    } else {
                        FilledTonalButton(onClick = onPrimaryClick, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                            Text(stringResource(if (routine.destructive) R.string.button_select else R.string.routines_run))
                        }
                    }
                }
            }
        }
    }
}
