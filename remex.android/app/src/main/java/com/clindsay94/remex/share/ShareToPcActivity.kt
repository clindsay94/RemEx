package com.clindsay94.remex.share

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.History
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.IntentCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.ui.screens.ConnectionOpenRequests
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.screens.openAppPermissionSettings
import com.clindsay94.remex.ui.screens.rememberConnectPermissionGate
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.delay

/**
 * Share-sheet target (plan WP8, RemEx-172ba). Registered for `ACTION_SEND` / `ACTION_SEND_MULTIPLE`
 * (all MIME types) so any app's "Share" can push files to a paired PC: the files, the target PC (the
 * connected one, or any paired PC to connect to), a destination folder (the PC's default, a recent
 * one, or any folder), then Send through [ShareToPcViewModel] and the existing `FileTransferEngine`.
 *
 * Cold launches are expected: the share sheet can start this process from nothing, so it initialises
 * the client itself and waits for the authenticated connection before asking for folders.
 */
class ShareToPcActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // Ensure the background connection loop is running so the destination picker can load roots.
        RemexClientManager.initialize(this)

        val uris = extractSharedUris(intent)

        setContent {
            RemExTheme {
                Surface(color = MaterialTheme.colorScheme.background) {
                    val vm: ShareToPcViewModel = viewModel()
                    // Feed the intent + kick a stale-staging sweep exactly once.
                    androidx.compose.runtime.LaunchedEffect(Unit) {
                        vm.setSharedFiles(uris)
                        vm.sweepStaleStaging()
                    }
                    ShareToPcScreen(
                        vm = vm,
                        onClose = { finish() },
                        onOpenPcs = {
                            startActivity(
                                Intent(this@ShareToPcActivity, MainActivity::class.java).apply {
                                    flags = Intent.FLAG_ACTIVITY_NEW_TASK or
                                        Intent.FLAG_ACTIVITY_CLEAR_TOP
                                    putExtra(ConnectionOpenRequests.EXTRA_OPEN_PCS, true)
                                }
                            )
                            finish()
                        },
                    )
                }
            }
        }
    }

    private fun extractSharedUris(intent: Intent?): List<Uri> {
        if (intent == null) return emptyList()
        // Any app can send this intent, and reading an extra unparcels the whole bundle, which can
        // throw. A share we cannot read is an empty share, which the screen says plainly.
        return try {
            ShareIntentParsing.sharedItems(
                action = intent.action,
                single = { IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java) },
                multiple = {
                    IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)
                },
                schemeOf = Uri::getScheme,
            )
        } catch (e: RuntimeException) {
            android.util.Log.w("ShareToPcActivity", "Could not read the shared items.", e)
            emptyList()
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShareToPcScreen(
    vm: ShareToPcViewModel,
    onClose: () -> Unit,
    onOpenPcs: () -> Unit,
) {
    val phase by vm.phase.collectAsStateWithLifecycle()
    val files by vm.files.collectAsStateWithLifecycle()
    val pairedPcs by vm.pairedPcs.collectAsStateWithLifecycle()
    val targetHost by vm.targetHost.collectAsStateWithLifecycle()
    val isConnecting by vm.isConnecting.collectAsStateWithLifecycle()
    val roots by vm.roots.collectAsStateWithLifecycle()
    val selectedRootId by vm.selectedRootId.collectAsStateWithLifecycle()
    val path by vm.path.collectAsStateWithLifecycle()
    val folders by vm.folders.collectAsStateWithLifecycle()
    val recentFolders by vm.recentFolders.collectAsStateWithLifecycle()
    val statusText by vm.statusText.collectAsStateWithLifecycle()
    val connectError by vm.connectErrorMessage.collectAsStateWithLifecycle()
    val connectErrorOpensSettings by vm.connectErrorOpensAppSettings.collectAsStateWithLifecycle()
    val context = LocalContext.current

    // The same gate as every other Connect: Android 17 refuses LAN sockets without the permission.
    val permissionGate = rememberConnectPermissionGate { permanently -> vm.reportLocalNetworkRefused(permanently) }
    val connectTo: (KnownPcEntry) -> Unit = { pc -> permissionGate.connect(pc.address) { vm.connectTo(pc) } }

    // Once the queue has accepted everything, briefly confirm and dismiss.
    androidx.compose.runtime.LaunchedEffect(phase) {
        if (phase == SharePhase.SENT) {
            delay(1200)
            onClose()
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResourceCompat(R.string.share_to_pc_title)) },
                navigationIcon = {
                    IconButton(onClick = onClose) {
                        Icon(
                            Icons.Filled.Close,
                            contentDescription = stringResourceCompat(R.string.share_to_pc_cancel),
                        )
                    }
                },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(horizontal = 16.dp),
        ) {
            when (phase) {
                SharePhase.EMPTY ->
                    CenteredMessage(stringResourceCompat(R.string.share_to_pc_nothing_to_send))
                SharePhase.NEEDS_SETUP ->
                    NeedsSetup(onOpenPcs)
                SharePhase.CONNECTING ->
                    CenteredProgress(stringResourceCompat(R.string.share_to_pc_connecting))
                SharePhase.SENT ->
                    CenteredMessage(stringResourceCompat(R.string.share_to_pc_queued))
                SharePhase.CHOOSE_PC -> {
                    FilesList(files)
                    Text(
                        stringResourceCompat(R.string.share_to_pc_choose_pc),
                        style = MaterialTheme.typography.titleSmall,
                        modifier = Modifier.padding(top = 12.dp, bottom = 4.dp),
                    )
                    connectError?.let { message ->
                        Text(
                            message,
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.error,
                            modifier = Modifier.padding(bottom = 4.dp),
                        )
                        if (connectErrorOpensSettings) {
                            TextButton(onClick = { openAppPermissionSettings(context) }) {
                                Text(stringResourceCompat(R.string.button_open_app_settings))
                            }
                        }
                    }
                    PcPicker(
                        pcs = pairedPcs,
                        connecting = isConnecting,
                        onPick = connectTo,
                        modifier = Modifier.weight(1f),
                    )
                    OutlinedButton(
                        onClick = onClose,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 12.dp),
                    ) { Text(stringResourceCompat(R.string.share_to_pc_cancel)) }
                }
                SharePhase.READY, SharePhase.SENDING ->
                    ReadyContent(
                        files = files,
                        targetName = pcName(targetHost, pairedPcs),
                        otherPcs = pairedPcs.filterNot { it.address.equals(targetHost, ignoreCase = true) },
                        roots = roots,
                        selectedRootId = selectedRootId,
                        path = path,
                        folders = folders,
                        recentFolders = recentFolders,
                        statusText = statusText,
                        sending = phase == SharePhase.SENDING,
                        onSwitchPc = connectTo,
                        onSelectRecent = vm::selectRecent,
                        onSelectRoot = vm::selectRoot,
                        onNavigate = vm::navigateInto,
                        onSend = vm::confirmSend,
                        onCancel = onClose,
                    )
            }
        }
    }
}

private fun pcName(host: String, pcs: List<KnownPcEntry>): String =
    pcs.firstOrNull { it.address.equals(host, ignoreCase = true) }?.displayName ?: host

@Composable
private fun FilesList(files: List<ShareFile>) {
    Text(
        stringResourceCompat(R.string.share_to_pc_files_to_send, files.size),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(top = 4.dp),
    )
    LazyColumn(modifier = Modifier.fillMaxWidth().heightIn(max = 160.dp)) {
        items(files) { f ->
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(Icons.AutoMirrored.Filled.InsertDriveFile, contentDescription = null)
                Spacer(Modifier.width(12.dp))
                Text(
                    f.name,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    FileManagerLogic.formatBytes(f.size),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** The paired PCs, one Connect each. Only trusted ones reach here, so a tap never asks for a PIN. */
@Composable
private fun PcPicker(
    pcs: List<KnownPcEntry>,
    connecting: Boolean,
    onPick: (KnownPcEntry) -> Unit,
    modifier: Modifier = Modifier,
) {
    LazyColumn(modifier = modifier.fillMaxWidth()) {
        items(pcs, key = { "${it.address}:${it.port}" }) { pc ->
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(Icons.Filled.Computer, contentDescription = null)
                Spacer(Modifier.width(12.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(pc.displayName, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (pc.isNamed) {
                        Text(
                            pc.address,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                Spacer(Modifier.width(8.dp))
                Button(onClick = { onPick(pc) }, enabled = !connecting) {
                    Text(stringResourceCompat(R.string.button_connect))
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ReadyContent(
    files: List<ShareFile>,
    targetName: String,
    otherPcs: List<KnownPcEntry>,
    roots: List<ShareRoot>,
    selectedRootId: String?,
    path: String,
    folders: List<ShareDirEntry>,
    recentFolders: List<ShareRecentFolder>,
    statusText: String,
    sending: Boolean,
    onSwitchPc: (KnownPcEntry) -> Unit,
    onSelectRecent: (ShareRecentFolder) -> Unit,
    onSelectRoot: (String) -> Unit,
    onNavigate: (ShareDirEntry) -> Unit,
    onSend: () -> Unit,
    onCancel: () -> Unit,
) {
    var choosingPc by rememberSaveable { mutableStateOf(false) }
    Column(modifier = Modifier.fillMaxSize()) {
        // Destination device, with the other paired PCs one tap away.
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Filled.Computer, contentDescription = null)
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    stringResourceCompat(R.string.share_to_pc_device_label),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(targetName, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (otherPcs.isNotEmpty() && !sending) {
                TextButton(onClick = { choosingPc = !choosingPc }) {
                    Text(stringResourceCompat(R.string.share_to_pc_change_pc))
                }
            }
        }
        if (choosingPc && otherPcs.isNotEmpty()) {
            PcPicker(
                pcs = otherPcs,
                connecting = sending,
                onPick = {
                    choosingPc = false
                    onSwitchPc(it)
                },
                modifier = Modifier.heightIn(max = 200.dp),
            )
        }

        FilesList(files)

        // Destination folder picker.
        Text(
            stringResourceCompat(R.string.share_to_pc_choose_folder),
            style = MaterialTheme.typography.titleSmall,
            modifier = Modifier.padding(top = 12.dp, bottom = 4.dp),
        )
        if (roots.isEmpty()) {
            Text(
                stringResourceCompat(R.string.share_to_pc_no_shared_folders),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error,
            )
        } else {
            if (recentFolders.isNotEmpty()) {
                Text(
                    stringResourceCompat(R.string.share_to_pc_recent_folders),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Row(
                    modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(bottom = 4.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    recentFolders.forEach { folder ->
                        AssistChip(
                            onClick = { onSelectRecent(folder) },
                            leadingIcon = { Icon(Icons.Filled.History, contentDescription = null, modifier = Modifier.size(18.dp)) },
                            label = {
                                Text(
                                    stringResourceCompat(R.string.share_to_pc_recent_folder_label, folder.rootName, folder.path),
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            },
                        )
                    }
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                roots.forEach { root ->
                    FilterChip(
                        selected = root.rootId == selectedRootId,
                        onClick = { onSelectRoot(root.rootId) },
                        label = { Text(root.displayName, maxLines = 1) },
                    )
                }
            }
            Text(
                stringResourceCompat(R.string.share_to_pc_folder_label, path),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(vertical = 6.dp),
            )
            LazyColumn(modifier = Modifier.fillMaxWidth().weight(1f)) {
                items(folders) { entry ->
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable { onNavigate(entry) }
                            .padding(vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(
                            if (entry.isParent) Icons.AutoMirrored.Filled.ArrowBack
                            else Icons.Filled.Folder,
                            contentDescription = null,
                        )
                        Spacer(Modifier.width(12.dp))
                        Text(
                            if (entry.isParent)
                                stringResourceCompat(R.string.share_to_pc_up)
                            else entry.name,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
        }

        if (statusText.isNotBlank()) {
            Text(
                statusText,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.error,
                modifier = Modifier.padding(top = 8.dp),
            )
        }

        // Bottom action bar.
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            OutlinedButton(onClick = onCancel, modifier = Modifier.weight(1f), enabled = !sending) {
                Text(stringResourceCompat(R.string.share_to_pc_cancel))
            }
            Button(
                onClick = onSend,
                modifier = Modifier.weight(1f),
                enabled = !sending && selectedRootId != null && roots.isNotEmpty(),
            ) {
                if (sending) {
                    RemexLoadingIndicator(
                        modifier = Modifier.size(18.dp),
                        color = MaterialTheme.colorScheme.onPrimary,
                    )
                } else {
                    Icon(
                        Icons.AutoMirrored.Filled.Send,
                        contentDescription = null,
                        modifier = Modifier.size(18.dp),
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(stringResourceCompat(R.string.share_to_pc_send))
                }
            }
        }
    }
}

@Composable
private fun NeedsSetup(onOpenPcs: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(horizontal = 8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            stringResourceCompat(R.string.share_to_pc_not_paired),
            style = MaterialTheme.typography.bodyLarge,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(16.dp))
        Button(onClick = onOpenPcs) {
            Text(stringResourceCompat(R.string.share_to_pc_open_pcs))
        }
    }
}

@Composable
private fun CenteredMessage(text: String) {
    Column(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(text, style = MaterialTheme.typography.bodyLarge, textAlign = TextAlign.Center)
    }
}

@Composable
private fun CenteredProgress(text: String) {
    Column(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        RemexLoadingIndicator()
        Spacer(Modifier.height(16.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}

// ── Small helpers kept private so the screen file stays self-contained ──────────

@Composable
private fun stringResourceCompat(id: Int): String =
    androidx.compose.ui.res.stringResource(id)

@Composable
private fun stringResourceCompat(id: Int, vararg formatArgs: Any): String =
    androidx.compose.ui.res.stringResource(id, *formatArgs)
