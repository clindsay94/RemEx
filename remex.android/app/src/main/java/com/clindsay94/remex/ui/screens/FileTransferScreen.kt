package com.clindsay94.remex.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DriveFileRenameOutline
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.service.FileManageOperations
import com.clindsay94.remex.ui.components.FileManagerBreadcrumbs
import com.clindsay94.remex.ui.components.FileManagerDestinationSheet
import com.clindsay94.remex.ui.components.FileManagerGridItem
import com.clindsay94.remex.ui.components.FileManagerListItem
import com.clindsay94.remex.ui.components.FileManagerPropertiesSheet
import com.clindsay94.remex.ui.components.FileManagerQueuePanel
import com.clindsay94.remex.ui.components.FileManagerQuickAccess
import com.clindsay94.remex.ui.components.FileManagerSelectionBar
import com.clindsay94.remex.ui.components.FileManagerTextDialog
import com.clindsay94.remex.ui.components.FileManagerToolbar
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileTransferScreen(
    onNavigateToConnection: () -> Unit = {},
    vm: FileTransferViewModel = viewModel(),
) {
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val remotePath by vm.remotePath.collectAsStateWithLifecycle()
    val displayedEntries by vm.displayedEntries.collectAsStateWithLifecycle()
    val remoteRoots by vm.remoteRoots.collectAsStateWithLifecycle()
    val volumes by vm.volumes.collectAsStateWithLifecycle()
    val selectedRootId by vm.selectedRootId.collectAsStateWithLifecycle()
    val capabilities by vm.capabilities.collectAsStateWithLifecycle()
    val isLoading by vm.isLoading.collectAsStateWithLifecycle()
    val isRefreshing by vm.isRefreshing.collectAsStateWithLifecycle()
    val statusText by vm.statusText.collectAsStateWithLifecycle()
    val isSelectionMode by vm.isSelectionMode.collectAsStateWithLifecycle()
    val selectedEntryNames by vm.selectedEntryNames.collectAsStateWithLifecycle()
    val sortOption by vm.sortOption.collectAsStateWithLifecycle()
    val viewMode by vm.viewMode.collectAsStateWithLifecycle()
    val searchQuery by vm.searchQuery.collectAsStateWithLifecycle()
    val searchActive by vm.searchActive.collectAsStateWithLifecycle()
    val searchTruncated by vm.searchTruncated.collectAsStateWithLifecycle()
    val thumbnails by vm.thumbnails.collectAsStateWithLifecycle()
    val transferQueue by vm.transferQueue.collectAsStateWithLifecycle()
    val properties by vm.properties.collectAsStateWithLifecycle()
    val propertiesLoading by vm.propertiesLoading.collectAsStateWithLifecycle()
    val destinationPath by vm.destinationPath.collectAsStateWithLifecycle()
    val destinationEntries by vm.destinationEntries.collectAsStateWithLifecycle()
    val destinationLoading by vm.destinationLoading.collectAsStateWithLifecycle()
    val isTransferring by vm.isTransferring.collectAsStateWithLifecycle()
    val transferProgress by vm.transferProgress.collectAsStateWithLifecycle()

    val selectedRoot = remoteRoots.firstOrNull { it.rootId == selectedRootId }
    val selectedVolume = volumes.firstOrNull { it.id == selectedRootId }
    val rootLabel = selectedRoot?.displayName ?: selectedVolume?.label ?: "/"
    val canWrite = selectedRoot?.isWritable ?: (selectedVolume != null)
    val canDelete = selectedRoot?.canDelete ?: (selectedVolume != null)
    val canRename = selectedRoot?.canRename ?: (selectedVolume != null)
    val padding = calculateAdaptivePadding(1f)

    // ── Pickers ────────────────────────────────────────────────────────────────
    val uploadLauncher = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri != null) vm.uploadFromUri(uri)
    }
    var pendingDownloadEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    val createDocumentLauncher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
        val entry = pendingDownloadEntry
        pendingDownloadEntry = null
        if (uri != null && entry != null) vm.downloadEntryTo(entry, uri)
    }

    // ── Dialog / sheet state ────────────────────────────────────────────────────
    var renameTarget by remember { mutableStateOf<RemoteFileEntry?>(null) }
    var showNewFolder by remember { mutableStateOf(false) }
    var contextMenuEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    var destinationMode by remember { mutableStateOf<String?>(null) } // FileManageOperations.COPY / MOVE

    fun startDownload(entry: RemoteFileEntry) {
        pendingDownloadEntry = entry
        createDocumentLauncher.launch(entry.name)
    }

    // ── Dialogs / sheets ────────────────────────────────────────────────────────
    renameTarget?.let { target ->
        FileManagerTextDialog(
            title = stringResource(R.string.file_transfer_rename_title),
            hint = stringResource(R.string.file_transfer_rename_hint),
            confirmLabel = stringResource(R.string.file_transfer_rename_confirm),
            initialValue = target.name,
            onConfirm = { vm.renameEntry(target, it) },
            onDismiss = { renameTarget = null },
        )
    }
    if (showNewFolder) {
        FileManagerTextDialog(
            title = stringResource(R.string.file_manager_new_folder),
            hint = stringResource(R.string.file_manager_folder_name_hint),
            confirmLabel = stringResource(R.string.file_manager_create),
            onConfirm = { vm.createFolder(it) },
            onDismiss = { showNewFolder = false },
        )
    }
    if (propertiesLoading || properties != null) {
        FileManagerPropertiesSheet(
            properties = properties,
            thumbnailBase64 = properties?.relativePath?.let { thumbnails[it] },
            onDismiss = vm::dismissProperties,
        )
    }
    destinationMode?.let { mode ->
        FileManagerDestinationSheet(
            isMove = mode == FileManageOperations.MOVE,
            destinationPath = destinationPath,
            entries = destinationEntries,
            loading = destinationLoading,
            onNavigate = vm::navigateDestinationInto,
            onConfirm = { dest ->
                if (mode == FileManageOperations.MOVE) vm.moveSelectedTo(dest) else vm.copySelectedTo(dest)
                destinationMode = null
            },
            onDismiss = { destinationMode = null },
        )
    }
    contextMenuEntry?.let { entry ->
        ContextMenuSheet(
            entry = entry,
            canRename = canRename,
            canDelete = canDelete,
            onDismiss = { contextMenuEntry = null },
            onDownload = { contextMenuEntry = null; startDownload(entry) },
            onRename = { contextMenuEntry = null; renameTarget = entry },
            onDelete = { contextMenuEntry = null; vm.deleteEntry(entry) },
            onProperties = { contextMenuEntry = null; vm.showProperties(entry) },
            onPin = { contextMenuEntry = null; vm.navigateInto(entry); vm.pinCurrentFolder() },
        )
    }

    val topBarScrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
        modifier = Modifier.nestedScroll(topBarScrollBehavior.nestedScrollConnection),
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.screen_file_transfer_title),
                subtitle = "$rootLabel • $remotePath",
                scrollBehavior = topBarScrollBehavior,
            )
        },
    ) { innerPadding ->
        Column(modifier = Modifier.fillMaxSize().padding(innerPadding).padding(horizontal = padding)) {
            if (!isConnected) {
                DisconnectedContent(onNavigateToConnection)
                return@Column
            }

            FileManagerToolbar(
                searchQuery = searchQuery,
                onSearchChange = vm::setSearchQuery,
                sortOption = sortOption,
                onSort = vm::setSort,
                viewMode = viewMode,
                onToggleViewMode = vm::toggleViewMode,
                canWrite = canWrite && !searchActive,
                onNewFolder = { showNewFolder = true },
                onUpload = { uploadLauncher.launch("*/*") },
                modifier = Modifier.padding(vertical = 4.dp),
            )

            FileManagerQuickAccess(
                roots = remoteRoots,
                volumes = volumes,
                selectedRootId = selectedRootId,
                canBrowseDevice = capabilities?.fullBrowse == true,
                onSelectRoot = vm::selectRoot,
                onSelectVolume = vm::selectVolume,
                onBrowseDevice = vm::loadVolumes,
                modifier = Modifier.padding(bottom = 4.dp),
            )

            if (!searchActive) {
                FileManagerBreadcrumbs(
                    crumbs = FileManagerLogic.buildBreadcrumbs(rootLabel, remotePath),
                    onNavigate = vm::navigateToPath,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                )
            }

            if (isSelectionMode) {
                FileManagerSelectionBar(
                    selectedCount = selectedEntryNames.size,
                    canWrite = canWrite,
                    canDelete = canDelete,
                    onClose = vm::clearSelection,
                    onSelectAll = vm::selectAll,
                    onCopy = { vm.openDestinationPicker(); destinationMode = FileManageOperations.COPY },
                    onMove = { vm.openDestinationPicker(); destinationMode = FileManageOperations.MOVE },
                    onDelete = vm::deleteSelectedEntries,
                    modifier = Modifier.padding(vertical = 4.dp),
                )
            }

            // The three stacked header notices grow/shrink instead of shoving the list in
            // one frame (RemEx-z01v).
            AnimatedVisibility(
                visible = searchActive && searchTruncated,
                enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
            ) {
                Text(
                    text = stringResource(R.string.file_manager_search_truncated),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 2.dp),
                )
            }

            // Remembers its last non-blank value so the shrink-out exit still has text.
            var lastStatusText by remember { mutableStateOf(statusText) }
            if (statusText.isNotBlank()) lastStatusText = statusText
            AnimatedVisibility(
                visible = statusText.isNotBlank(),
                enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
            ) {
                Text(
                    text = lastStatusText,
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 2.dp),
                )
            }

            AnimatedVisibility(
                visible = isTransferring,
                enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                    fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
            ) {
                Column {
                    RemexLinearWavyProgress(progress = transferProgress, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp))
                    OutlinedButton(onClick = vm::cancelLegacyTransfer, modifier = Modifier.fillMaxWidth()) {
                        Text(stringResource(R.string.file_transfer_cancel))
                    }
                }
            }

            Box(modifier = Modifier.weight(1f)) {
                PullToRefreshBox(
                    isRefreshing = isRefreshing,
                    onRefresh = vm::refresh,
                    modifier = Modifier.fillMaxSize(),
                ) {
                    // The four body states (loading / no roots / empty / content) cross-fade through
                    // AnimatedContent, keyed on kind + view mode. Entry-list changes deliberately do
                    // NOT re-key the region: rows animate individually via animateItem below
                    // (RemEx-598q reconciling RemEx-h9rd), so folder navigation, sorting, searching
                    // and renames animate per row while LIST/GRID and state-kind changes cross-fade.
                    val bodyState = when {
                        isLoading && displayedEntries.isEmpty() ->
                            FileManagerBodyState(FileManagerBodyState.Kind.Loading, viewMode)
                        remoteRoots.isEmpty() && volumes.isEmpty() ->
                            FileManagerBodyState(FileManagerBodyState.Kind.NoRoots, viewMode)
                        displayedEntries.isEmpty() ->
                            FileManagerBodyState(FileManagerBodyState.Kind.Empty, viewMode)
                        else -> FileManagerBodyState(FileManagerBodyState.Kind.Content, viewMode)
                    }
                    val effectsSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
                    val itemPlacementSpec = MaterialTheme.motionScheme.fastSpatialSpec<IntOffset>()
                    AnimatedContent(
                        targetState = bodyState,
                        transitionSpec = { fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec) },
                        modifier = Modifier.fillMaxSize(),
                    ) { state ->
                        when (state.kind) {
                            FileManagerBodyState.Kind.Loading -> {
                                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                                    RemexLoadingIndicator(contained = true)
                                }
                            }
                            FileManagerBodyState.Kind.NoRoots -> {
                                CenteredMessage(stringResource(R.string.file_transfer_no_shared_folders))
                            }
                            FileManagerBodyState.Kind.Empty -> {
                                CenteredMessage(
                                    if (searchActive) stringResource(R.string.file_manager_no_results)
                                    else stringResource(R.string.file_transfer_empty)
                                )
                            }
                            FileManagerBodyState.Kind.Content -> if (state.viewMode == FileViewMode.GRID) {
                                LazyVerticalGrid(columns = GridCells.Adaptive(minSize = 100.dp), modifier = Modifier.fillMaxSize()) {
                                    items(displayedEntries, key = { it.relativePath ?: it.name }) { entry ->
                                        FileManagerGridItem(
                                            modifier = Modifier.animateItem(
                                                fadeInSpec = effectsSpec,
                                                placementSpec = itemPlacementSpec,
                                                fadeOutSpec = effectsSpec,
                                            ),
                                            entry = entry,
                                            isSelectionMode = isSelectionMode,
                                            isSelected = entry.name in selectedEntryNames,
                                            thumbnailBase64 = thumbnails[entry.relativePath ?: FileManagerLogic.combinePath(remotePath, entry.name)],
                                            showOverflow = entry.name != FileManagerLogic.PARENT_ENTRY,
                                            onRequestThumbnail = { vm.requestThumbnail(entry) },
                                            onTap = {
                                                if (isSelectionMode) vm.toggleEntrySelection(entry)
                                                else if (entry.isDirectory) vm.navigateInto(entry)
                                                else vm.showProperties(entry)
                                            },
                                            onLongPress = { vm.enterSelectionMode(entry) },
                                            onOverflow = { contextMenuEntry = entry },
                                        )
                                    }
                                }
                            } else {
                                LazyColumn(modifier = Modifier.fillMaxSize()) {
                                    items(displayedEntries, key = { it.relativePath ?: it.name }) { entry ->
                                        FileManagerListItem(
                                            modifier = Modifier.animateItem(
                                                fadeInSpec = effectsSpec,
                                                placementSpec = itemPlacementSpec,
                                                fadeOutSpec = effectsSpec,
                                            ),
                                            entry = entry,
                                            isSelectionMode = isSelectionMode,
                                            isSelected = entry.name in selectedEntryNames,
                                            thumbnailBase64 = thumbnails[entry.relativePath ?: FileManagerLogic.combinePath(remotePath, entry.name)],
                                            showDownload = true,
                                            showOverflow = entry.name != FileManagerLogic.PARENT_ENTRY,
                                            onRequestThumbnail = { vm.requestThumbnail(entry) },
                                            onTap = {
                                                if (isSelectionMode) vm.toggleEntrySelection(entry)
                                                else vm.navigateInto(entry)
                                            },
                                            onLongPress = { vm.enterSelectionMode(entry) },
                                            onDownload = { startDownload(entry) },
                                            onOverflow = { contextMenuEntry = entry },
                                        )
                                        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
                                    }
                                }
                            }
                        }
                    }
                }
            }

            FileManagerQueuePanel(
                transfers = transferQueue,
                onPause = vm::pauseTransfer,
                onResume = vm::resumeTransfer,
                onCancel = vm::cancelTransfer,
                onClearFinished = vm::clearFinishedTransfers,
            )
        }
    }
}

@Composable
private fun DisconnectedContent(onNavigateToConnection: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Icon(
                Icons.Default.CloudOff,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(48.dp),
            )
            Text(
                text = stringResource(R.string.file_transfer_not_connected),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(onClick = onNavigateToConnection) { Text(stringResource(R.string.button_connect)) }
        }
    }
}

/**
 * Key for the file-list region's AnimatedContent: state kind + view mode. The entry list is
 * deliberately NOT part of the key — list changes animate per row via animateItem instead of
 * cross-fading the whole region (RemEx-598q), and the exiting copy of a kind-change renders an
 * empty list (invisible), so no stale-content artifact is possible.
 */
private data class FileManagerBodyState(
    val kind: Kind,
    val viewMode: FileViewMode,
) {
    enum class Kind { Loading, NoRoots, Empty, Content }
}

@Composable
private fun CenteredMessage(message: String) {
    // Scrollable so pull-to-refresh still fires when the listing is empty.
    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.size(96.dp))
        Text(message, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ContextMenuSheet(
    entry: RemoteFileEntry,
    canRename: Boolean,
    canDelete: Boolean,
    onDismiss: () -> Unit,
    onDownload: () -> Unit,
    onRename: () -> Unit,
    onDelete: () -> Unit,
    onProperties: () -> Unit,
    onPin: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(modifier = Modifier.padding(bottom = 24.dp)) {
            Text(
                text = entry.name,
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(horizontal = 24.dp, vertical = 12.dp),
            )
            HorizontalDivider()
            if (!entry.isDirectory) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.file_transfer_download)) },
                    leadingIcon = { Icon(Icons.Default.Download, null) },
                    onClick = onDownload,
                )
            }
            DropdownMenuItem(
                text = { Text(stringResource(R.string.file_manager_properties)) },
                leadingIcon = { Icon(Icons.Default.Info, null) },
                onClick = onProperties,
            )
            if (canRename) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.file_transfer_rename)) },
                    leadingIcon = { Icon(Icons.Default.DriveFileRenameOutline, null) },
                    onClick = onRename,
                )
            }
            if (canDelete) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.file_transfer_delete), color = MaterialTheme.colorScheme.error) },
                    leadingIcon = { Icon(Icons.Default.Delete, null, tint = MaterialTheme.colorScheme.error) },
                    onClick = onDelete,
                )
            }
            if (entry.isDirectory && entry.name != FileManagerLogic.PARENT_ENTRY) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.file_transfer_pin_folder)) },
                    leadingIcon = { Icon(Icons.Default.PushPin, null) },
                    onClick = onPin,
                )
            }
        }
    }
}
