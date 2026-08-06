package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.launch

/**
 * Lets the user read the whole diagnostics report and then send it (RemEx-0iww).
 *
 * THE PREVIEW IS THE FEATURE. What this screen shows is the finished, scrubbed string held by
 * [ShareDiagnosticsViewModel] — not a summary of it, not a re-render of it, and not a sample. The
 * share action attaches that same string. Showing anything else would mean the user consented to
 * one thing and sent another, which is the accident the whole redaction layer exists to prevent.
 *
 * The report body is deliberately NOT localized (see [com.clindsay94.remex.diagnostics
 * .DiagnosticsBundle]): it is read by whoever is debugging the fault. Everything around it —
 * headings, the note about what is included, the toggle, the button — is aimed at the sender and
 * localizes normally.
 *
 * Rendering the preview as one lazy line per row rather than a single [Text] keeps a long report
 * from stalling the first frame; the file is capped at 128KB and laying that out in one go is
 * measurable jank on the phones most likely to be reporting a fault.
 */
@Composable
fun ShareDiagnosticsScreen(viewModel: ShareDiagnosticsViewModel = viewModel()) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val failureMessage = stringResource(R.string.share_diagnostics_failed)

    ShareDiagnosticsContent(
            collecting = state.collecting,
            sharing = state.sharing,
            includeNetworkInfo = state.includeNetworkInfo,
            bundle = state.bundle,
            snackbarHostState = snackbarHostState,
            onIncludeNetworkInfoChange = viewModel::setIncludeNetworkInfo,
            onShare = {
                // Launched rather than called: staging writes the report to disk, and this runs
                // from the frame that is drawing the button the user just pressed.
                scope.launch {
                    when (val request = viewModel.requestShare(context)) {
                        is ShareDiagnosticsViewModel.ShareRequest.Ready ->
                                context.startActivity(request.chooser)
                        ShareDiagnosticsViewModel.ShareRequest.Failed ->
                                snackbarHostState.showSnackbar(failureMessage)
                        // A double tap. Silent on purpose: the first share is already on its way,
                        // and a message here would be the app complaining about its own timing.
                        ShareDiagnosticsViewModel.ShareRequest.AlreadyInFlight -> Unit
                    }
                }
            },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShareDiagnosticsContent(
        collecting: Boolean,
        sharing: Boolean,
        includeNetworkInfo: Boolean,
        bundle: String,
        snackbarHostState: SnackbarHostState,
        onIncludeNetworkInfoChange: (Boolean) -> Unit,
        onShare: () -> Unit,
) {
    val view = LocalView.current
    val topBarScrollBehavior = rememberRemexTopBarScrollBehavior()

    Scaffold(
            modifier = Modifier.nestedScroll(topBarScrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_share_diagnostics_title),
                        scrollBehavior = topBarScrollBehavior,
                )
            },
            snackbarHost = { SnackbarHost(snackbarHostState) { Snackbar(it) } },
            bottomBar = {
                // A bottom bar rather than a button below the preview: the report runs to hundreds
                // of lines and a send action at the end of it is one the user has to go looking for.
                Surface(color = MaterialTheme.colorScheme.surfaceContainer) {
                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                onShare()
                            },
                            enabled = !collecting && !sharing && bundle.isNotEmpty(),
                            modifier = Modifier.fillMaxWidth().padding(24.dp),
                    ) { Text(stringResource(R.string.share_diagnostics_send)) }
                }
            },
    ) { innerPadding ->
        Column(
                modifier = Modifier.fillMaxSize().padding(innerPadding).padding(horizontal = 24.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                    text = stringResource(R.string.share_diagnostics_explainer),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            NetworkInfoToggle(
                    includeNetworkInfo = includeNetworkInfo,
                    enabled = !collecting,
                    onChange = onIncludeNetworkInfoChange,
            )

            Text(
                    text = stringResource(R.string.share_diagnostics_preview_title),
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onSurface,
            )

            Surface(
                    modifier = Modifier.fillMaxWidth().weight(1f),
                    color = MaterialTheme.colorScheme.surfaceContainerHigh,
                    shape = MaterialTheme.shapes.medium,
            ) {
                if (collecting) {
                    Column(
                            modifier = Modifier.fillMaxSize().padding(24.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
                    ) {
                        CircularProgressIndicator()
                        Text(
                                text = stringResource(R.string.share_diagnostics_collecting),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                } else {
                    val lines = remember(bundle) { bundle.lines() }
                    LazyColumn(modifier = Modifier.fillMaxSize().padding(16.dp)) {
                        items(lines) { line ->
                            Text(
                                    text = line,
                                    style = MaterialTheme.typography.bodySmall,
                                    fontFamily = FontFamily.Monospace,
                                    color = MaterialTheme.colorScheme.onSurface,
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun NetworkInfoToggle(
        includeNetworkInfo: Boolean,
        enabled: Boolean,
        onChange: (Boolean) -> Unit,
) {
    val view = LocalView.current
    Row(
            modifier =
                    Modifier.fillMaxWidth()
                            // One tap target for the row, so the label is not a decoration beside
                            // the only thing that can be operated.
                            .toggleable(
                                    value = includeNetworkInfo,
                                    enabled = enabled,
                                    role = Role.Switch,
                                    onValueChange = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.KEYBOARD_TAP
                                        )
                                        onChange(it)
                                    },
                            ),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                    text = stringResource(R.string.share_diagnostics_network_toggle),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                    text = stringResource(R.string.share_diagnostics_network_toggle_body),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        // onCheckedChange = null: the row above owns the gesture, and a Switch with its own handler
        // would announce a second, overlapping control to a screen reader.
        Switch(checked = includeNetworkInfo, onCheckedChange = null, enabled = enabled)
    }
}

@Preview(showBackground = true)
@Composable
private fun ShareDiagnosticsCollectingPreview() {
    RemExTheme {
        ShareDiagnosticsContent(
                collecting = true,
                sharing = false,
                includeNetworkInfo = false,
                bundle = "",
                snackbarHostState = SnackbarHostState(),
                onIncludeNetworkInfoChange = {},
                onShare = {},
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun ShareDiagnosticsPreview() {
    RemExTheme {
        ShareDiagnosticsContent(
                collecting = false,
                sharing = false,
                includeNetworkInfo = false,
                bundle =
                        "=== RemEx diagnostics ===\n\n[app]\nversion: 2.4.0\n" +
                                "device: Samsung SM-S938B\nandroid API: 37\nconnection: connected\n\n" +
                                "[host]\nhost version: 2.4.0\nplatform: Windows\n\n" +
                                "[log]\nRemexManager: negotiated codec h264_nvenc",
                snackbarHostState = SnackbarHostState(),
                onIncludeNetworkInfoChange = {},
                onShare = {},
        )
    }
}
