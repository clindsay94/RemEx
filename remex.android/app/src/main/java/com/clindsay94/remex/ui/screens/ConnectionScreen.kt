package com.clindsay94.remex.ui.screens

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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.EstablishedConnection
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import kotlinx.coroutines.launch
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes
import androidx.compose.material3.ButtonDefaults
import com.clindsay94.remex.ui.theme.rememberAnimateBoundsModifier
import androidx.compose.ui.layout.LookaheadScope

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreen(
        viewModel: ConnectionViewModel = viewModel(),
        onNavigateToQrScanner: () -> Unit = {}
) {
        val connectionPrefs by viewModel.connectionPreferences.collectAsStateWithLifecycle()
        val isConnecting by viewModel.isConnecting.collectAsStateWithLifecycle()
        val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
        val connectedHost by RemexClientManager.connectedHost.collectAsStateWithLifecycle()
        val needsPairing by RemexClientManager.needsPairing.collectAsStateWithLifecycle()
        val status by viewModel.connectionStatus.collectAsStateWithLifecycle()
        val connectionError by viewModel.connectionError.collectAsStateWithLifecycle()
        val isCertMismatch by viewModel.isCertMismatch.collectAsStateWithLifecycle()
        val errorOpensAppSettings by viewModel.errorOpensAppSettings.collectAsStateWithLifecycle()
        val isDiscovering by viewModel.isDiscovering.collectAsStateWithLifecycle()
        val discoveredHost by viewModel.discoveredHost.collectAsStateWithLifecycle()
        val knownPcRows by viewModel.knownPcRows.collectAsStateWithLifecycle()
        val certRepair by viewModel.certRepair.collectAsStateWithLifecycle()

        // Leaving Connection drops a pending routine "Switch and run" (RemEx-pp0rt.12): it may only
        // start from a switch the person makes while they are here.
        val routineContext = androidx.compose.ui.platform.LocalContext.current
        androidx.compose.runtime.DisposableEffect(Unit) {
                onDispose { com.clindsay94.remex.routines.Routines.syncClient(routineContext).cancelPendingSwitchRun() }
        }

        ConnectionScreenContent(
                certRepair = certRepair,
                connectionPrefs = connectionPrefs,
                isConnecting = isConnecting,
                isConnected = isConnected,
                connectedHost = connectedHost,
                status = status,
                connectionError = connectionError,
                isCertMismatch = isCertMismatch,
                errorOpensAppSettings = errorOpensAppSettings,
                onLocalNetworkRefused = { permanently -> viewModel.reportLocalNetworkRefused(permanently) },
                isDiscovering = isDiscovering,
                discoveredHost = discoveredHost,
                knownPcRows = knownPcRows,
                onNavigateToQrScanner = onNavigateToQrScanner,
                onConnect = { host, port, mac, broadcast, subnet, pairingPin ->
                        viewModel.connect(host, port, mac, broadcast, subnet, pairingPin)
                },
                onClearError = { viewModel.clearError() },
                onDiscoverHost = { viewModel.discoverHost() },
                onRepair = { context, host, port -> viewModel.beginCertRepair(context, host, port) },
                onDismissCertRepair = { viewModel.dismissCertRepair() },
                onConfirmCertRepair = { context -> viewModel.confirmCertRepair(context) },
                onConsumeDiscoveredHost = { viewModel.consumeDiscoveredHost() },
                onConnectToKnownPc = { entry, pairingPin ->
                        viewModel.connectToKnownPc(entry, pairingPin)
                },
                onRenameKnownHost = { identity, nickname ->
                        viewModel.renameKnownHost(identity, nickname)
                },
                onUnpairKnownHost = { context, knownHost ->
                        viewModel.unpairKnownHost(context, knownHost)
                },
                onForgetRecentConnection = { address ->
                        viewModel.forgetRecentConnection(address)
                },
                onRefreshKnownHosts = { viewModel.refreshKnownHosts() },
                onUnpairAddress = { context, address -> viewModel.unpairAddress(context, address) },
                connectedNeedsPairing = isConnected && needsPairing,
        )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreenContent(
        connectionPrefs: SettingsManager.ConnectionPreferences?,
        isConnecting: Boolean,
        isConnected: Boolean,
        status: String,
        connectionError: String?,
        isCertMismatch: Boolean,
        isDiscovering: Boolean,
        discoveredHost: DiscoveredHost?,
        knownPcRows: List<KnownPcEntry>,
        certRepair: CertRepairPrompt? = null,
        connectedHost: EstablishedConnection? = null,
        onNavigateToQrScanner: () -> Unit,
        onConnect: (String, Int, String, String, String, String) -> Unit,
        onClearError: () -> Unit,
        onDiscoverHost: () -> Unit,
        onRepair: (android.content.Context, String, Int) -> Unit,
        onDismissCertRepair: () -> Unit = {},
        onConfirmCertRepair: (android.content.Context) -> Unit = {},
        onConsumeDiscoveredHost: () -> Unit,
        onConnectToKnownPc: (KnownPcEntry, String) -> Unit,
        onRenameKnownHost: (String, String) -> Unit,
        onUnpairKnownHost: (android.content.Context, KnownHost) -> Unit,
        onForgetRecentConnection: (String) -> Unit,
        onRefreshKnownHosts: () -> Unit,
        onUnpairAddress: (android.content.Context, String) -> Unit,
        /** The connected PC no longer recognises this phone (sweep P3): its card says so. */
        connectedNeedsPairing: Boolean = false,
        /** The error is a local-network refusal only the app's settings page can undo now. */
        errorOpensAppSettings: Boolean = false,
        /** A connect or discovery stopped because the local-network permission was refused. */
        onLocalNetworkRefused: (permanently: Boolean) -> Unit = {},
) {
        val haptics = rememberRemexHaptics()
        val context = LocalContext.current
        val scope = rememberCoroutineScope()
        val motionScheme = MaterialTheme.motionScheme

        var hostInput by remember { mutableStateOf("") }
        var portInput by remember { mutableStateOf("") }
        var macInput by remember { mutableStateOf("") }
        var broadcastInput by remember { mutableStateOf("") }
        var subnetInput by remember { mutableStateOf("") }
        var pairingPinInput by remember { mutableStateOf("") }
        var showHelpSection by remember { mutableStateOf(false) }

        // The sheets (RemEx-wqo7a.6). Held here, with the form fields, so every sheet reads and
        // writes the same state the old single form did and the connect paths are unchanged.
        var showAddPcSheet by remember { mutableStateOf(false) }
        var detailsEntry by remember { mutableStateOf<KnownPcEntry?>(null) }
        var connectionForm by remember { mutableStateOf<ConnectionFormRequest?>(null) }

        // Known PCs row actions (RemEx-k62t). Held at screen level rather than inside the row so a
        // dialog is not torn down by the very action it confirms — unpairing removes the row.
        var renamingHost by remember { mutableStateOf<KnownHost?>(null) }
        var unpairingHost by remember { mutableStateOf<KnownHost?>(null) }
        var nicknameInput by remember { mutableStateOf("") }

        // The row whose address is not pinned and is waiting on a PIN (RemEx-obxlo). Its own input
        // rather than the form's pairingPinInput: the form's PIN belongs to whatever the user typed
        // into the form, and sending that to a different machine would fail the pairing exchange
        // while looking like the row was broken.
        var pairingEntry by remember { mutableStateOf<KnownPcEntry?>(null) }
        var rowPinInput by remember { mutableStateOf("") }

        // Snackbar state, for the "PC found" message below.
        val snackbarHostState = remember { SnackbarHostState() }

        // THE FORMAT STRING RATHER THAN THE FORMATTED RESULT, because the host name is only known
        // inside the callback. Hoisting the template still gets the locale-correct text; only the
        // substitution happens late. Read in composable scope, not in the callback (RemEx-2evl3).
        val hostDiscoveredFormat = stringResource(R.string.host_discovered_snackbar)

        // The shared permission gate every connect path goes through (3.0 comb, qr-home-no-perms).
        // Each attempt is handed to it as a closure carrying its own target and PIN, so an attempt
        // that waits on the permission dialog resumes as itself. That matters for a Known PCs card:
        // falling back to the FORM would send the form's pairing PIN, and a 6-digit PIN makes
        // RemexClientManager drop the pinned hash and force a re-pair - a tap meant to RECONNECT to a
        // paired PC would try to pair it again with a PIN belonging to a different machine. A
        // Tailscale/VPN target needs no LAN permission, so a refusal there never blocks it.
        val permissionGate = rememberConnectPermissionGate(onLocalNetworkRefused)

        // Points the form at another PC. The Wake-on-LAN MAC is typed for ONE PC, so it follows the
        // host: kept only when it was entered for this address, cleared otherwise. Without this a
        // failed card connect left PC A's MAC in the field and the next manual Connect saved it as
        // PC B's (RemEx-pp4cm.3).
        fun retargetForm(newHost: String) {
                hostInput = newHost
                macInput = connectionPrefs?.let {
                        SettingsManager.macInputFor(it.macAddress, it.macManualHost, newHost)
                } ?: ""
        }

        fun doConnect() {
                val p = portInput.toIntOrNull() ?: 5005
                onConnect(
                        hostInput.trim(),
                        p,
                        macInput.trim(),
                        broadcastInput.trim().ifEmpty { "255.255.255.255" },
                        subnetInput.trim().ifEmpty { "255.255.255.0" },
                        pairingPinInput.trim()
                )
        }

        /**
         * The address form's Connect, from its button or the PIN field's Done key (3.0 comb,
         * ime-done). Permissions are scoped to the typed host: a loopback or Tailscale/VPN target
         * needs no LAN permission, so a refused local-network grant must NOT block it (the root cause
         * of "won't even try to connect" over Tailscale).
         */
        fun submitForm() {
                if (isConnecting || hostInput.isBlank()) return
                permissionGate.connect(hostInput) { doConnect() }
                // Back to Your PCs, where the status line and the card show how the attempt goes.
                // A deferred attempt keeps working: doConnect reads the screen's own state.
                connectionForm = null
        }

        /**
         * Starts a Known PCs row's connection, asking for permissions first if they are missing.
         *
         * Shared by the row tap and the pairing dialog's confirm so both take the same route: the
         * permission check is scoped to the row's own address, which is what lets a Tailscale row
         * proceed on a phone that has declined local-network access.
         */
        fun startKnownPcConnect(entry: KnownPcEntry, pin: String) {
                // Fill the form as well as connect: it shows what was tapped.
                retargetForm(entry.address)
                portInput = entry.port.toString()
                permissionGate.connect(entry.address) { onConnectToKnownPc(entry, pin) }
        }

        // Initialize inputs from saved values only once they are loaded
        // Remote Desktop defaults are no longer on this screen (RemEx-wqo7a.6): they live in the
        // stream's own settings sheet, and connecting no longer rewrites them.
        LaunchedEffect(connectionPrefs) {
                if (connectionPrefs != null) {
                        if (hostInput.isEmpty() && connectionPrefs.host.isNotEmpty()) hostInput =
                            connectionPrefs.host
                        if (portInput.isEmpty()) portInput = connectionPrefs.port.toString()
                        if (macInput.isEmpty()) macInput = SettingsManager.macInputFor(
                            connectionPrefs.macAddress, connectionPrefs.macManualHost, hostInput
                        )
                        if (broadcastInput.isEmpty()) broadcastInput = connectionPrefs.broadcastIp
                        if (subnetInput.isEmpty()) subnetInput = connectionPrefs.subnetMask
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
        LaunchedEffect(knownPcRows, connectionPrefs) {
                if (connectionPrefs == null) return@LaunchedEffect
                if (hostInput.isNotEmpty() || connectionPrefs.host.isNotEmpty()) return@LaunchedEffect
                // The rows are already most-recent-first, so the first one that has ever connected
                // IS the last address used — which is a sharper answer than the PC-level record it
                // replaced: that one named a machine, and a machine reached at three addresses could
                // only offer whichever of them it happened to have stored.
                knownPcRows.firstOrNull { it.hasEverConnected }?.let { mostRecent ->
                        retargetForm(mostRecent.address)
                        portInput = mostRecent.port.toString()
                }
        }

        // Autofill host/port and show snackbar when a host is discovered. Consume the event
        // *before* showing the snackbar (and fire the snackbar on the screen's own scope, not this
        // effect) so a configuration change — e.g. rotation — during the snackbar's few seconds can't
        // replay the 'PC found' message: the state is already cleared. (RemEx-b0lv)
        // A found PC opens straight into the "Add manually" form with its address filled in
        // (RemEx-wqo7a.6), which is where the old screen pointed: "fields filled in below".
        LaunchedEffect(discoveredHost) {
                discoveredHost?.let {
                        retargetForm(it.host)
                        portInput = it.port.toString()
                        val discoveredHostName = it.host
                        onConsumeDiscoveredHost()
                        showAddPcSheet = false
                        connectionForm = ConnectionFormRequest(ConnectionFormMode.Add, null)
                        scope.launch {
                                snackbarHostState.showSnackbar(
                                        hostDiscoveredFormat.format(discoveredHostName)
                                )
                        }
                }
        }

        /** A card's Connect: a paired address reconnects; an unpaired one asks for the PIN first. */
        fun connectKnownPc(entry: KnownPcEntry) {
                // AN UNPINNED ADDRESS PAIRS RATHER THAN CONNECTS, and it has to ask for the PIN
                // before the permission prompt: the connection cannot succeed without one, and
                // spending a system dialog on an attempt that is already doomed reads as the card
                // being broken.
                if (entry.isTrusted) {
                        startKnownPcConnect(entry, "")
                } else {
                        rowPinInput = ""
                        pairingEntry = entry
                }
        }

        /** "Discover automatically", asking for the nearby-devices permission first if needed. */
        fun startDiscovery() {
                if (isDiscovering) return
                permissionGate.discover { onDiscoverHost() }
        }

        /**
         * Opens the address form. Editing a PC fills in its own address and port; adding keeps
         * whatever the form already holds (the last PC tried, or the one discovery just found).
         */
        fun openConnectionForm(mode: ConnectionFormMode, entry: KnownPcEntry?) {
                if (entry != null) {
                        retargetForm(entry.address)
                        portInput = entry.port.toString()
                }
                connectionForm = ConnectionFormRequest(mode, entry)
        }

        val connectedEndpoint = connectedHost?.takeIf { isConnected }?.let { PcEndpoint(it.host, it.port) }
        // The stored host is written before the connection starts (ConnectionViewModel.connect), so
        // while a connection is being made it names the address being tried.
        val connectingEndpoint =
                connectionPrefs?.takeIf { isConnecting && it.host.isNotBlank() }?.let { PcEndpoint(it.host, it.port) }
        val yourPcCards =
                remember(knownPcRows, connectedEndpoint, connectingEndpoint, connectedNeedsPairing) {
                        YourPcCards.build(knownPcRows, connectedEndpoint, connectingEndpoint, connectedNeedsPairing)
                }

        // Home, Files or the process list asked for Add a PC (their Pair action, sweep P3).
        val addPcRequested by ConnectionOpenRequests.addPc.collectAsStateWithLifecycle()
        LaunchedEffect(addPcRequested) {
                if (ConnectionOpenRequests.consumeAddPc()) showAddPcSheet = true
        }

        val scrollBehavior = rememberRemexTopBarScrollBehavior()
        Scaffold(
                modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
                topBar = {
                        RemexFlexibleTopBar(
                                title = stringResource(R.string.screen_connection_title),
                                subtitle = stringResource(R.string.screen_connection_subtitle),
                                scrollBehavior = scrollBehavior
                        )
                },
                snackbarHost = { SnackbarHost(snackbarHostState) }
        ) { padding ->
                val prefsLoaded = connectionPrefs != null
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
                        val connectionScroll = rememberScrollState()
                        // Tapping the tab you are on goes back to the top (RemEx-pp4cm.3).
                        com.clindsay94.remex.ui.navigation.TabReselectEffect(com.clindsay94.remex.ui.navigation.Screen.Connection) { connectionScroll.animateScrollTo(0) }
                        Column(
                                modifier =
                                        Modifier.fillMaxSize()
                                                .padding(padding)
                                                .verticalScroll(connectionScroll)
                                                .padding(horizontal = 16.dp, vertical = 8.dp),
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
                                                                                // Opens the
                                                                                // comparison
                                                                                // dialog; nothing
                                                                                // is cleared until
                                                                                // the user
                                                                                // confirms there
                                                                                // (RemEx-vnps).
                                                                                onClick = {
                                                                                        onRepair(
                                                                                                context,
                                                                                                hostInput,
                                                                                                portInput.toIntOrNull()
                                                                                                        ?: 5005
                                                                                        )
                                                                                },
                                                                                modifier = Modifier.align(Alignment.End),
                                                                                shapes = rememberRemexButtonShapes(),
                                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
                                                                        ) {
                                                                                Text(stringResource(R.string.connection_action_repair), color = MaterialTheme.colorScheme.onErrorContainer)
                                                                        }
                                                                } else if (errorOpensAppSettings) {
                                                                        // Android has stopped asking, so the
                                                                        // settings page is the only way back
                                                                        // (3.0 comb, qr-perm-deadend).
                                                                        TextButton(
                                                                                onClick = {
                                                                                        haptics.perform(RemexHapticEvent.Press)
                                                                                        openAppPermissionSettings(context)
                                                                                },
                                                                                modifier = Modifier.align(Alignment.End),
                                                                                shapes = rememberRemexButtonShapes(),
                                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
                                                                        ) {
                                                                                Text(stringResource(R.string.button_open_app_settings), color = MaterialTheme.colorScheme.onErrorContainer)
                                                                        }
                                                                }
                                                        }
                                                        IconButton(
                                                                onClick = {
                                                                        haptics.perform(RemexHapticEvent.Press)
                                                                        onClearError()
                                                                },
                                                                shapes = rememberRemexIconButtonShapes()
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

                                // --- Your PCs (RemEx-wqo7a.6) ---
                                // The screen leads with the PCs this phone knows. Finding a new one
                                // (discovery, QR, typing the address) moved into the "Add a PC"
                                // sheet, and the address fields into "Add manually" and each PC's
                                // details sheet. Pairing, pinning and reconnecting are untouched:
                                // every path below ends in the same calls the old form made.
                                YourPcsSection(
                                        cards = yourPcCards,
                                        status = status,
                                        headerStatus =
                                                YourPcCards.headerStatus(
                                                        cards = yourPcCards,
                                                        isConnected = isConnected,
                                                        isConnecting = isConnecting,
                                                        connectedNeedsPairing = connectedNeedsPairing
                                                ),
                                        enabled = !isConnecting,
                                        onConnect = { entry -> connectKnownPc(entry) },
                                        onDetails = { entry -> detailsEntry = entry },
                                        onAddPc = { showAddPcSheet = true }
                                )

                                // Pairing a row whose address is not pinned (RemEx-obxlo). Its own
                                // PIN field rather than the form's: the form's PIN belongs to
                                // whatever the user typed there, and sending it to a different
                                // machine fails the pairing exchange while looking like this row
                                // does not work.
                                pairingEntry?.let { target ->
                                        AlertDialog(
                                                onDismissRequest = { pairingEntry = null },
                                                title = {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .connection_known_pc_pair_title,
                                                                        target.displayName
                                                                )
                                                        )
                                                },
                                                text = {
                                                        Column(
                                                                verticalArrangement =
                                                                        Arrangement.spacedBy(12.dp)
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_known_pc_pair_message
                                                                        )
                                                                )
                                                                OutlinedTextField(
                                                                        value = rowPinInput,
                                                                        onValueChange = {
                                                                                rowPinInput = it
                                                                        },
                                                                        singleLine = true,
                                                                        label = {
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_label_pairing_pin
                                                                                        )
                                                                                )
                                                                        },
                                                                        keyboardOptions =
                                                                                androidx.compose
                                                                                        .foundation
                                                                                        .text
                                                                                        .KeyboardOptions(
                                                                                                keyboardType =
                                                                                                        KeyboardType
                                                                                                                .NumberPassword,
                                                                                                imeAction =
                                                                                                        ImeAction.Done
                                                                                        ),
                                                                        // Done pairs, like the Pair button,
                                                                        // once there is a PIN to send
                                                                        // (3.0 comb, ime-done).
                                                                        keyboardActions =
                                                                                androidx.compose.foundation.text.KeyboardActions(
                                                                                        onDone = {
                                                                                                val pin = rowPinInput.trim()
                                                                                                if (pin.isNotBlank()) {
                                                                                                        haptics.perform(RemexHapticEvent.Press)
                                                                                                        pairingEntry = null
                                                                                                        rowPinInput = ""
                                                                                                        startKnownPcConnect(target, pin)
                                                                                                }
                                                                                        }
                                                                                )
                                                                )
                                                        }
                                                },
                                                confirmButton = {
                                                        TextButton(
                                                                // A blank PIN would reach
                                                                // connect() as "already paired",
                                                                // which for an unpinned address is
                                                                // a trust-on-first-use the user did
                                                                // not ask for.
                                                                enabled =
                                                                        rowPinInput
                                                                                .isNotBlank(),
                                                                onClick = {
                                                                        val pin =
                                                                                rowPinInput.trim()
                                                                        pairingEntry = null
                                                                        rowPinInput = ""
                                                                        startKnownPcConnect(
                                                                                target,
                                                                                pin
                                                                        )
                                                                },
                                                                shapes = rememberRemexButtonShapes(),
                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_known_pc_pair_confirm
                                                                        )
                                                                )
                                                        }
                                                },
                                                dismissButton = {
                                                        TextButton(
                                                                onClick = { pairingEntry = null },
                                                                shapes = rememberRemexButtonShapes(),
                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
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
                                                                },
                                                                shapes = rememberRemexButtonShapes(),
                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
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
                                                                onClick = { renamingHost = null },
                                                                shapes = rememberRemexButtonShapes(),
                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
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
                                                                        target.displayName
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
                                                                },
                                                                shapes = rememberRemexButtonShapes(),
                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
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
                                                                onClick = { unpairingHost = null },
                                                                shapes = rememberRemexButtonShapes(),
                                                                contentPadding = ButtonDefaults.TextButtonContentPadding
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

                                // The certificate-change confirmation (RemEx-vnps). What used to be
                                // behind the Re-pair tap was the clearing itself: an error the user
                                // wanted rid of, one button, and the pin protecting them from an
                                // impostor was gone before they had read the sentence above it.
                                // Nothing here clears anything — only the confirm button does.
                                certRepair?.let { prompt ->
                                        AlertDialog(
                                                // Tapping outside is a cancel, and cancel costs the
                                                // user nothing. Fail closed.
                                                onDismissRequest = onDismissCertRepair,
                                                icon = {
                                                        Icon(
                                                                Icons.Default.ErrorOutline,
                                                                contentDescription = null
                                                        )
                                                },
                                                title = {
                                                        Text(
                                                                stringResource(
                                                                        if (prompt.state ==
                                                                                        CertRepairState
                                                                                                .Unchanged
                                                                        )
                                                                                R.string
                                                                                        .connection_cert_repair_title_unchanged
                                                                        else
                                                                                R.string
                                                                                        .connection_cert_repair_title
                                                                )
                                                        )
                                                },
                                                text = {
                                                        Column(
                                                                modifier =
                                                                        Modifier.verticalScroll(
                                                                                rememberScrollState()
                                                                        ),
                                                                verticalArrangement =
                                                                        Arrangement.spacedBy(12.dp)
                                                        ) {
                                                                when (prompt.state) {
                                                                        CertRepairState.Checking ->
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_checking
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium
                                                                                )
                                                                        CertRepairState.Unchanged ->
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_unchanged
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium
                                                                                )
                                                                        CertRepairState.Unknown -> {
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_unknown
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium
                                                                                )
                                                                                // The warning
                                                                                // belongs here MORE
                                                                                // than under
                                                                                // Changed, not
                                                                                // less: this branch
                                                                                // also offers to
                                                                                // discard the pin,
                                                                                // and it does so
                                                                                // against a host
                                                                                // that did not even
                                                                                // answer.
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_when_unsafe
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium,
                                                                                        color =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .error
                                                                                )
                                                                        }
                                                                        CertRepairState.Changed -> {
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_changed
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium
                                                                                )
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_when_safe
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium
                                                                                )
                                                                                // The one line that
                                                                                // must not read as
                                                                                // routine. Error
                                                                                // ROLE, not a
                                                                                // literal — it has
                                                                                // to survive
                                                                                // monochrome and
                                                                                // contrast 1.0.
                                                                                Text(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .connection_cert_repair_when_unsafe
                                                                                        ),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .bodyMedium,
                                                                                        color =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .error
                                                                                )
                                                                        }
                                                                }

                                                                // SpkiFingerprint's own marker for
                                                                // an absent pin is a model value,
                                                                // not display text — this dialog is
                                                                // the first thing to put one on
                                                                // screen, and it must not be the
                                                                // one English word in a Ukrainian
                                                                // dialog. "Not known" and
                                                                // "checking" are also genuinely
                                                                // different states, and the user
                                                                // draws opposite conclusions from
                                                                // them.
                                                                val unavailable =
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_cert_repair_fingerprint_unavailable
                                                                        )
                                                                val checking =
                                                                        stringResource(
                                                                                R.string
                                                                                        .connection_cert_repair_fingerprint_checking
                                                                        )

                                                                CertFingerprintRow(
                                                                        label =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_cert_repair_label_pinned
                                                                                ),
                                                                        fingerprint =
                                                                                if (prompt.pinnedPin
                                                                                                .isNullOrBlank()
                                                                                )
                                                                                        unavailable
                                                                                else
                                                                                        prompt.pinnedFingerprint
                                                                )
                                                                CertFingerprintRow(
                                                                        label =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_cert_repair_label_presented
                                                                                ),
                                                                        fingerprint =
                                                                                when {
                                                                                        prompt.state ==
                                                                                                CertRepairState
                                                                                                        .Checking ->
                                                                                                checking
                                                                                        prompt.presentedPin
                                                                                                .isNullOrBlank() ->
                                                                                                unavailable
                                                                                        else ->
                                                                                                prompt.presentedFingerprint
                                                                                }
                                                                )

                                                                if (prompt.canRepair) {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_cert_repair_effect
                                                                                ),
                                                                                style =
                                                                                        MaterialTheme
                                                                                                .typography
                                                                                                .bodySmall,
                                                                                color =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .onSurfaceVariant
                                                                        )
                                                                }
                                                        }
                                                },
                                                confirmButton = {
                                                        // Absent entirely while checking, and when
                                                        // the certificate turns out to be
                                                        // unchanged. A disabled button still reads
                                                        // as "this is the way forward"; no button
                                                        // says there is nothing to decide.
                                                        if (prompt.canRepair) {
                                                                TextButton(
                                                                        onClick = {
                                                                                haptics.perform(RemexHapticEvent.Press)
                                                                                onConfirmCertRepair(
                                                                                        context
                                                                                )
                                                                        },
                                                                        shapes = rememberRemexButtonShapes(),
                                                                        contentPadding = ButtonDefaults.TextButtonContentPadding
                                                                ) {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .connection_cert_repair_confirm
                                                                                ),
                                                                                color =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .error
                                                                        )
                                                                }
                                                        }
                                                },
                                                dismissButton = {
                                                        TextButton(onClick = onDismissCertRepair, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.TextButtonContentPadding) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string.button_cancel
                                                                        )
                                                                )
                                                        }
                                                }
                                        )
                                }
                        }
                }
                }
        }

        // --- Add a PC: find it on the network, scan its QR code, or type it in ---
        if (showAddPcSheet) {
                AddPcSheet(
                        isDiscovering = isDiscovering,
                        problem =
                                if (isCertMismatch) stringResource(R.string.connection_error_cert_changed)
                                else connectionError,
                        onDismiss = { showAddPcSheet = false },
                        onDiscover = { startDiscovery() },
                        onScanQr = {
                                showAddPcSheet = false
                                onNavigateToQrScanner()
                        },
                        onAddManually = {
                                showAddPcSheet = false
                                openConnectionForm(ConnectionFormMode.Add, null)
                        }
                )
        }

        // --- A PC's details: connect, edit its connection details, rename, unpair or forget ---
        detailsEntry?.let { entry ->
                val card = yourPcCards.firstOrNull { it.key == entry.address }
                PcDetailsSheet(
                        entry = entry,
                        status = card?.status ?: if (entry.isTrusted) YourPcStatus.Ready else YourPcStatus.NeedsPairing,
                        enabled = !isConnecting,
                        onDismiss = { detailsEntry = null },
                        onConnect = {
                                detailsEntry = null
                                connectKnownPc(entry)
                        },
                        onEdit = {
                                detailsEntry = null
                                openConnectionForm(ConnectionFormMode.Edit, entry)
                        },
                        onRename = {
                                detailsEntry = null
                                entry.knownHost?.let {
                                        nicknameInput = it.nickname
                                        renamingHost = it
                                }
                        },
                        onUnpair = {
                                detailsEntry = null
                                entry.knownHost?.let { unpairingHost = it }
                        },
                        onForget = {
                                detailsEntry = null
                                onForgetRecentConnection(entry.address)
                        }
                )
        }

        // --- The address form: "Add manually", or a PC's "Edit connection details" ---
        // The same fields, permission handling and Save & Connect the form always had; it just lives
        // in a sheet now instead of under everything else on the screen.
        connectionForm?.let { form ->
                val formSheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))
                ModalBottomSheet(
                        onDismissRequest = { connectionForm = null },
                        sheetState = formSheetState
                ) {
                        Column(
                                modifier =
                                        Modifier.fillMaxWidth()
                                                // Before verticalScroll on purpose: this
                                                // shrinks the scroll VIEWPORT when the
                                                // keyboard opens, so the lower fields can be
                                                // scrolled above it. Applied after the scroll
                                                // it would only pad the content, leaving the
                                                // viewport itself behind the keyboard
                                                // (RemEx-a9ci).
                                                .imePadding()
                                                .verticalScroll(rememberScrollState())
                                                .padding(start = 24.dp, end = 24.dp, bottom = 24.dp),
                                verticalArrangement = Arrangement.spacedBy(16.dp)
                        ) {
                                Text(
                                        text =
                                                if (form.mode == ConnectionFormMode.Edit && form.entry != null)
                                                        stringResource(
                                                                R.string.connection_edit_details_title,
                                                                form.entry.displayName
                                                        )
                                                else stringResource(R.string.connection_add_manually),
                                        style = MaterialTheme.typography.titleLargeEmphasized
                                )

                                if (form.mode == ConnectionFormMode.Add) {
                                // --- How to connect help section ---
                                Card(
                                        modifier =
                                                Modifier.fillMaxWidth()
                                                        .animateContentSize(
                                                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                                                        )
                                                        .clickable {
                                                                haptics.perform(RemexHapticEvent.Press)
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
                                }

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
                                        // Done connects, as the button below does, instead of only
                                        // hiding the keyboard (3.0 comb, ime-done).
                                        keyboardActions =
                                                androidx.compose.foundation.text.KeyboardActions(
                                                        onDone = {
                                                                haptics.perform(RemexHapticEvent.Press)
                                                                submitForm()
                                                        }
                                                ),
                                        supportingText = {
                                                Text(
                                                        stringResource(
                                                                R.string.connection_hint_pairing_pin
                                                        )
                                                )
                                        }
                                )

                                Button(
                                        onClick = {
                                                haptics.perform(RemexHapticEvent.Press)
                                                submitForm()
                                        },
                                        modifier = Modifier.fillMaxWidth(),
                                        enabled = !isConnecting && hostInput.isNotEmpty(),
                                        shapes = rememberRemexButtonShapes(),
                                        contentPadding = ButtonDefaults.ContentPadding
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
                                                                        haptics.perform(RemexHapticEvent.Press)
                                                                        // In the ViewModel, not this
                                                                        // screen's scope: leaving the
                                                                        // screen mid-flush must not
                                                                        // leave the pin behind while
                                                                        // this says "unpaired". It also
                                                                        // refreshes the Known PCs list.
                                                                        onUnpairAddress(context, hostInput)
                                                                        isPaired = false
                                                                },
                                                                contentPadding =
                                                                        PaddingValues(
                                                                                horizontal = 8.dp,
                                                                                vertical = 0.dp
                                                                        ),
                                                                modifier = Modifier.height(32.dp),
                                                                shapes = rememberRemexButtonShapes()
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

/**
 * One labelled fingerprint in the certificate-change dialog (RemEx-vnps).
 *
 * Monospaced on purpose. The value is already grouped in fours by [SpkiFingerprint], and grouping
 * only helps if the groups line up vertically between the two rows — in a proportional face they do
 * not, and the comparison the dialog is asking the user to make gets harder than reading one long
 * run would have been.
 */
@Composable
private fun CertFingerprintRow(label: String, fingerprint: String) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Text(
            text = fingerprint,
            style = MaterialTheme.typography.bodyMedium,
            fontFamily = FontFamily.Monospace
        )
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
            isConnecting = false,
            isConnected = true,
            status = "Connected",
            connectionError = null,
            isDiscovering = false,
            discoveredHost = null,
            isCertMismatch = false,
            knownPcRows = run {
                val studio =
                    KnownHost(
                        identity = "0123456789abcdef",
                        nickname = "Studio PC",
                        addresses = listOf("192.168.1.10", "100.72.10.4"),
                        port = 5005,
                        lastConnectedAtMillis = 1_700_000_000_000L
                    )
                listOf(
                    // The same machine at two addresses, which is the case the address-keyed list
                    // exists for and the one the PC-keyed list could not show.
                    KnownPcEntry(
                        address = "192.168.1.10",
                        port = 5005,
                        nickname = "Studio PC",
                        lastConnectedAtMillis = 1_700_000_000_000L,
                        knownHost = studio,
                        isTrusted = true
                    ),
                    KnownPcEntry(
                        address = "100.72.10.4",
                        port = 5005,
                        nickname = "Studio PC",
                        lastConnectedAtMillis = 1_699_900_000_000L,
                        knownHost = studio,
                        isTrusted = true
                    ),
                    // Connected to once, no longer paired: still listed, and tapping it pairs.
                    KnownPcEntry(
                        address = "192.168.1.42",
                        port = 5005,
                        nickname = "",
                        lastConnectedAtMillis = 1_699_000_000_000L,
                        knownHost = null,
                        isTrusted = false
                    )
                )
            },
            onNavigateToQrScanner = {},
            connectedHost = EstablishedConnection(host = "192.168.1.10", port = 5005, epoch = 1L),
            onConnect = { _, _, _, _, _, _ -> },
            onClearError = {},
            onDiscoverHost = {},
            onRepair = { _, _, _ -> },
            onConsumeDiscoveredHost = {},
            onConnectToKnownPc = { _, _ -> },
            onRenameKnownHost = { _, _ -> },
            onUnpairKnownHost = { _, _ -> },
            onForgetRecentConnection = {},
            onRefreshKnownHosts = {},
            onUnpairAddress = { _, _ -> }
        )
    }
}

/** Which job the address form sheet is doing. */
enum class ConnectionFormMode {
        /** "Add manually": typing in a PC that isn't in the list yet. */
        Add,

        /** "Edit connection details" for a PC already in the list. */
        Edit,
}

/** The address form sheet that is open, and the PC it is editing, if any. */
private data class ConnectionFormRequest(val mode: ConnectionFormMode, val entry: KnownPcEntry?)

/** "Last connected 5 min ago", or "Not connected yet", in the phone's own relative-time words. */
@Composable
private fun lastConnectedText(entry: KnownPcEntry): String =
        if (entry.hasEverConnected) {
                stringResource(
                        R.string.connection_known_pc_last_connected,
                        // The system's own relative-time wording, so it is localized and formatted
                        // the way the rest of the phone does it, except under a minute, where the
                        // system says "0 minutes ago" (3.0 comb, zero-minutes-ago). The clock ticks
                        // once a minute so the text moves on by itself.
                        relativeTimeText(entry.lastConnectedAtMillis, rememberMinuteClock())
                )
        } else {
                stringResource(R.string.connection_known_pc_never_connected)
        }

/**
 * The top of the Connection screen (RemEx-wqo7a.6): a title, the connection status, one card per
 * remembered PC address, and "Add a PC".
 *
 * The status line has no line limit on purpose: it used to sit at the very bottom of a long form,
 * where a long "Connecting to …" was cut off by the edge of the screen (RemEx-wqo7a.1 item 5).
 */
@Composable
private fun YourPcsSection(
        cards: List<YourPcCard>,
        /** The view model's line, used while connecting ("Connecting to …") and when not connected. */
        status: String,
        /** What the header says, from the same status the cards use ([YourPcCards.headerStatus]). */
        headerStatus: YourPcStatus?,
        enabled: Boolean,
        onConnect: (KnownPcEntry) -> Unit,
        onDetails: (KnownPcEntry) -> Unit,
        onAddPc: () -> Unit
) {
        val haptics = rememberRemexHaptics()
        val motionScheme = MaterialTheme.motionScheme
        Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(
                                text = stringResource(R.string.connection_your_pcs_title),
                                style = MaterialTheme.typography.titleMediumEmphasized
                        )
                        // One source for the header and the cards (3.0 comb, conn-header-contradicts):
                        // it used to read "Connected" over a card saying "Needs pairing", with a raw
                        // "windows / interactive / desktop available" line under it.
                        val statusColor by animateColorAsState(
                                targetValue =
                                        when (headerStatus) {
                                                YourPcStatus.ConnectedNow -> MaterialTheme.colorScheme.primary
                                                YourPcStatus.ConnectedNeedsPairing -> MaterialTheme.colorScheme.error
                                                else -> MaterialTheme.colorScheme.onSurfaceVariant
                                        },
                                animationSpec = motionScheme.defaultEffectsSpec(),
                                label = "connectionStatusColor"
                        )
                        val statusText =
                                when (headerStatus) {
                                        YourPcStatus.ConnectedNow -> stringResource(R.string.status_connected)
                                        YourPcStatus.ConnectedNeedsPairing ->
                                                stringResource(R.string.connection_known_pc_needs_pairing)
                                        else -> status
                                }
                        Text(
                                text = stringResource(R.string.connection_status_label, statusText),
                                style = MaterialTheme.typography.bodyMedium,
                                color = statusColor
                        )
                }

                if (cards.isEmpty()) {
                        Text(
                                text = stringResource(R.string.connection_your_pcs_empty),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                }

                if (cards.isNotEmpty()) {
                        // The list re-sorts when a connection lands; in a lookahead scope each card
                        // glides to its new place instead of jumping there (phase 6, RemEx-wqo7a.8).
                        LookaheadScope {
                                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                                        cards.forEach { card ->
                                                // Keyed by address, not by position: the list re-sorts when a
                                                // connection lands, and a card's remembered state would otherwise
                                                // stay with the SLOT and end up acting on whichever address moved
                                                // into it.
                                                key(card.key) {
                                                        Box(rememberAnimateBoundsModifier(this@LookaheadScope)) {
                                                                YourPcCardView(
                                                                        card = card,
                                                                        enabled = enabled,
                                                                        onConnect = { onConnect(card.entry) },
                                                                        onDetails = { onDetails(card.entry) }
                                                                )
                                                        }
                                                }
                                        }
                                }
                        }
                }

                // The first-run primary action when there is nothing to reconnect to; a quieter
                // tonal button once the list has PCs in it, so "Connect" stays the loudest thing.
                val addPc: () -> Unit = {
                        haptics.perform(RemexHapticEvent.Press)
                        onAddPc()
                }
                if (cards.isEmpty()) {
                        Button(onClick = addPc, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                                Icon(Icons.Default.Add, contentDescription = null)
                                Spacer(Modifier.width(8.dp))
                                Text(stringResource(R.string.connection_add_pc))
                        }
                } else {
                        FilledTonalButton(onClick = addPc, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                                Icon(Icons.Default.Add, contentDescription = null)
                                Spacer(Modifier.width(8.dp))
                                Text(stringResource(R.string.connection_add_pc))
                        }
                }
        }
}

/**
 * One remembered PC address as a card (RemEx-wqo7a.6, replacing the RemEx-k62t/obxlo row).
 *
 * Tapping the card connects, because reconnecting is the common action here and it should not need
 * aim; the explicit Connect button gives a list of near-identical addresses an unambiguous target on
 * each card. The card for the live connection connects nowhere: tapping it opens its details.
 * Everything that changes or removes the PC (edit, rename, unpair, forget) sits one step away in the
 * details sheet, for the reason it always sat behind an overflow: an unpair one mis-tap from a
 * connect is one mis-tap from needing the PIN off the PC to undo.
 */
@Composable
private fun YourPcCardView(
        card: YourPcCard,
        enabled: Boolean,
        onConnect: () -> Unit,
        onDetails: () -> Unit
) {
        val haptics = rememberRemexHaptics()
        val entry = card.entry
        val displayName = entry.displayName
        Card(
                onClick = {
                        haptics.perform(RemexHapticEvent.Press)
                        if (card.isCurrent) onDetails() else onConnect()
                },
                enabled = enabled || card.isCurrent,
                shape = MaterialTheme.shapes.large,
                colors =
                        CardDefaults.cardColors(
                                containerColor = MaterialTheme.colorScheme.surfaceContainer,
                                disabledContainerColor = MaterialTheme.colorScheme.surfaceContainer
                        ),
                modifier = Modifier.fillMaxWidth()
        ) {
                Row(
                        modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 12.dp, end = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                        Icon(
                                Icons.Default.Computer,
                                contentDescription = null,
                                tint =
                                        if (entry.isTrusted) MaterialTheme.colorScheme.primary
                                        else MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Column(
                                modifier = Modifier.weight(1f),
                                verticalArrangement = Arrangement.spacedBy(2.dp)
                        ) {
                                Text(displayName, style = MaterialTheme.typography.titleSmallEmphasized)
                                // Only when the headline is a name (nickname or the PC's machine
                                // name, RemEx-odqj5); otherwise it already IS the address.
                                if (entry.isNamed) {
                                        Text(
                                                "${entry.address}:${entry.port}",
                                                style = MaterialTheme.typography.bodySmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                }
                                Text(
                                        lastConnectedText(entry),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                YourPcStatusLabel(card.status)
                        }
                        if (!card.isCurrent) {
                                FilledTonalButton(
                                        onClick = {
                                                haptics.perform(RemexHapticEvent.Press)
                                                onConnect()
                                        },
                                        enabled = enabled,
                                        contentPadding = PaddingValues(horizontal = 16.dp),
                                        shapes = rememberRemexButtonShapes()
                                ) {
                                        Text(stringResource(R.string.button_connect))
                                }
                        }
                        IconButton(onClick = onDetails, shapes = rememberRemexIconButtonShapes()) {
                                Icon(
                                        Icons.Default.MoreVert,
                                        contentDescription =
                                                stringResource(R.string.connection_known_pc_actions, displayName)
                                )
                        }
                }
        }
}

/**
 * The card's status in a word or two. Colour carries meaning only: tertiary for the live
 * connection, the tertiary text for "needs pairing" (said before the tap, because pairing needs the
 * PIN showing on the PC and someone who isn't at it should learn that from the card).
 */
@Composable
private fun YourPcStatusLabel(status: YourPcStatus) {
        when (status) {
                YourPcStatus.ConnectedNow ->
                        Surface(
                                color = MaterialTheme.colorScheme.tertiaryContainer,
                                contentColor = MaterialTheme.colorScheme.onTertiaryContainer,
                                shape = MaterialTheme.shapes.small
                        ) {
                                Row(
                                        modifier = Modifier.padding(horizontal = 8.dp, vertical = 2.dp),
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(4.dp)
                                ) {
                                        Icon(
                                                Icons.Default.CheckCircle,
                                                contentDescription = null,
                                                modifier = Modifier.size(14.dp)
                                        )
                                        Text(
                                                stringResource(R.string.connection_pc_status_connected),
                                                style = MaterialTheme.typography.labelMedium
                                        )
                                }
                        }
                YourPcStatus.Connecting ->
                        Text(
                                stringResource(R.string.connection_pc_status_connecting),
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.primary
                        )
                YourPcStatus.NeedsPairing, YourPcStatus.ConnectedNeedsPairing ->
                        Text(
                                stringResource(R.string.connection_known_pc_needs_pairing),
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.tertiary
                        )
                YourPcStatus.Ready -> Unit
        }
}

/**
 * "Add a PC" (RemEx-wqo7a.6): the three ways in, all of them the flows the screen already had —
 * the network search, the QR code the PC shows, and typing the address. Nothing new on the wire.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AddPcSheet(
        isDiscovering: Boolean,
        problem: String?,
        onDismiss: () -> Unit,
        onDiscover: () -> Unit,
        onScanQr: () -> Unit,
        onAddManually: () -> Unit
) {
        val haptics = rememberRemexHaptics()
        val motionScheme = MaterialTheme.motionScheme
        val sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))
        ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
                Column(
                        modifier =
                                Modifier.fillMaxWidth()
                                        .verticalScroll(rememberScrollState())
                                        .padding(start = 24.dp, end = 24.dp, bottom = 24.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                        Text(
                                stringResource(R.string.connection_add_pc),
                                style = MaterialTheme.typography.titleLargeEmphasized
                        )
                        Text(
                                stringResource(R.string.connection_auto_discover_hint),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Button(
                                onClick = {
                                        haptics.perform(RemexHapticEvent.Press)
                                        onDiscover()
                                },
                                enabled = !isDiscovering,
                                modifier = Modifier.fillMaxWidth(),
                                shapes = rememberRemexButtonShapes(),
                                contentPadding = ButtonDefaults.ContentPadding
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
                                                                modifier = Modifier.size(24.dp),
                                                                color = MaterialTheme.colorScheme.onPrimary
                                                        )
                                                        Spacer(modifier = Modifier.width(8.dp))
                                                        Text(stringResource(R.string.connection_searching))
                                                } else {
                                                        Icon(Icons.Default.Search, contentDescription = null)
                                                        Spacer(modifier = Modifier.width(8.dp))
                                                        Text(stringResource(R.string.connection_discover_button))
                                                }
                                        }
                                }
                        }
                        // A search that found nothing says so here, in the sheet the person is
                        // looking at, not only on the screen underneath it.
                        if (problem != null && !isDiscovering) {
                                Text(
                                        problem,
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = MaterialTheme.colorScheme.error
                                )
                        }
                        FilledTonalButton(
                                onClick = {
                                        haptics.perform(RemexHapticEvent.Press)
                                        onScanQr()
                                },
                                modifier = Modifier.fillMaxWidth(),
                                shapes = rememberRemexButtonShapes(),
                                contentPadding = ButtonDefaults.ContentPadding
                        ) {
                                Icon(Icons.Default.QrCodeScanner, contentDescription = null)
                                Spacer(modifier = Modifier.width(8.dp))
                                Text(stringResource(R.string.connection_scan_qr_code))
                        }
                        OutlinedButton(
                                onClick = {
                                        haptics.perform(RemexHapticEvent.Press)
                                        onAddManually()
                                },
                                modifier = Modifier.fillMaxWidth(),
                                shapes = rememberRemexButtonShapes(),
                                contentPadding = ButtonDefaults.ContentPadding
                        ) {
                                Icon(Icons.Default.Edit, contentDescription = null)
                                Spacer(modifier = Modifier.width(8.dp))
                                Text(stringResource(R.string.connection_add_manually))
                        }
                }
        }
}

/**
 * One PC's details (RemEx-wqo7a.6): what the card says, plus everything that changes the PC. The
 * actions are the Known PCs overflow's, unchanged: rename and unpair act on the paired machine, and
 * an address whose PC is no longer paired anywhere offers "remove from list" instead, since unpair
 * would have nothing to act on and the address would otherwise sit in the list forever.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PcDetailsSheet(
        entry: KnownPcEntry,
        status: YourPcStatus,
        enabled: Boolean,
        onDismiss: () -> Unit,
        onConnect: () -> Unit,
        onEdit: () -> Unit,
        onRename: () -> Unit,
        onUnpair: () -> Unit,
        onForget: () -> Unit
) {
        val haptics = rememberRemexHaptics()
        val sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))
        ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
                Column(
                        modifier =
                                Modifier.fillMaxWidth()
                                        .verticalScroll(rememberScrollState())
                                        .padding(start = 24.dp, end = 24.dp, bottom = 24.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                        Text(entry.displayName, style = MaterialTheme.typography.titleLargeEmphasized)
                        Text(
                                "${entry.address}:${entry.port}",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Text(
                                lastConnectedText(entry),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        YourPcStatusLabel(status)
                        Spacer(Modifier.height(8.dp))
                        if (status != YourPcStatus.ConnectedNow && status != YourPcStatus.ConnectedNeedsPairing) {
                                Button(
                                        onClick = {
                                                haptics.perform(RemexHapticEvent.Press)
                                                onConnect()
                                        },
                                        enabled = enabled,
                                        modifier = Modifier.fillMaxWidth(),
                                        shapes = rememberRemexButtonShapes(),
                                        contentPadding = ButtonDefaults.ContentPadding
                                ) {
                                        Text(stringResource(R.string.button_connect))
                                }
                        }
                        FilledTonalButton(onClick = onEdit, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                                Icon(Icons.Default.Edit, contentDescription = null)
                                Spacer(Modifier.width(8.dp))
                                Text(stringResource(R.string.connection_edit_details))
                        }
                        if (entry.knownHost != null) {
                                OutlinedButton(onClick = onRename, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                                        Text(stringResource(R.string.connection_known_pc_rename))
                                }
                                OutlinedButton(
                                        onClick = onUnpair,
                                        modifier = Modifier.fillMaxWidth(),
                                        colors =
                                                ButtonDefaults.outlinedButtonColors(
                                                        contentColor = MaterialTheme.colorScheme.error
                                                ),
                                        shapes = rememberRemexButtonShapes(),
                                        contentPadding = ButtonDefaults.ContentPadding
                                ) {
                                        Text(stringResource(R.string.connection_unpair))
                                }
                        } else {
                                OutlinedButton(onClick = onForget, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                                        Text(stringResource(R.string.connection_known_pc_forget))
                                }
                        }
                }
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
