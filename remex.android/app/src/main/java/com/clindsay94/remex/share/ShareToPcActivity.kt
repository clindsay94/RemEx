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
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Send
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
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.IntentCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.delay

/**
 * Share-sheet target (plan WP8). Registered for `ACTION_SEND` / `ACTION_SEND_MULTIPLE` (all MIME types) so any
 * app's "Share" can push files straight to the paired PC. Extracts the shared URIs, lets the user pick
 * a destination folder on the peer, then enqueues push uploads through [ShareToPcViewModel] (which
 * hands off to the already-landed `FileTransferEngine`).
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
                        onOpenApp = {
                            startActivity(
                                Intent(this@ShareToPcActivity, MainActivity::class.java).apply {
                                    flags = Intent.FLAG_ACTIVITY_NEW_TASK or
                                        Intent.FLAG_ACTIVITY_CLEAR_TOP
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
        return when (intent.action) {
            Intent.ACTION_SEND -> {
                IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)
                    ?.let { listOf(it) } ?: emptyList()
            }
            Intent.ACTION_SEND_MULTIPLE -> {
                IntentCompat.getParcelableArrayListExtra(
                    intent,
                    Intent.EXTRA_STREAM,
                    Uri::class.java,
                ).orEmpty().filterNotNull()
            }
            else -> emptyList()
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShareToPcScreen(
    vm: ShareToPcViewModel,
    onClose: () -> Unit,
    onOpenApp: () -> Unit,
) {
    val phase by vm.phase.collectAsStateWithLifecycle()
    val files by vm.files.collectAsStateWithLifecycle()
    val hostLabel by vm.hostLabel.collectAsStateWithLifecycle()
    val roots by vm.roots.collectAsStateWithLifecycle()
    val selectedRootId by vm.selectedRootId.collectAsStateWithLifecycle()
    val path by vm.path.collectAsStateWithLifecycle()
    val folders by vm.folders.collectAsStateWithLifecycle()

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
                    NeedsSetup(onOpenApp)
                SharePhase.CONNECTING ->
                    CenteredProgress(stringResourceCompat(R.string.share_to_pc_connecting))
                SharePhase.SENT ->
                    CenteredMessage(stringResourceCompat(R.string.share_to_pc_queued))
                SharePhase.READY, SharePhase.SENDING ->
                    ReadyContent(
                        files = files,
                        hostLabel = hostLabel,
                        roots = roots,
                        selectedRootId = selectedRootId,
                        path = path,
                        folders = folders,
                        sending = phase == SharePhase.SENDING,
                        onSelectRoot = vm::selectRoot,
                        onNavigate = vm::navigateInto,
                        onSend = vm::confirmSend,
                        onCancel = onClose,
                    )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ReadyContent(
    files: List<ShareFile>,
    hostLabel: String,
    roots: List<ShareRoot>,
    selectedRootId: String?,
    path: String,
    folders: List<ShareDirEntry>,
    sending: Boolean,
    onSelectRoot: (String) -> Unit,
    onNavigate: (ShareDirEntry) -> Unit,
    onSend: () -> Unit,
    onCancel: () -> Unit,
) {
    Column(modifier = Modifier.fillMaxSize()) {
        // Destination device.
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Filled.Computer, contentDescription = null)
            Spacer(Modifier.width(12.dp))
            Column {
                Text(
                    stringResourceCompat(R.string.share_to_pc_device_label),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(hostLabel, style = MaterialTheme.typography.bodyLarge)
            }
        }

        // Files being sent.
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
                    Icon(Icons.Filled.Send, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text(stringResourceCompat(R.string.share_to_pc_send))
                }
            }
        }
    }
}

@Composable
private fun NeedsSetup(onOpenApp: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            stringResourceCompat(R.string.share_to_pc_not_configured),
            style = MaterialTheme.typography.bodyLarge,
        )
        Spacer(Modifier.height(16.dp))
        Button(onClick = onOpenApp) {
            Text(stringResourceCompat(R.string.share_to_pc_open_app))
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
        Text(text, style = MaterialTheme.typography.bodyLarge)
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
