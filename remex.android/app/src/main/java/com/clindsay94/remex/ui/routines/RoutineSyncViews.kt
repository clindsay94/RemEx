package com.clindsay94.remex.ui.routines

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutinePcSyncView
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.RoutineSyncState
import com.clindsay94.remex.routines.RoutineSyncView
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineSyncStatuses

// The PC side of a PC-run routine as the phone shows it (routines spec 1.2, 1.7, 1.10, 2.2, §7.4.1
// step 5, §8.7; RemEx-pp0rt.12). Colour roles only: every tint here must survive monochrome and
// contrast 1.0. Icons come from the core set only (material-icons-extended is frozen, RemEx-owdk).

/** One line: what the PC made of this routine ("Waiting to sync", "On Gaming PC", "Off on ..."). */
@Composable
internal fun routineSyncText(view: RoutineSyncView, pcName: String?): String {
    val context = LocalContext.current
    val pc = pcLabel(pcName)
    // The real nickname or null: RoutineReasonText supplies the localized fallback and capitalises it
    // where it starts a sentence ("Your PC can't sleep."), which a pre-filled "your PC" defeats.
    val args =
        RoutineReasonArgs(
            pc = pcName?.takeIf { it.isNotBlank() },
            action = view.action,
            app = view.app,
            sensor = view.sensor,
        )
    return when (view.state) {
        RoutineSyncState.PENDING -> stringResource(R.string.routines_sync_pending, pc)
        RoutineSyncState.SYNCED -> stringResource(R.string.routines_sync_synced, pc)
        RoutineSyncState.OFF_ON_PC -> RoutineReasonText.message(context, RoutineReasonCodes.DISABLED_ON_PC, args)
        RoutineSyncState.REJECTED -> {
            val why = RoutineReasonText.message(context, view.reasonCode ?: RoutineReasonCodes.REJECTED_BY_PC, args)
            RoutineReasonText.message(context, RoutineReasonCodes.REJECTED_BY_PC, args.copy(detail = why))
        }
        RoutineSyncState.REFUSED -> RoutineReasonText.message(context, view.reasonCode ?: RoutineReasonCodes.BLOCKED_BY_PC, args)
    }
}

@Composable
internal fun routineSyncLook(view: RoutineSyncView): OutcomeLook {
    val scheme = MaterialTheme.colorScheme
    return when (view.state) {
        RoutineSyncState.PENDING -> OutcomeLook(Icons.Default.Refresh, scheme.onSurfaceVariant)
        RoutineSyncState.SYNCED -> OutcomeLook(Icons.Default.CheckCircle, scheme.primary)
        RoutineSyncState.OFF_ON_PC -> OutcomeLook(Icons.Default.Close, scheme.onSurfaceVariant)
        RoutineSyncState.REJECTED, RoutineSyncState.REFUSED -> OutcomeLook(Icons.Default.Warning, scheme.error)
    }
}

/** The "Runs on <PC>" chip and the routine's sync state, on list cards and in the editor. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun RoutinePcStatusRow(syncView: RoutineSyncView?, pcName: String?, modifier: Modifier = Modifier) {
    FlowRow(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        RunsOnChip(runsOnPc = true, pcName = pcName)
        if (syncView != null) {
            OutcomeLine(routineSyncLook(syncView), routineSyncText(syncView, pcName), Modifier.align(Alignment.CenterVertically))
        }
    }
}

/**
 * The set-level state of one PC (§7.4.1 step 5, §8.7): blocked, refused, owner-absent, "Paused on
 * <PC>", and how far this phone's own Pause all has reached. Emits nothing when all is well.
 */
@Composable
internal fun PcSyncBanners(pc: RoutinePcSyncView, phonePaused: Boolean, pcName: String?, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val name = pcLabel(pcName)
    val args = RoutineReasonArgs(pc = name)
    val problems = buildList {
        if (pc.blocked) add(RoutineReasonText.message(context, RoutineReasonCodes.BLOCKED_BY_PC, args))
        when (pc.status) {
            RoutineSyncStatuses.SCHEMA_TOO_NEW -> add(RoutineReasonText.message(context, RoutineReasonCodes.SCHEMA_TOO_NEW, args))
            RoutineSyncStatuses.PAYLOAD_TOO_LARGE -> add(RoutineReasonText.message(context, RoutineReasonCodes.PAYLOAD_TOO_LARGE, args))
        }
        if (pc.ownerSuspended) add(RoutineReasonText.message(context, RoutineReasonCodes.OWNER_ABSENT, args))
    }
    val notices = buildList {
        if (pc.hostPaused) add(RoutineReasonText.message(context, RoutineReasonCodes.PAUSED_ON_PC, args))
        if (phonePaused) {
            // "Paused on this phone and on <PC>" only once the PC echoed it (§8.7).
            add(
                if (pc.ownerPaused && !pc.pending) {
                    stringResource(R.string.routines_paused_scope_both, name)
                } else {
                    stringResource(R.string.routines_paused_scope_pending, name)
                },
            )
        }
    }
    if (problems.isEmpty() && notices.isEmpty()) return
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        problems.forEach { NoticeCard(title = null, body = it) }
        notices.forEach { NoticeCard(title = null, body = it, tonal = true) }
    }
}

/** The "on PC" badge on a PC run merged into the phone's history (§8.8). */
@Composable
internal fun OnPcBadge(modifier: Modifier = Modifier) {
    Surface(
        color = MaterialTheme.colorScheme.secondaryContainer,
        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
        shape = MaterialTheme.shapes.small,
        modifier = modifier,
    ) {
        Text(
            stringResource(R.string.routines_badge_on_pc),
            style = MaterialTheme.typography.labelSmall,
            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp),
        )
    }
}
