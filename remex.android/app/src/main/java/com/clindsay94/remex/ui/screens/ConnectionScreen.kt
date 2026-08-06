package com.clindsay94.remex.ui.screens

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.text.format.DateUtils
import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Help
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.core.content.ContextCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreen(
        viewModel: ConnectionViewModel = viewModel(),
        onNavigateToQrScanner: () -> Unit = {}
) {
        val connectionPrefs by viewModel.connectionPreferences.collectAsStateWithLifecycle()
        val desktopPrefs by viewModel.remoteDesktopPreferences.collectAsStateWithLifecycle()
        val isConnecting by viewModel.isConnecting.collectAsStateWithLifecycle()
        val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
        val status by viewModel.connectionStatus.collectAsStateWithLifecycle()
        val connectionError by viewModel.connectionError.collectAsStateWithLifecycle()
        val isCertMismatch by viewModel.isCertMismatch.collectAsStateWithLifecycle()
        val capabilitySummary by viewModel.capabilitySummary.collectAsStateWithLifecycle()
        val isDiscovering by viewModel.isDiscovering.collectAsStateWithLifecycle()
        val discoveredHost by viewModel.discoveredHost.collectAsStateWithLifecycle()
        val knownHosts by viewModel.knownHosts.collectAsStateWithLifecycle()

        ConnectionScreenContent(
                connectionPrefs = connectionPrefs,
                desktopPrefs = desktopPrefs,
                isConnecting = isConnecting,
                isConnected = isConnected,
                status = status,
                connectionError = connectionError,
                isCertMismatch = isCertMismatch,
                capabilitySummary = capabilitySummary,
                isDiscovering = isDiscovering,
                discoveredHost = discoveredHost,
                knownHosts = knownHosts,
                onNavigateToQrScanner = onNavigateToQrScanner,
                onConnect = { host, port, mac, broadcast, subnet, pairingPin, quality, fps, scale ->
                        viewModel.connect(
                                host,
                                port,
                                mac,
                                broadcast,
                                subnet,
                                pairingPin,
                                quality,
                                fps,
                                scale
                        )
                },
                onClearError = { viewModel.clearError() },
                onDiscoverHost = { viewModel.discoverHost() },
                onRepair = { context, host -> viewModel.clearPinForHost(context, host) },
                onConsumeDiscoveredHost = { viewModel.consumeDiscoveredHost() },
                onConnectToKnownHost = { knownHost -> viewModel.connectToKnownHost(knownHost) },
                onRenameKnownHost = { identity, nickname ->
                        viewModel.renameKnownHost(identity, nickname)
                },
                onUnpairKnownHost = { context, knownHost ->
                        viewModel.unpairKnownHost(context, knownHost)
                },
                onRefreshKnownHosts = { viewModel.refreshKnownHosts() }
        )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreenContent(
        connectionPrefs: SettingsManager.ConnectionPreferences?,
        desktopPrefs: SettingsManager.RemoteDesktopPreferences?,
        isConnecting: Boolean,
        isConnected: Boolean,
        status: String,
        connectionError: String?,
        isCertMismatch: Boolean,
        capabilitySummary: String,
        isDiscovering: Boolean,
        discoveredHost: DiscoveredHost?,
        knownHosts: List<KnownHost>,
        onNavigateToQrScanner: () -> Unit,
        onConnect: (String, Int, String, String, String, String, Int, Int, Float) -> Unit,
        onClearError: () -> Unit,
        onDiscoverHost: () -> Unit,
        onRepair: (android.content.Context, String) -> Unit,
        onConsumeDiscoveredHost: () -> Unit,
        onConnectToKnownHost: (KnownHost) -> Unit,
        onRenameKnownHost: (String, String) -> Unit,
        onUnpairKnownHost: (android.content.Context, KnownHost) -> Unit,
        onRefreshKnownHosts: () -> Unit
) {
        val view = LocalView.current
        val context = LocalContext.current
        val scope = rememberCoroutineScope()
        val motionScheme = MaterialTheme.motionScheme

        var hostInput by remember { mutableStateOf("") }
        var portInput by remember { mutableStateOf("") }
        var macInput by remember { mutableStateOf("") }
        var broadcastInput by remember { mutableStateOf("") }
        var subnetInput by remember { mutableStateOf("") }
        var pairingPinInput by remember { mutableStateOf("") }
        var qualityInput by remember { mutableFloatStateOf(50f) }
        var targetFpsInput by remember { mutableFloatStateOf(120f) }
        var scaleInput by remember { mutableFloatStateOf(1.0f) }
        var showHelpSection by remember { mutableStateOf(false) }

        // Known PCs row actions (RemEx-k62t). Held at screen level rather than inside the row so a
        // dialog is not torn down by the very action it confirms — unpairing removes the row.
        var renamingHost by remember { mutableStateOf<KnownHost?>(null) }
        var unpairingHost by remember { mutableStateOf<KnownHost?>(null) }
        var nicknameInput by remember { mutableStateOf("") }

        // Pending flags for deferred actions after permission grants
        var pendingConnect by remember { mutableStateOf(false) }
        var pendingConnectNeedsLan by remember { mutableStateOf(false) }
        // The Known PCs row whose tap is waiting on a permission grant, if it was a row rather than
        // the form. Without this the deferred path falls through to doConnect(), which carries the
        // form's pairing PIN — and a 6-digit PIN makes RemexClientManager drop the pinned hash and
        // force a re-pair, so a tap meant to RECONNECT to an already-paired PC would try to pair it
        // again with a PIN belonging to a different machine.
        var pendingKnownHost by remember { mutableStateOf<KnownHost?>(null) }
        var pendingDiscover by remember { mutableStateOf(false) }

        // Snackbar state declared early so permission launchers below can reference it.
        val snackbarHostState = remember { SnackbarHostState() }

        // Runtime permissions required to connect, scoped to the target host. A loopback or
        // VPN/Tailscale host is not on the local network, so the LAN-scoped permissions
        // (NEARBY_WIFI_DEVICES / ACCESS_LOCAL_NETWORK) are irrelevant there and must NOT be
        // requested or treated as blocking — only POST_NOTIFICATIONS (for the keepalive
        // foreground service) applies. This is what lets a Tailscale connection proceed even
        // when the user has declined local-network access.
        fun connectPermissionsFor(host: String): Array<String> {
                val needsLan =
                        com.clindsay94.remex.security.TransportTrust.requiresLocalNetworkAccess(host)
                return buildList {
                                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                                        add(Manifest.permission.POST_NOTIFICATIONS)
                                        if (needsLan) add(Manifest.permission.NEARBY_WIFI_DEVICES)
                                }
                                // SDK 37 (Android 17) requires ACCESS_LOCAL_NETWORK for LAN access.
                                if (needsLan && Build.VERSION.SDK_INT >= 36) {
                                        add("android.permission.ACCESS_LOCAL_NETWORK")
                                }
                        }
                        .toTypedArray()
        }

        fun hasNearbyWifiPermission(): Boolean {
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return true
                val hasNearby = ContextCompat.checkSelfPermission(
                        context,
                        Manifest.permission.NEARBY_WIFI_DEVICES
                ) == PackageManager.PERMISSION_GRANTED

                val hasLocalNet = if (Build.VERSION.SDK_INT >= 36) {
                        ContextCompat.checkSelfPermission(context, "android.permission.ACCESS_LOCAL_NETWORK") == PackageManager.PERMISSION_GRANTED
                } else true

                return hasNearby && hasLocalNet
        }

        fun doConnect() {
                val p = portInput.toIntOrNull() ?: 5005
                onConnect(
                        hostInput.trim(),
                        p,
                        macInput.trim(),
                        broadcastInput.trim().ifEmpty { "255.255.255.255" },
                        subnetInput.trim().ifEmpty { "255.255.255.0" },
                        pairingPinInput.trim(),
                        qualityInput.roundToInt(),
                        targetFpsInput.roundToInt(),
                        scaleInput
                )
        }

        // Permission launcher for "Save & Connect" — requests POST_NOTIFICATIONS,
        // NEARBY_WIFI_DEVICES, and ACCESS_LOCAL_NETWORK (Android 17+ / API 37+).
        // If ACCESS_LOCAL_NETWORK is denied on API 37+, we surface a clear rationale
        // via snackbar before falling through to the connect attempt (which will fail
        // at the socket layer with an equally clear error, but the rationale string
        // explains *why* before that happens).
        val connectPermissionLauncher =
                rememberLauncherForActivityResult(
                        ActivityResultContracts.RequestMultiplePermissions()
                ) { results ->
                        if (pendingConnect) {
                                pendingConnect = false
                                val knownHost = pendingKnownHost
                                pendingKnownHost = null
                                // Only treat a LAN-permission denial as fatal when this connection
                                // actually needs the local network. A Tailscale/VPN target does not,
                                // so a denial there must still fall through to doConnect().
                                val localNetworkDenied = pendingConnectNeedsLan &&
                                        Build.VERSION.SDK_INT >= 37 &&
                                        results["android.permission.ACCESS_LOCAL_NETWORK"] == false
                                if (localNetworkDenied) {
                                        scope.launch {
                                                snackbarHostState.showSnackbar(
                                                        context.getString(R.string.error_local_network_permission_denied)
                                                )
                                        }
                                        // Do not attempt connection — it will fail silently without LAN access.
                                        return@rememberLauncherForActivityResult
                                }
                                // A deferred Known PCs tap resumes as that tap, not as the form: it
                                // is already paired, and doConnect() would send the form's PIN.
                                if (knownHost != null) onConnectToKnownHost(knownHost) else doConnect()
                        }
                }

        // Separate permission launcher for "Discover" — needs NEARBY_WIFI_DEVICES and
        // ACCESS_LOCAL_NETWORK.  Same denial handling: show rationale and abort.
        val discoverPermissionLauncher =
                rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
                        results ->
                        if (pendingDiscover) {
                                pendingDiscover = false
                                // Check if ACCESS_LOCAL_NETWORK was denied on Android 17+
                                val localNetworkDenied = Build.VERSION.SDK_INT >= 37 &&
                                        results["android.permission.ACCESS_LOCAL_NETWORK"] == false
                                if (localNetworkDenied) {
                                        scope.launch {
                                                snackbarHostState.showSnackbar(
                                                        context.getString(R.string.error_local_network_permission_denied)
                                                )
                                        }
                                        return@rememberLauncherForActivityResult
                                }
                                onDiscoverHost()
                        }
                }

        // Initialize inputs from saved values only once they are loaded
        LaunchedEffect(connectionPrefs, desktopPrefs) {
                if (connectionPrefs != null && desktopPrefs != null) {
                        if (hostInput.isEmpty() && connectionPrefs.host.isNotEmpty()) hostInput =
                            connectionPrefs.host
                        if (portInput.isEmpty()) portInput = connectionPrefs.port.toString()
                        if (macInput.isEmpty()) macInput = connectionPrefs.macAddress
                        if (broadcastInput.isEmpty()) broadcastInput = connectionPrefs.broadcastIp
                        if (subnetInput.isEmpty()) subnetInput = connectionPrefs.subnetMask
                        if (qualityInput == 50f && desktopPrefs.quality != 50)
                                qualityInput = desktopPrefs.quality.toFloat()
                        if (targetFpsInput == 120f && desktopPrefs.targetFps != 120)
                                targetFpsInput = desktopPrefs.targetFps.toFloat()
                        if (scaleInput == 1.0f && desktopPrefs.scale != 1.0f) scaleInput =
                            desktopPrefs.scale
                }
        }

        // The Known PCs list is a snapshot of a separate DataStore, so refresh it on entry rather
        // than showing whatever was true when the ViewModel was created.
        LaunchedEffect(Unit) { onRefreshKnownHosts() }

        // Most recently used PC as the default connect target (RemEx-k62t). The stored host is
        // normally already that PC — tapping a row saves it — so this is the fallback for the case
        // where nothing is stored, not the mechanism. It is deliberately narrow: the stored host is
        // the last one ATTEMPTED rather than the last one that succeeded, and replacing a failed
        // address the user is still trying to reach would be the screen arguing with them. Waits
        // for prefs to load, so a slow DataStore read cannot lose to this and leave them unused.
        LaunchedEffect(knownHosts, connectionPrefs) {
                if (connectionPrefs == null) return@LaunchedEffect
                if (hostInput.isNotEmpty() || connectionPrefs.host.isNotEmpty()) return@LaunchedEffect
                KnownHosts.mostRecentlyConnected(knownHosts)?.let { mostRecent ->
                        hostInput = mostRecent.preferredAddress
                        portInput = mostRecent.port.toString()
                }
        }

        // Autofill host/port and show snackbar when a host is discovered. Consume the event
        // *before* showing the snackbar (and fire the snackbar on the screen's own scope, not this
        // effect) so a configuration change — e.g. rotation — during the snackbar's few seconds can't
        // replay the 'PC found' message: the state is already cleared. (RemEx-b0lv)
        LaunchedEffect(discoveredHost) {
                discoveredHost?.let {
                        hostInput = it.host
                        portInput = it.port.toString()
                        val discoveredHostName = it.host
                        onConsumeDiscoveredHost()
                        scope.launch {
                                snackbarHostState.showSnackbar(
                                        context.getString(
                                                R.string.host_discovered_snackbar,
                                                discoveredHostName
                                        )
                                )
                        }
                }
        }

        val scrollBehavior = rememberRemexTopBarScrollBehavior()
        Scaffold(
                modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
                topBar = {
                        RemexFlexibleTopBar(
                                title = stringResource(R.string.screen_connection_title),
                                scrollBehavior = scrollBehavior
                        )
                },
                snackbarHost = { SnackbarHost(snackbarHostState) }
        ) { padding ->
                val prefsLoaded = connectionPrefs != null && desktopPrefs != null
                AnimatedContent(
                        targetState = prefsLoaded,
                        transitionSpec = {
                                val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                                fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                        },
                        label = "connectionPrefsLoaded"
                ) { loaded ->
                if (!loaded) {
                        Box(
                                modifier = Modifier.fillMaxSize(),
                                contentAlignment = Alignment.Center
                        ) { RemexLoadingIndicator(contained = true) }
                } else {
                        Column(
                                modifier =
                                        Modifier.fillMaxSize()
                                                .padding(padding)
                                                // Before verticalScroll on purpose: this
                                                // shrinks the scroll VIEWPORT when the
                                                // keyboard opens, so the lower fields can be
                                                // scrolled above it. Applied after the scroll
                                                // it would only pad the content, leaving the
                                                // viewport itself behind the keyboard
                                                // (RemEx-a9ci).
                                                .imePadding()
                                                .verticalScroll(rememberScrollState())
                                                .padding(24.dp),
                                verticalArrangement = Arrangement.spacedBy(16.dp)
                        ) {
                                // --- Error display ---
                                AnimatedVisibility(
                                        visible = connectionError != null,
                                        enter =
                                                expandVertically(
                                                        animationSpec =
                                                                MaterialTheme.motionScheme
                                                                        .fastSpatialSpec()
                                                ) +
                                                        fadeIn(
                                                                animationSpec =
                                                                        MaterialTheme.motionScheme
                                                                                .fastEffectsSpec()
                                                        ),
                                        exit =
                                                shrinkVertically(
                                                        animationSpec =
                                                                MaterialTheme.motionScheme
                                                                        .fastSpatialSpec()
                                                ) +
                                                        fadeOut(
                                                                animationSpec =
                                                                        MaterialTheme.motionScheme
                                                                                .fastEffectsSpec()
                                                        )
                                ) {
                                        Card(
                                                colors =
                                                        CardDefaults.cardColors(
                                                                containerColor =
                                                                        MaterialTheme.colorScheme
                                                                                .errorContainer
                                                        ),
                                                modifier = Modifier.fillMaxWidth()
                                        ) {
                                                Row(
                                                        modifier = Modifier.padding(12.dp),
                                                        verticalAlignment =
                                                                Alignment.CenterVertically,
                                                        horizontalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        Icon(
                                                                Icons.Default.ErrorOutline,
                                                                contentDescription = null,
                                                                tint =
                                                                        MaterialTheme.colorScheme
                                                                                .onErrorContainer
                                                        )
                                                        Column(modifier = Modifier.weight(1f)) {
                                                                Text(
                                                                        text = if (isCertMismatch) stringResource(R.string.connection_error_cert_changed) else (connectionError ?: ""),
                                                                        style =
                                                                                MaterialTheme.typography
                                                                                        .bodyMedium,
                                                                        color =
                                                                                MaterialTheme.colorScheme
                                                                                        .onErrorContainer
                                                                )
                                                                if (isCertMismatch) {
                                                                        TextButton(
                                                                                onClick = { onRepair(context, hostInput) },
                                                                                modifier = Modifier.align(Alignment.End)
                                                                        ) {
                                                                                Text(stringResource(R.string.connection_action_repair), color = MaterialTheme.colorScheme.onErrorContainer)
                                                                        }
                                                                }
                                                        }
                                                        IconButton(
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .KEYBOARD_TAP
                                                                        )
                                                                        onClearError()
                                                                }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Close,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .button_dismiss
                                                                                ),
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onErrorContainer
                                                                )
                                                        }
                                                }
                                        }
                                }

                                // --- Auto-discovery (primary action) ---
                                Card(
                                        modifier = Modifier.fillMaxWidth(),
                                        colors =
                                                CardDefaults.cardColors(
                                                        containerColor =
                                                                MaterialTheme.colorScheme
                                                                        .primaryContainer
                                                )
                                ) {
                                        Column(
                                                modifier = Modifier.padding(16.dp),
                                                verticalArrangement = Arrangement.spacedBy(8.dp)
                                        ) {
                                                Row(
                                                        verticalAlignment =
                                                                Alignment.CenterVertically
                                                ) {
                                                        Icon(
                                                                Icons.Default.Wifi,
                                                                contentDescription = null,
                                                                tint =
                                                                        MaterialTheme.colorScheme
                                                                                .onPrimaryContainer
                                                        )
                                                        Spacer(Modifier.width(8.dp))
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_auto_discover_title
                                                                ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleSmallEmphasized,
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .onPrimaryContainer
                                                        )
                                                }
                                                Text(
                                                        stringResource(
                                                                R.string
                                                                        .connection_auto_discover_hint
                                                        ),
                                                        style = MaterialTheme.typography.bodySmall,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onPrimaryContainer
                                                )
                                                Button(
                                                        onClick = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .KEYBOARD_TAP
                                                                )
                                                                if (isDiscovering) return@Button
                                                                if (!hasNearbyWifiPermission()) {
                                                                        pendingDiscover = true
                                                                        val permsToRequest = if (Build.VERSION.SDK_INT >= 36) {
                                                                            arrayOf(Manifest.permission.NEARBY_WIFI_DEVICES, "android.permission.ACCESS_LOCAL_NETWORK")
                                                                        } else {
                                                                            arrayOf(Manifest.permission.NEARBY_WIFI_DEVICES)
                                                                        }
                                                                        discoverPermissionLauncher.launch(permsToRequest)
                                                                } else {
                                                                        onDiscoverHost()
                                                                }
                                                        },
                                                        enabled = !isDiscovering,
                                                        modifier = Modifier.fillMaxWidth()
                                                ) {
                                                        AnimatedContent(
                                                                targetState = isDiscovering,
                                                                transitionSpec = {
                                                                        val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                                                                        fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                                                                },
                                                                label = "discoverButtonContent"
                                                        ) { discovering ->
                                                                Row(verticalAlignment = Alignment.CenterVertically) {
                                                                        if (discovering) {
                                                                                RemexLoadingIndicator(
                                                                                        modifier =
                                                                                                Modifier.size(
                                                                                                        24.dp
                                                                                                ),
                                                                                        color =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .onPrimary
                                                                                )
                                                                                Spacer(
                                                                                        modifier =
                                                                                                Modifier.width(8.dp)
                                                                                )
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_searching
                                                                                        )
                                                                                )
                                                                        } else {
                                                                                Icon(
                                                                                        Icons.Default.Search,
                                                                                        contentDescription = null
                                                                                )
                                                                                Spacer(
                                                                                        modifier =
                                                                                                Modifier.width(8.dp)
                                                                                )
                                                                                Text(
                                                                                        if (discoveredHost != null
                                                                                        ) {
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_found_host,
                                                                                                        discoveredHost
                                                                                                                .host
                                                                                                )
                                                                                        } else {
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_discover_button
                                                                                                )
                                                                                        }
                                                                                )
                                                                        }
                                                                }
                                                        }
                                                }

                                                OutlinedButton(
                                                        onClick = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .CONFIRM
                                                                )
                                                                onNavigateToQrScanner()
                                                        },
                                                        modifier = Modifier.fillMaxWidth(),
                                                        colors =
                                                                ButtonDefaults.outlinedButtonColors(
                                                                        contentColor =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onPrimaryContainer
                                                                )
                                                ) {
                                                        Icon(
                                                                Icons.Default.QrCodeScanner,
                                                                contentDescription = null
                                                        )
                                                        Spacer(modifier = Modifier.width(8.dp))
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_scan_qr_code
                                                                )
                                                        )
                                                }

                                                AnimatedVisibility(
                                                        visible = discoveredHost != null,
                                                        enter =
                                                                expandVertically(
                                                                        animationSpec =
                                                                                MaterialTheme
                                                                                        .motionScheme
                                                                                        .fastSpatialSpec()
                                                                ) +
                                                                        fadeIn(
                                                                                animationSpec =
                                                                                        MaterialTheme
                                                                                                .motionScheme
                                                                                                .fastEffectsSpec()
                                                                        ),
                                                        exit =
                                                                shrinkVertically(
                                                                        animationSpec =
                                                                                MaterialTheme
                                                                                        .motionScheme
                                                                                        .fastSpatialSpec()
                                                                ) +
                                                                        fadeOut(
                                                                                animationSpec =
                                                                                        MaterialTheme
                                                                                                .motionScheme
                                                                                                .fastEffectsSpec()
                                                                        )
                                                ) {
                                                        Row(
                                                                verticalAlignment =
                                                                        Alignment.CenterVertically,
                                                                horizontalArrangement =
                                                                        Arrangement.spacedBy(6.dp)
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.CheckCircle,
                                                                        contentDescription = null,
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .primary,
                                                                        modifier =
                                                                                Modifier.size(16.dp)
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_host_found
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onPrimaryContainer
                                                                )
                                                        }
                                                }
                                        }
                                }

                                // --- Known PCs (RemEx-k62t) ---
                                // One row per physical PC, keyed by its pinned certificate rather
                                // than its address: the same machine answers at a LAN IP, a
                                // Tailscale address and a hostname, and an address-keyed list would
                                // show it three times with a third of the user's settings each.
                                AnimatedVisibility(
                                        visible = knownHosts.isNotEmpty(),
                                        enter =
                                                expandVertically(motionScheme.fastSpatialSpec()) +
                                                        fadeIn(motionScheme.fastEffectsSpec()),
                                        exit =
                                                shrinkVertically(motionScheme.fastSpatialSpec()) +
                                                        fadeOut(motionScheme.fastEffectsSpec())
                                ) {
                                        Card(modifier = Modifier.fillMaxWidth()) {
                                                Column(
                                                        modifier = Modifier.padding(16.dp),
                                                        verticalArrangement =
                                                                Arrangement.spacedBy(4.dp)
                                                ) {
                                                        Row(
                                                                verticalAlignment =
                                                                        Alignment.CenterVertically
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Computer,
                                                                        contentDescription = null,
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                                Spacer(Modifier.width(8.dp))
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_known_pcs_title
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .titleSmallEmphasized
                                                                )
                                                        }
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_known_pcs_hint
                                                                ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodySmall,
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant
                                                        )
                                                        knownHosts.forEach { knownHost ->
                                                                // Keyed by identity, not by
                                                                // position: the list re-sorts when
                                                                // a connection lands, and a row's
                                                                // remembered state (its open
                                                                // overflow menu) would otherwise
                                                                // stay with the SLOT and end up
                                                                // acting on whichever PC moved into
                                                                // it.
                                                                key(knownHost.identity) {
                                                                KnownPcRow(
                                                                        knownHost = knownHost,
                                                                        enabled = !isConnecting,
                                                                        onConnect = {
                                                                                // Fill the form as
                                                                                // well as connect:
                                                                                // it shows what was
                                                                                // tapped, and the
                                                                                // deferred
                                                                                // permission path
                                                                                // below reads it.
                                                                                hostInput =
                                                                                        knownHost
                                                                                                .preferredAddress
                                                                                portInput =
                                                                                        knownHost.port
                                                                                                .toString()
                                                                                val perms =
                                                                                        connectPermissionsFor(
                                                                                                knownHost.preferredAddress
                                                                                        )
                                                                                pendingConnectNeedsLan =
                                                                                        com.clindsay94
                                                                                                .remex
                                                                                                .security
                                                                                                .TransportTrust
                                                                                                .requiresLocalNetworkAccess(
                                                                                                        knownHost.preferredAddress
                                                                                                )
                                                                                val allGranted =
                                                                                        perms.all {
                                                                                                ContextCompat
                                                                                                        .checkSelfPermission(
                                                                                                                context,
                                                                                                                it
                                                                                                        ) ==
                                                                                                        PackageManager
                                                                                                                .PERMISSION_GRANTED
                                                                                        }
                                                                                if (perms.isNotEmpty() &&
                                                                                                !allGranted
                                                                                ) {
                                                                                        pendingKnownHost =
                                                                                                knownHost
                                                                                        pendingConnect =
                                                                                                true
                                                                                        connectPermissionLauncher
                                                                                                .launch(
                                                                                                        perms
                                                                                                )
                                                                                } else {
                                                                                        onConnectToKnownHost(
                                                                                                knownHost
                                                                                        )
                                                                                }
                                                                        },
                                                                        onRename = {
                                                                                nicknameInput =
                                                                                        knownHost.nickname
                                                                                renamingHost =
                                                                                        knownHost
                                                                        },
                                                                        onUnpair = {
                                                                                unpairingHost =
                                                                                        knownHost
                                                                        }
                                                                )
                                                                }
                                                        }
                                                }
                                        }
                                }

                                renamingHost?.let { target ->
                                        AlertDialog(
                                                onDismissRequest = { renamingHost = null },
                                                title = {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_known_pc_rename_title
                                                                )
                                                        )
                                                },
                                                text = {
                                                        OutlinedTextField(
                                                                value = nicknameInput,
                                                                onValueChange = {
                                                                        nicknameInput = it
                                                                },
                                                                singleLine = true,
                                                                label = {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_known_pc_nickname_label
                                                                                )
                                                                        )
                                                                },
                                                                keyboardOptions =
                                                                        androidx.compose.foundation
                                                                                .text
                                                                                .KeyboardOptions(
                                                                                        capitalization =
                                                                                                KeyboardCapitalization
                                                                                                        .Words,
                                                                                        imeAction =
                                                                                                ImeAction.Done
                                                                                )
                                                        )
                                                },
                                                confirmButton = {
                                                        TextButton(
                                                                onClick = {
                                                                        onRenameKnownHost(
                                                                                target.identity,
                                                                                nicknameInput
                                                                        )
                                                                        renamingHost = null
                                                                }
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .button_confirm
                                                                        )
                                                                )
                                                        }
                                                },
                                                dismissButton = {
                                                        TextButton(
                                                                onClick = { renamingHost = null }
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string.button_cancel
                                                                        )
                                                                )
                                                        }
                                                }
                                        )
                                }

                                // Confirm first: unpairing is not undoable from the phone. Pairing
                                // again needs the PIN showing on the PC, which the user may not be
                                // standing in front of.
                                unpairingHost?.let { target ->
                                        AlertDialog(
                                                onDismissRequest = { unpairingHost = null },
                                                title = {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_known_pc_unpair_title,
                                                                        target.nickname.ifBlank {
                                                                                target.preferredAddress
                                                                        }
                                                                )
                                                        )
                                                },
                                                text = {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_known_pc_unpair_message
                                                                )
                                                        )
                                                },
                                                confirmButton = {
                                                        TextButton(
                                                                onClick = {
                                                                        onUnpairKnownHost(
                                                                                context,
                                                                                target
                                                                        )
                                                                        unpairingHost = null
                                                                }
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_unpair
                                                                        ),
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .error
                                                                )
                                                        }
                                                },
                                                dismissButton = {
                                                        TextButton(
                                                                onClick = { unpairingHost = null }
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string.button_cancel
                                                                        )
                                                                )
                                                        }
                                                }
                                        )
                                }

                                // --- How to connect help section ---
                                Card(
                                        modifier =
                                                Modifier.fillMaxWidth()
                                                        .animateContentSize(
                                                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                                                        )
                                                        .clickable {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .KEYBOARD_TAP
                                                                )
                                                                showHelpSection = !showHelpSection
                                                        },
                                        colors =
                                                CardDefaults.cardColors(
                                                        containerColor =
                                                                MaterialTheme.colorScheme
                                                                        .surfaceVariant.copy(
                                                                        alpha = 0.6f
                                                                )
                                                )
                                ) {
                                        Column(modifier = Modifier.padding(16.dp)) {
                                                Row(
                                                        verticalAlignment =
                                                                Alignment.CenterVertically,
                                                        horizontalArrangement =
                                                                Arrangement.SpaceBetween,
                                                        modifier = Modifier.fillMaxWidth()
                                                ) {
                                                        Row(
                                                                verticalAlignment =
                                                                        Alignment.CenterVertically,
                                                                horizontalArrangement =
                                                                        Arrangement.spacedBy(8.dp)
                                                        ) {
                                                                Icon(
                                                                        Icons.AutoMirrored.Filled
                                                                                .Help,
                                                                        contentDescription = null,
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_help_title
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .titleSmallEmphasized,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        }
                                                        val helpChevronRotation by animateFloatAsState(
                                                                targetValue = if (showHelpSection) 180f else 0f,
                                                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                                                                label = "helpChevronRotation"
                                                        )
                                                        Icon(
                                                                Icons.Default.ExpandMore,
                                                                contentDescription = null,
                                                                tint =
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant,
                                                                modifier = Modifier.rotate(helpChevronRotation)
                                                        )
                                                }

                                                AnimatedVisibility(
                                                        visible = showHelpSection,
                                                        enter =
                                                                expandVertically(
                                                                        animationSpec =
                                                                                MaterialTheme
                                                                                        .motionScheme
                                                                                        .fastSpatialSpec()
                                                                ) +
                                                                        fadeIn(
                                                                                animationSpec =
                                                                                        MaterialTheme
                                                                                                .motionScheme
                                                                                                .fastEffectsSpec()
                                                                        ),
                                                        exit =
                                                                shrinkVertically(
                                                                        animationSpec =
                                                                                MaterialTheme
                                                                                        .motionScheme
                                                                                        .fastSpatialSpec()
                                                                ) +
                                                                        fadeOut(
                                                                                animationSpec =
                                                                                        MaterialTheme
                                                                                                .motionScheme
                                                                                                .fastEffectsSpec()
                                                                        )
                                                ) {
                                                        Column(
                                                                modifier =
                                                                        Modifier.padding(
                                                                                top = 12.dp
                                                                        ),
                                                                verticalArrangement =
                                                                        Arrangement.spacedBy(12.dp)
                                                        ) {
                                                                HelpStep(
                                                                        number = "1",
                                                                        title =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step1_title
                                                                                ),
                                                                        body =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step1_body
                                                                                )
                                                                )
                                                                HelpStep(
                                                                        number = "2",
                                                                        title =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step2_title
                                                                                ),
                                                                        body =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step2_body
                                                                                )
                                                                )
                                                                HelpStep(
                                                                        number = "3",
                                                                        title =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step3_title
                                                                                ),
                                                                        body = null
                                                                )
                                                                // Platform-specific IP instructions
                                                                Surface(
                                                                        shape =
                                                                                MaterialTheme.shapes
                                                                                        .small,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .surface,
                                                                        modifier =
                                                                                Modifier.fillMaxWidth()
                                                                ) {
                                                                        Column(
                                                                                modifier =
                                                                                        Modifier.padding(
                                                                                                12.dp
                                                                                        ),
                                                                                verticalArrangement =
                                                                                        Arrangement
                                                                                                .spacedBy(
                                                                                                        8.dp
                                                                                                )
                                                                        ) {
                                                                                IpInstructionRow(
                                                                                        platform =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_platform_windows
                                                                                                ),
                                                                                        icon =
                                                                                                Icons.Default
                                                                                                        .Computer,
                                                                                        instruction =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_ip_windows
                                                                                                )
                                                                                )
                                                                                HorizontalDivider()
                                                                                IpInstructionRow(
                                                                                        platform =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_platform_linux
                                                                                                ),
                                                                                        icon =
                                                                                                Icons.Default
                                                                                                        .Terminal,
                                                                                        instruction =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_ip_linux
                                                                                                )
                                                                                )
                                                                        }
                                                                }
                                                                HelpStep(
                                                                        number = "4",
                                                                        title =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step4_title
                                                                                ),
                                                                        body =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_help_step4_body
                                                                                )
                                                                )
                                                        }
                                                }
                                        }
                                }

                                // --- Manual host fields ---
                                Text(
                                        text =
                                                stringResource(
                                                        R.string.connection_or_enter_manually
                                                ),
                                        style = MaterialTheme.typography.labelMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        modifier = Modifier.align(Alignment.Start)
                                )

                                OutlinedTextField(
                                        value = hostInput,
                                        onValueChange = { hostInput = it },
                                        label = {
                                                Text(stringResource(R.string.connection_label_host))
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        singleLine = true,
                                        leadingIcon = {
                                                Icon(Icons.Default.Dns, contentDescription = null)
                                        },
                                        keyboardOptions =
                                                androidx.compose.foundation.text.KeyboardOptions(
                                                        keyboardType = KeyboardType.Decimal,
                                                        imeAction = ImeAction.Next
                                                ),
                                        supportingText = {
                                                Text(stringResource(R.string.connection_hint_host))
                                        }
                                )

                                OutlinedTextField(
                                        value = portInput,
                                        onValueChange = {
                                                portInput = it.filter { c -> c.isDigit() }
                                        },
                                        label = {
                                                Text(stringResource(R.string.connection_label_port))
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        singleLine = true,
                                        leadingIcon = {
                                                Icon(
                                                        Icons.Default.Numbers,
                                                        contentDescription = null
                                                )
                                        },
                                        keyboardOptions =
                                                androidx.compose.foundation.text.KeyboardOptions(
                                                        keyboardType = KeyboardType.Number,
                                                        imeAction = ImeAction.Next
                                                ),
                                        supportingText = {
                                                Text(stringResource(R.string.connection_hint_port))
                                        }
                                )

                                OutlinedTextField(
                                        value = macInput,
                                        onValueChange = { macInput = it.uppercase() },
                                        label = {
                                                Text(stringResource(R.string.connection_label_mac))
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        singleLine = true,
                                        leadingIcon = {
                                                Icon(
                                                        Icons.Default.Memory,
                                                        contentDescription = null
                                                )
                                        },
                                        keyboardOptions =
                                                androidx.compose.foundation.text.KeyboardOptions(
                                                        capitalization =
                                                                KeyboardCapitalization.Characters,
                                                        imeAction = ImeAction.Next
                                                ),
                                        placeholder = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_placeholder_mac
                                                        ),
                                                        style = MaterialTheme.typography.bodySmall
                                                )
                                        },
                                        supportingText = {
                                                Text(stringResource(R.string.connection_hint_mac))
                                        }
                                )

                                OutlinedTextField(
                                        value = broadcastInput,
                                        onValueChange = { broadcastInput = it },
                                        label = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_label_broadcast
                                                        )
                                                )
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        singleLine = true,
                                        leadingIcon = {
                                                Icon(
                                                        Icons.Default.Router,
                                                        contentDescription = null
                                                )
                                        },
                                        keyboardOptions =
                                                androidx.compose.foundation.text.KeyboardOptions(
                                                        keyboardType = KeyboardType.Decimal,
                                                        imeAction = ImeAction.Next
                                                ),
                                        supportingText = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_hint_broadcast
                                                        )
                                                )
                                        }
                                )

                                OutlinedTextField(
                                        value = subnetInput,
                                        onValueChange = { subnetInput = it },
                                        label = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_label_subnet
                                                        )
                                                )
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        singleLine = true,
                                        leadingIcon = {
                                                Icon(Icons.Default.Lan, contentDescription = null)
                                        },
                                        keyboardOptions =
                                                androidx.compose.foundation.text.KeyboardOptions(
                                                        keyboardType = KeyboardType.Decimal,
                                                        imeAction = ImeAction.Next
                                                ),
                                        supportingText = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_hint_subnet
                                                        )
                                                )
                                        }
                                )

                                OutlinedTextField(
                                        value = pairingPinInput,
                                        onValueChange = { if (it.length <= 6) pairingPinInput = it },
                                        label = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_label_pairing_pin
                                                        )
                                                )
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        singleLine = true,
                                        leadingIcon = {
                                                Icon(
                                                        Icons.Default.VpnKey,
                                                        contentDescription = null
                                                )
                                        },
                                        keyboardOptions =
                                                androidx.compose.foundation.text.KeyboardOptions(
                                                        keyboardType = KeyboardType.NumberPassword,
                                                        imeAction = ImeAction.Done
                                                ),
                                        supportingText = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_hint_pairing_pin
                                                        )
                                                )
                                        }
                                )

                                Card(modifier = Modifier.fillMaxWidth()) {
                                        Column(
                                                modifier = Modifier.padding(16.dp),
                                                verticalArrangement = Arrangement.spacedBy(8.dp)
                                        ) {
                                                Text(
                                                        text =
                                                                stringResource(
                                                                        R.string
                                                                                .connection_desktop_defaults_title
                                                                ),
                                                        style =
                                                                MaterialTheme.typography
                                                                        .titleMediumEmphasized
                                                )

                                                Spacer(modifier = Modifier.height(8.dp))

                                                Text(
                                                        stringResource(
                                                                R.string.connection_quality_label,
                                                                qualityInput.toInt()
                                                        )
                                                )
                                                Slider(
                                                        value = qualityInput,
                                                        onValueChange = { qualityInput = it },
                                                        onValueChangeFinished = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .CLOCK_TICK
                                                                )
                                                        },
                                                        valueRange = 1f..100f,
                                                        modifier =
                                                                Modifier.minimumInteractiveComponentSize()
                                                )

                                                Spacer(modifier = Modifier.height(16.dp))

                                                Text(
                                                        if (targetFpsInput.toInt() >
                                                                        DESKTOP_FPS_PACED_MAX
                                                        )
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_fps_unlimited_label
                                                                )
                                                        else
                                                                stringResource(
                                                                        R.string.connection_fps_label,
                                                                        targetFpsInput.toInt()
                                                                )
                                                )
                                                Slider(
                                                        value = targetFpsInput,
                                                        onValueChange = { targetFpsInput = it },
                                                        onValueChangeFinished = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .CLOCK_TICK
                                                                )
                                                        },
                                                        valueRange = 1f..360f,
                                                        modifier =
                                                                Modifier.minimumInteractiveComponentSize()
                                                )

                                                Spacer(modifier = Modifier.height(16.dp))

                                                Text(
                                                        stringResource(
                                                                R.string.connection_scale_label,
                                                                "%.2f".format(scaleInput)
                                                        )
                                                )
                                                Slider(
                                                        value = scaleInput,
                                                        onValueChange = { scaleInput = it },
                                                        onValueChangeFinished = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .CLOCK_TICK
                                                                )
                                                        },
                                                        valueRange = 0.25f..1.0f,
                                                        modifier =
                                                                Modifier.minimumInteractiveComponentSize()
                                                )
                                        }
                                }

                                Spacer(modifier = Modifier.height(8.dp))

                                Button(
                                        onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                )
                                                // Scope the required permissions to the target host: a
                                                // loopback or Tailscale/VPN target needs no LAN permission,
                                                // so a denied/undecided local-network grant must NOT block it
                                                // (the root cause of "won't even try to connect" over Tailscale).
                                                val targetHost = hostInput.trim()
                                                val perms = connectPermissionsFor(targetHost)
                                                pendingConnectNeedsLan =
                                                        com.clindsay94.remex.security.TransportTrust
                                                                .requiresLocalNetworkAccess(targetHost)
                                                val allGranted =
                                                        perms.all {
                                                                ContextCompat.checkSelfPermission(
                                                                        context,
                                                                        it
                                                                ) == PackageManager.PERMISSION_GRANTED
                                                        }
                                                if (perms.isNotEmpty() && !allGranted) {
                                                        pendingConnect = true
                                                        connectPermissionLauncher.launch(perms)
                                                } else {
                                                        doConnect()
                                                }
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        enabled = !isConnecting && hostInput.isNotEmpty()
                                ) {
                                        AnimatedContent(
                                                targetState = isConnecting,
                                                transitionSpec = {
                                                        val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                                                        fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                                                },
                                                label = "connectButtonContent"
                                        ) { connecting ->
                                                if (connecting) {
                                                        RemexLoadingIndicator(
                                                                modifier = Modifier.size(24.dp),
                                                                color = MaterialTheme.colorScheme.onPrimary
                                                        )
                                                } else {
                                                        Text(stringResource(R.string.button_save_connect))
                                                }
                                        }
                                }

                                Row(
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                                        modifier = Modifier.fillMaxWidth()
                                ) {
                                        Column(modifier = Modifier.weight(1f)) {
                                                val statusColor by animateColorAsState(
                                                        targetValue =
                                                                if (isConnected)
                                                                        MaterialTheme.colorScheme.primary
                                                                else
                                                                        MaterialTheme.colorScheme.onSurfaceVariant,
                                                        animationSpec = motionScheme.defaultEffectsSpec(),
                                                        label = "connectionStatusColor"
                                                )
                                                Text(
                                                        text =
                                                                stringResource(
                                                                        R.string
                                                                                .connection_status_label,
                                                                        status
                                                                ),
                                                        style = MaterialTheme.typography.bodyMedium,
                                                        color = statusColor
                                                )
                                                Text(
                                                        text = capabilitySummary,
                                                        style = MaterialTheme.typography.bodySmall,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant
                                                )
                                        }

                                        var isPaired by
                                                remember(hostInput) { mutableStateOf(false) }
                                        LaunchedEffect(hostInput) {
                                                isPaired =
                                                        PinnedHostStore.getPin(
                                                                context,
                                                                hostInput
                                                        ) != null
                                        }

                                        AnimatedVisibility(
                                                visible = isPaired,
                                                enter = expandVertically(motionScheme.fastSpatialSpec()) + fadeIn(motionScheme.fastEffectsSpec()),
                                                exit = shrinkVertically(motionScheme.fastSpatialSpec()) + fadeOut(motionScheme.fastEffectsSpec())
                                        ) {
                                                Row(
                                                        verticalAlignment =
                                                                Alignment.CenterVertically,
                                                        horizontalArrangement =
                                                                Arrangement.spacedBy(4.dp)
                                                ) {
                                                        Icon(
                                                                Icons.Default.CheckCircle,
                                                                contentDescription = null,
                                                                tint = com.clindsay94.remex.ui.theme.LocalCustomColors.current.success,
                                                                modifier = Modifier.size(16.dp)
                                                        )
                                                        Text(
                                                                text =
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_paired
                                                                        ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodySmall,
                                                                color = com.clindsay94.remex.ui.theme.LocalCustomColors.current.success
                                                        )
                                                        TextButton(
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .KEYBOARD_TAP
                                                                        )
                                                                        scope.launch {
                                                                                PinnedHostStore
                                                                                        .forgetHost(
                                                                                                context,
                                                                                                hostInput
                                                                                        )
                                                                                // That address may
                                                                                // be one of a Known
                                                                                // PCs row's — the
                                                                                // list is a
                                                                                // snapshot.
                                                                                onRefreshKnownHosts()
                                                                        }
                                                                        isPaired = false
                                                                },
                                                                contentPadding =
                                                                        PaddingValues(
                                                                                horizontal = 8.dp,
                                                                                vertical = 0.dp
                                                                        ),
                                                                modifier = Modifier.height(32.dp)
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_unpair
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .labelSmall
                                                                )
                                                        }
                                                }
                                        }
                                }
                        }
                }
                }
        }
}

@Preview(showBackground = true)
@Composable
private fun ConnectionScreenPreview() {
    RemExTheme {
        ConnectionScreenContent(
            connectionPrefs = SettingsManager.ConnectionPreferences(
                host = "192.168.1.10",
                port = 5005,
                macAddress = "AA:BB:CC:DD:EE:FF",
                broadcastIp = "192.168.1.255",
                subnetMask = "255.255.255.0"
            ),
            desktopPrefs = SettingsManager.RemoteDesktopPreferences(
                quality = 70,
                targetFps = 60,
                scale = 0.75f
            ),
            isConnecting = false,
            isConnected = true,
            status = "Connected",
            connectionError = null,
            capabilitySummary = "Desktop, Shell, TaskManager",
            isDiscovering = false,
            discoveredHost = null,
            isCertMismatch = false,
            knownHosts = listOf(
                KnownHost(
                    identity = "0123456789abcdef",
                    nickname = "Studio PC",
                    addresses = listOf("192.168.1.10", "100.72.10.4"),
                    port = 5005,
                    lastConnectedAtMillis = 1_700_000_000_000L
                ),
                KnownHost(
                    identity = "fedcba9876543210",
                    nickname = "",
                    addresses = listOf("192.168.1.11"),
                    port = 5005,
                    lastConnectedAtMillis = 0L
                )
            ),
            onNavigateToQrScanner = {},
            onConnect = { _, _, _, _, _, _, _, _, _ -> },
            onClearError = {},
            onDiscoverHost = {},
            onRepair = { _, _ -> },
            onConsumeDiscoveredHost = {},
            onConnectToKnownHost = {},
            onRenameKnownHost = { _, _ -> },
            onUnpairKnownHost = { _, _ -> },
            onRefreshKnownHosts = {}
        )
    }
}

/**
 * One PC in the Known PCs list: tap to connect, overflow to rename or unpair (RemEx-k62t).
 *
 * The whole row is the connect target rather than a trailing "Connect" button — reconnecting to a
 * PC the phone is already paired with is the common action on this screen, and it should not need
 * aim. The destructive action is behind the overflow for the same reason: an unpair that is one
 * mis-tap away from a connect is one mis-tap away from needing the PIN off the PC to undo.
 */
@Composable
private fun KnownPcRow(
        knownHost: KnownHost,
        enabled: Boolean,
        onConnect: () -> Unit,
        onRename: () -> Unit,
        onUnpair: () -> Unit
) {
        val view = LocalView.current
        var menuExpanded by remember { mutableStateOf(false) }
        val displayName = knownHost.nickname.ifBlank { knownHost.preferredAddress }
        val lastConnected =
                if (knownHost.hasEverConnected) {
                        stringResource(
                                R.string.connection_known_pc_last_connected,
                                // The system's own relative-time wording, so it is localized and
                                // formatted the way the rest of the phone does it rather than by a
                                // string this app would have to translate nine times.
                                DateUtils.getRelativeTimeSpanString(
                                                knownHost.lastConnectedAtMillis,
                                                System.currentTimeMillis(),
                                                DateUtils.MINUTE_IN_MILLIS
                                        )
                                        .toString()
                        )
                } else {
                        stringResource(R.string.connection_known_pc_never_connected)
                }

        ListItem(
                modifier =
                        Modifier.clickable(enabled = enabled) {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                onConnect()
                        },
                leadingContent = {
                        Icon(
                                Icons.Default.Computer,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary
                        )
                },
                supportingContent = {
                        Column {
                                // Only when the name is a nickname, otherwise the headline already
                                // IS the address and this would print it twice.
                                if (knownHost.nickname.isNotBlank()) {
                                        Text(
                                                knownHost.preferredAddress,
                                                style = MaterialTheme.typography.bodySmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                }
                                Text(
                                        lastConnected,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                        }
                },
                trailingContent = {
                        Box {
                                IconButton(onClick = { menuExpanded = true }) {
                                        Icon(
                                                Icons.Default.MoreVert,
                                                contentDescription =
                                                        stringResource(
                                                                R.string
                                                                        .connection_known_pc_actions,
                                                                displayName
                                                        )
                                        )
                                }
                                DropdownMenu(
                                        expanded = menuExpanded,
                                        onDismissRequest = { menuExpanded = false }
                                ) {
                                        DropdownMenuItem(
                                                text = {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_known_pc_rename
                                                                )
                                                        )
                                                },
                                                onClick = {
                                                        menuExpanded = false
                                                        onRename()
                                                }
                                        )
                                        DropdownMenuItem(
                                                text = {
                                                        Text(
                                                                stringResource(
                                                                        R.string.connection_unpair
                                                                ),
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .error
                                                        )
                                                },
                                                onClick = {
                                                        menuExpanded = false
                                                        onUnpair()
                                                }
                                        )
                                }
                        }
                },
                colors =
                        ListItemDefaults.colors(
                                containerColor = androidx.compose.ui.graphics.Color.Transparent
                        )
        ) {
                Text(displayName, style = MaterialTheme.typography.bodyLargeEmphasized)
        }
}

@Composable
private fun HelpStep(number: String, title: String, body: String?) {
        ListItem(
                supportingContent =
                        body?.let {
                                {
                                        Text(
                                                it,
                                                style = MaterialTheme.typography.bodySmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                }
                        },
                leadingContent = {
                        Surface(
                                shape = MaterialTheme.shapes.small,
                                color = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(24.dp)
                        ) {
                                Box(contentAlignment = Alignment.Center) {
                                        Text(
                                                number,
                                                style = MaterialTheme.typography.labelSmallEmphasized,
                                                color = MaterialTheme.colorScheme.onPrimary
                                        )
                                }
                        }
                },
                colors =
                        ListItemDefaults.colors(
                                containerColor = androidx.compose.ui.graphics.Color.Transparent
                        )
        ) {
                Text(
                        title,
                        style = MaterialTheme.typography.bodyMediumEmphasized
                )
        }
}

@Composable
private fun IpInstructionRow(
        platform: String,
        icon: androidx.compose.ui.graphics.vector.ImageVector,
        instruction: String
) {
        ListItem(
                supportingContent = {
                        Text(
                                instruction,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                },
                leadingContent = {
                        Icon(
                                icon,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                },
                colors =
                        ListItemDefaults.colors(
                                containerColor = androidx.compose.ui.graphics.Color.Transparent
                        )
        ) {
                Text(
                        platform,
                        style = MaterialTheme.typography.labelMediumEmphasized
                )
        }
}
