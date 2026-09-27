package com.clindsay94.remex.ui.routines

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Home
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.SheetValue
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.home.HomeCaptureProbe
import com.clindsay94.remex.routines.home.HomeCaptureRefusal
import com.clindsay94.remex.routines.home.HomeFacts
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.LocalReducedMotion

/**
 * "Set your home network" (routines spec 1.4, A10; R-UX-12, R-SYS-39). Capture never runs silently:
 * the sheet shows what it found (router, address range, DNS, domain), whether the PC is reachable on
 * it, and only "Use this network as home" stores it. The captured facts are shown because trust is
 * the feature: it is how a person tells a mis-capture on a hotspot from the real thing.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun HomeCaptureSheet(viewModel: RoutinesViewModel, onNavigateToConnection: () -> Unit, onDismiss: () -> Unit) {
    val home by viewModel.home.collectAsStateWithLifecycle()
    val pcs by viewModel.pcs.collectAsStateWithLifecycle()
    var attempt by remember { mutableIntStateOf(0) }
    var probe by remember { mutableStateOf<HomeCaptureProbe?>(null) }
    var confirmForget by remember { mutableStateOf(false) }
    LaunchedEffect(attempt) {
        probe = null
        probe = viewModel.probeHome()
    }
    val sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))
    val current = probe
    val pcName = (current?.pcIdentity ?: home?.capturedWithHostIdentity)?.let { id -> pcs.firstOrNull { it.identity == id }?.name }
    val pcLabel = pcName?.takeIf { it.isNotBlank() } ?: stringResource(R.string.routine_pc_fallback_name)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, sheetMaxWidth = 640.dp) {
        Column(
            Modifier.fillMaxWidth().padding(horizontal = 24.dp).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(stringResource(R.string.routines_home_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
            Text(stringResource(R.string.routines_home_explain), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                Surface(shape = CircleShape, color = MaterialTheme.colorScheme.secondaryContainer, modifier = Modifier.size(88.dp)) {
                    Box(contentAlignment = Alignment.Center) {
                        Icon(Icons.Default.Home, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer, modifier = Modifier.size(44.dp))
                    }
                }
            }

            val saved = home
            if (saved != null) {
                Text(
                    stringResource(
                        R.string.routines_home_captured,
                        RoutineTimeText.date(java.time.Instant.ofEpochMilli(saved.capturedAtUnixMs).atZone(java.time.ZoneId.systemDefault()).toLocalDate(), appLocale()),
                    ),
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.primary,
                )
                FactRows(saved.facts)
                if (!saved.facts.hasSecondary) {
                    Text(stringResource(R.string.routines_home_weak), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }

            when {
                current == null ->
                    if (!LocalReducedMotion.current) {
                        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) { RemexLoadingIndicator(modifier = Modifier.size(48.dp)) }
                    }
                current.result.ready -> {
                    val facts = current.result.facts
                    val sameAsSaved = saved != null && facts == saved.facts
                    if (saved == null && facts != null) FactRows(facts)
                    if (!sameAsSaved) {
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.semantics(mergeDescendants = true) {}) {
                            Icon(Icons.Default.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
                            Spacer(Modifier.width(8.dp))
                            Text(stringResource(R.string.routines_home_pc_reachable, pcLabel), style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End), modifier = Modifier.fillMaxWidth()) {
                        TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.routines_home_not_now)) }
                        if (!sameAsSaved && facts != null && current.pcIdentity != null) {
                            Button(
                                onClick = {
                                    viewModel.saveHome(facts, current.pcIdentity)
                                    onDismiss()
                                },
                                modifier = Modifier.heightIn(min = 48.dp),
                            ) { Text(stringResource(if (saved == null) R.string.routines_home_use else R.string.routines_home_use_instead)) }
                        }
                    }
                }
                else -> {
                    val refusal = current.result.refusal
                    Text(
                        when (refusal) {
                            HomeCaptureRefusal.VPN_ACTIVE -> stringResource(R.string.routines_home_vpn)
                            HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN -> stringResource(R.string.routines_home_pc_unreachable, pcLabel)
                            else -> stringResource(R.string.routines_home_not_on_wifi)
                        },
                        style = MaterialTheme.typography.bodyLarge,
                        modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                    )
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End), modifier = Modifier.fillMaxWidth()) {
                        if (refusal == HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN) {
                            OutlinedButton(
                                onClick = {
                                    onDismiss()
                                    onNavigateToConnection()
                                },
                                modifier = Modifier.heightIn(min = 48.dp),
                            ) { Text(stringResource(R.string.routines_home_connect)) }
                        }
                        Button(onClick = { attempt++ }, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.routines_nfc_try_again)) }
                    }
                }
            }

            if (saved != null) {
                OutlinedButton(
                    onClick = { confirmForget = true },
                    colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
                    modifier = Modifier.heightIn(min = 48.dp),
                ) { Text(stringResource(R.string.routines_home_forget)) }
            }
        }
    }

    if (confirmForget) {
        val names = viewModel.homeRoutineNames()
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text(stringResource(R.string.routines_home_forget_title)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.routines_home_forget_body))
                    if (names.isNotEmpty()) Text(stringResource(R.string.routines_home_forget_used_by, names.joinToString(", ")))
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        confirmForget = false
                        viewModel.forgetHome()
                        onDismiss()
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error, contentColor = MaterialTheme.colorScheme.onError),
                ) { Text(stringResource(R.string.routines_home_forget)) }
            },
            dismissButton = { TextButton(onClick = { confirmForget = false }) { Text(stringResource(R.string.button_cancel)) } },
        )
    }
}

/**
 * "Android may delay this routine while RemEx is asleep" (spec 1.3 step 8) with the battery exemption
 * action the tutorial already uses.
 */
@Composable
internal fun BackgroundRestrictedNotice(modifier: Modifier = Modifier) {
    val context = androidx.compose.ui.platform.LocalContext.current
    NoticeCard(
        title = null,
        body = stringResource(R.string.routines_home_background_restricted),
        actionLabel = stringResource(R.string.tutorial_battery_action),
        onAction = {
            runCatching {
                context.startActivity(
                    android.content.Intent(android.provider.Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                        .setData("package:${context.packageName}".toUri()),
                )
            }
        },
        modifier = modifier,
    )
}

/** The captured facts in plain rows; only rows that exist (spec 1.4). */
@Composable
private fun FactRows(facts: HomeFacts) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        FactRow(stringResource(R.string.routines_home_router), facts.gateways.joinToString(", "))
        FactRow(stringResource(R.string.routines_home_range), facts.prefixes.joinToString(", "))
        if (facts.dnsServers.isNotEmpty()) FactRow(stringResource(R.string.routines_home_dns), facts.dnsServers.joinToString(", "))
        facts.domain?.let { FactRow(stringResource(R.string.routines_home_domain), it) }
    }
}

@Composable
private fun FactRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().semantics(mergeDescendants = true) {}) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(0.4f))
        Text(value, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(0.6f))
    }
}
