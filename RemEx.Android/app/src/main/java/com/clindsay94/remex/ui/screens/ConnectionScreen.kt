package com.clindsay94.remex.ui.screens

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Help
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
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
        val connectionPrefs by viewModel.connectionPreferences.collectAsState()
        val desktopPrefs by viewModel.remoteDesktopPreferences.collectAsState()
        val isConnecting by viewModel.isConnecting.collectAsState()
        val isConnected by RemexClientManager.isConnected.collectAsState()
        val status by viewModel.connectionStatus.collectAsState()
        val connectionError by viewModel.connectionError.collectAsState()
        val capabilitySummary by viewModel.capabilitySummary.collectAsState()
        val isDiscovering by viewModel.isDiscovering.collectAsState()
        val discoveredHost by viewModel.discoveredHost.collectAsState()

        ConnectionScreenContent(
                connectionPrefs = connectionPrefs,
                desktopPrefs = desktopPrefs,
                isConnecting = isConnecting,
                isConnected = isConnected,
                status = status,
                connectionError = connectionError,
                capabilitySummary = capabilitySummary,
                isDiscovering = isDiscovering,
                discoveredHost = discoveredHost,
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
                onDiscoverHost = { viewModel.discoverHost() }
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
        capabilitySummary: String,
        isDiscovering: Boolean,
        discoveredHost: DiscoveredHost?,
        onNavigateToQrScanner: () -> Unit,
        onConnect: (String, Int, String, String, String, String, Int, Int, Float) -> Unit,
        onClearError: () -> Unit,
        onDiscoverHost: () -> Unit
) {
        val view = LocalView.current
        val context = LocalContext.current
        val scope = rememberCoroutineScope()

        var hostInput by remember { mutableStateOf("") }
        var portInput by remember { mutableStateOf("") }
        var macInput by remember { mutableStateOf("") }
        var broadcastInput by remember { mutableStateOf("") }
        var subnetInput by remember { mutableStateOf("") }
        var pairingPinInput by remember { mutableStateOf("") }
        var qualityInput by remember { mutableFloatStateOf(50f) }
        var targetFpsInput by remember { mutableFloatStateOf(30f) }
        var scaleInput by remember { mutableFloatStateOf(0.6f) }
        var showHelpSection by remember { mutableStateOf(false) }

        // Pending flags for deferred actions after permission grants
        var pendingConnect by remember { mutableStateOf(false) }
        var pendingDiscover by remember { mutableStateOf(false) }

        // Snackbar state declared early so permission launchers below can reference it.
        val snackbarHostState = remember { SnackbarHostState() }

        // Permissions needed only for connect (POST_NOTIFICATIONS + NEARBY_WIFI_DEVICES + ACCESS_LOCAL_NETWORK)
        val connectPermissions = remember {
                buildList {
                                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                                        add(Manifest.permission.POST_NOTIFICATIONS)
                                        add(Manifest.permission.NEARBY_WIFI_DEVICES)
                                }
                                // SDK 37 (Android 17) requires ACCESS_LOCAL_NETWORK for LAN discovery
                                if (Build.VERSION.SDK_INT >= 36) {
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

        fun hasAllConnectPermissions(): Boolean {
                return connectPermissions.all {
                        ContextCompat.checkSelfPermission(context, it) ==
                                PackageManager.PERMISSION_GRANTED
                }
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
                                // Check if ACCESS_LOCAL_NETWORK was denied on Android 17+
                                val localNetworkDenied = Build.VERSION.SDK_INT >= 37 &&
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
                                doConnect()
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
                        if (targetFpsInput == 30f && desktopPrefs.targetFps != 30)
                                targetFpsInput = desktopPrefs.targetFps.toFloat()
                        if (scaleInput == 0.6f && desktopPrefs.scale != 0.6f) scaleInput =
                            desktopPrefs.scale
                }
        }

        // Autofill host/port when a host is discovered
        LaunchedEffect(discoveredHost) {
                discoveredHost?.let {
                        hostInput = it.host
                        portInput = it.port.toString()
                }
        }

        val scrollBehavior = rememberRemexTopBarScrollBehavior()
        LaunchedEffect(discoveredHost) {
                if (discoveredHost != null) {
                        snackbarHostState.showSnackbar("Host discovered: ${discoveredHost.host}")
                }
        }
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
                if (connectionPrefs == null || desktopPrefs == null) {
                        Box(
                                modifier = Modifier.fillMaxSize(),
                                contentAlignment = Alignment.Center
                        ) { RemexLoadingIndicator(contained = true) }
                } else {
                        Column(
                                modifier =
                                        Modifier.fillMaxSize()
                                                .padding(padding)
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
                                                        Text(
                                                                text = connectionError ?: "",
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodyMedium,
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .onErrorContainer,
                                                                modifier = Modifier.weight(1f)
                                                        )
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
                                                                                .titleSmall,
                                                                fontWeight = FontWeight.SemiBold,
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
                                                        if (isDiscovering) {
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

                                // --- How to connect help section ---
                                Card(
                                        modifier =
                                                Modifier.fillMaxWidth()
                                                        .animateContentSize()
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
                                                                                        .titleSmall,
                                                                        fontWeight =
                                                                                FontWeight.SemiBold,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        }
                                                        Icon(
                                                                if (showHelpSection)
                                                                        Icons.Default.ExpandLess
                                                                else Icons.Default.ExpandMore,
                                                                contentDescription = null,
                                                                tint =
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant
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
                                                                                                                .connection_platform_macos
                                                                                                ),
                                                                                        icon =
                                                                                                Icons.Default
                                                                                                        .Laptop,
                                                                                        instruction =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .connection_ip_macos
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
                                                                        .titleMedium,
                                                        fontWeight = FontWeight.SemiBold
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
                                                if (connectPermissions.isNotEmpty() &&
                                                                !hasAllConnectPermissions()
                                                ) {
                                                        pendingConnect = true
                                                        connectPermissionLauncher.launch(
                                                                connectPermissions
                                                        )
                                                } else {
                                                        doConnect()
                                                }
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        enabled = !isConnecting && hostInput.isNotEmpty()
                                ) {
                                        if (isConnecting) {
                                                RemexLoadingIndicator(
                                                        modifier = Modifier.size(24.dp),
                                                        color = MaterialTheme.colorScheme.onPrimary
                                                )
                                        } else {
                                                Text(stringResource(R.string.button_save_connect))
                                        }
                                }

                                Row(
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                                        modifier = Modifier.fillMaxWidth()
                                ) {
                                        Column(modifier = Modifier.weight(1f)) {
                                                Text(
                                                        text =
                                                                stringResource(
                                                                        R.string
                                                                                .connection_status_label,
                                                                        status
                                                                ),
                                                        style = MaterialTheme.typography.bodyMedium,
                                                        color =
                                                                if (status == "Connected")
                                                                        MaterialTheme.colorScheme
                                                                                .primary
                                                                else
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant
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

                                        if (isPaired) {
                                                Row(
                                                        verticalAlignment =
                                                                Alignment.CenterVertically,
                                                        horizontalArrangement =
                                                                Arrangement.spacedBy(4.dp)
                                                ) {
                                                        Icon(
                                                                Icons.Default.CheckCircle,
                                                                contentDescription = null,
                                                                tint =
                                                                        androidx.compose.ui.graphics
                                                                                .Color(0xFF4CAF50),
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
                                                                color =
                                                                        androidx.compose.ui.graphics
                                                                                .Color(0xFF4CAF50)
                                                        )
                                                        TextButton(
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .KEYBOARD_TAP
                                                                        )
                                                                        scope.launch {
                                                                                PinnedHostStore
                                                                                        .removePin(
                                                                                                context,
                                                                                                hostInput
                                                                                        )
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
            onNavigateToQrScanner = {},
            onConnect = { _, _, _, _, _, _, _, _, _ -> },
            onClearError = {},
            onDiscoverHost = {}
        )
    }
}

@Composable
private fun HelpStep(number: String, title: String, body: String?) {
        ListItem(
                headlineContent = {
                        Text(
                                title,
                                style = MaterialTheme.typography.bodyMedium,
                                fontWeight = FontWeight.SemiBold
                        )
                },
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
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onPrimary,
                                                fontWeight = FontWeight.Bold
                                        )
                                }
                        }
                },
                colors =
                        ListItemDefaults.colors(
                                containerColor = androidx.compose.ui.graphics.Color.Transparent
                        )
        )
}

@Composable
private fun IpInstructionRow(
        platform: String,
        icon: androidx.compose.ui.graphics.vector.ImageVector,
        instruction: String
) {
        ListItem(
                headlineContent = {
                        Text(
                                platform,
                                style = MaterialTheme.typography.labelMedium,
                                fontWeight = FontWeight.Bold
                        )
                },
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
        )
}
