package com.clindsay94.remex.ui.components

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.service.FileTransferModes
import com.clindsay94.remex.service.QueuedTransfer
import com.clindsay94.remex.service.TransferState
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemexLinearWavyProgress

/**
 * Persistent transfer-queue panel (plan WP7): one row per [QueuedTransfer] with live progress and
 * per-item pause / resume / cancel, plus a "Clear finished" action. Bound to [FileTransferEngine]'s
 * queue by the caller.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileManagerQueuePanel(
    transfers: List<QueuedTransfer>,
    onPause: (String) -> Unit,
    onResume: (String) -> Unit,
    onCancel: (String) -> Unit,
    onClearFinished: () -> Unit,
    modifier: Modifier = Modifier,
) {
    // Keep the last non-empty queue around so the exit animation shrinks over real rows
    // instead of recomposing to an empty "0 transfers" shell mid-departure.
    var lastNonEmpty by remember { mutableStateOf(transfers) }
    if (transfers.isNotEmpty()) lastNonEmpty = transfers
    AnimatedVisibility(
        visible = transfers.isNotEmpty(),
        enter = expandVertically(MaterialTheme.motionScheme.defaultSpatialSpec()) +
            fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
        exit = shrinkVertically(MaterialTheme.motionScheme.defaultSpatialSpec()) +
            fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
        modifier = modifier,
    ) {
        QueuePanelContent(lastNonEmpty, onPause, onResume, onCancel, onClearFinished)
    }
}

@Composable
private fun QueuePanelContent(
    transfers: List<QueuedTransfer>,
    onPause: (String) -> Unit,
    onResume: (String) -> Unit,
    onCancel: (String) -> Unit,
    onClearFinished: () -> Unit,
) {
    val hasFinished = transfers.any {
        it.state == TransferState.Done || it.state == TransferState.Cancelled || it.state == TransferState.Failed
    }
    Surface(
        color = MaterialTheme.colorScheme.surfaceContainer,
        tonalElevation = 3.dp,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = stringResource(R.string.file_manager_transfers_title, transfers.size),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f),
                )
                if (hasFinished) {
                    TextButton(onClick = onClearFinished) {
                        Text(stringResource(R.string.file_manager_clear_finished))
                    }
                }
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
            LazyColumn(modifier = Modifier.heightIn(max = 220.dp)) {
                items(transfers, key = { it.id }) { transfer ->
                    TransferRow(
                        transfer, onPause, onResume, onCancel,
                        modifier = Modifier.animateItem(
                            placementSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                        ),
                    )
                }
            }
        }
    }
}

@Composable
private fun TransferRow(
    transfer: QueuedTransfer,
    onPause: (String) -> Unit,
    onResume: (String) -> Unit,
    onCancel: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val isDownload = transfer.mode == FileTransferModes.DOWNLOAD
    val fraction =
        if (transfer.size > 0) (transfer.bytesTransferred.toFloat() / transfer.size).coerceIn(0f, 1f) else 0f
    val finished = transfer.state == TransferState.Done ||
        transfer.state == TransferState.Cancelled ||
        transfer.state == TransferState.Failed

    Row(
        modifier = modifier.fillMaxWidth().padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(
            imageVector = if (isDownload) Icons.Default.Download else Icons.Default.Upload,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(20.dp),
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = transfer.fileName,
                style = MaterialTheme.typography.bodySmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = transferStatusLabel(transfer),
                style = MaterialTheme.typography.labelSmall,
                color = when (transfer.state) {
                    TransferState.Failed -> MaterialTheme.colorScheme.error
                    TransferState.Done -> MaterialTheme.colorScheme.primary
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                },
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            // Shrink+fade instead of deleting the bar in one frame and snapping row height.
            AnimatedVisibility(
                visible = !finished,
                enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
            ) {
                RemexLinearWavyProgress(progress = fraction, modifier = Modifier.fillMaxWidth().padding(top = 2.dp))
            }
        }
        // The pause/resume/cancel cluster swaps through AnimatedContent as the state
        // machine advances; branches read the lambda's state so an exiting copy renders
        // ITS state, not live state (RemEx-c6xp).
        val actionFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
        AnimatedContent(
            targetState = transfer.state,
            transitionSpec = { fadeIn(actionFadeSpec) togetherWith fadeOut(actionFadeSpec) },
            label = "transfer_actions",
        ) { state ->
            val stateActive = state == TransferState.Active ||
                state == TransferState.Negotiating ||
                state == TransferState.Queued ||
                state == TransferState.Verifying
            val stateFinished = state == TransferState.Done ||
                state == TransferState.Cancelled ||
                state == TransferState.Failed
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (state == TransferState.Paused || state == TransferState.Failed) {
                    IconButton(onClick = { onResume(transfer.id) }) {
                        Icon(Icons.Default.PlayArrow, contentDescription = stringResource(R.string.file_manager_resume))
                    }
                } else if (stateActive) {
                    IconButton(onClick = { onPause(transfer.id) }) {
                        Icon(Icons.Default.Pause, contentDescription = stringResource(R.string.file_manager_pause))
                    }
                }
                if (!stateFinished) {
                    IconButton(onClick = { onCancel(transfer.id) }) {
                        Icon(
                            Icons.Default.Cancel,
                            contentDescription = stringResource(R.string.file_transfer_cancel),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun transferStatusLabel(transfer: QueuedTransfer): String {
    val progress =
        if (transfer.size > 0) "${(transfer.bytesTransferred * 100 / transfer.size)}%"
        else FileManagerLogic.formatBytes(transfer.bytesTransferred)
    return when (transfer.state) {
        TransferState.Queued -> stringResource(R.string.file_manager_transfer_queued)
        TransferState.Negotiating -> stringResource(R.string.file_manager_transfer_negotiating)
        TransferState.Active -> stringResource(R.string.file_manager_transfer_active, progress)
        TransferState.Paused -> stringResource(R.string.file_manager_transfer_paused, progress)
        TransferState.Verifying -> stringResource(R.string.file_manager_transfer_verifying)
        TransferState.Done -> stringResource(R.string.file_manager_transfer_done)
        TransferState.Failed -> transfer.error ?: stringResource(R.string.file_manager_transfer_failed)
        TransferState.Cancelled -> stringResource(R.string.file_manager_transfer_cancelled)
    }
}
