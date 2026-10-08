package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.net.Uri
import android.util.Size
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DriveFileRenameOutline
import androidx.compose.material.icons.filled.FolderZip
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.VerticalDivider
import androidx.compose.material3.adaptive.ExperimentalMaterial3AdaptiveApi
import androidx.compose.material3.adaptive.currentWindowAdaptiveInfoV2
import androidx.compose.material3.adaptive.layout.AnimatedPane
import androidx.compose.material3.adaptive.layout.ListDetailPaneScaffoldRole
import androidx.compose.material3.adaptive.layout.PaneAdaptedValue
import androidx.compose.material3.adaptive.navigation.NavigableListDetailPaneScaffold
import androidx.compose.material3.adaptive.navigation.rememberListDetailPaneScaffoldNavigator
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.window.core.layout.WindowSizeClass
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.service.FileManageOperations
import com.clindsay94.remex.share.FileOpener
import com.clindsay94.remex.ui.components.FileConflictSheet
import com.clindsay94.remex.ui.components.FileManagerBreadcrumbs
import com.clindsay94.remex.ui.components.FileManagerDestinationSheet
import com.clindsay94.remex.ui.components.FileManagerGridItem
import com.clindsay94.remex.ui.components.FileManagerListItem
import com.clindsay94.remex.ui.components.FileManagerPropertiesSheet
import com.clindsay94.remex.ui.components.FileManagerQueuePanel
import com.clindsay94.remex.ui.components.FileManagerSelectionBar
import com.clindsay94.remex.ui.components.FileManagerTextDialog
import com.clindsay94.remex.ui.components.FileManagerToolbar
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.ToolbarAction
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.files.CrossDevicePicker
import com.clindsay94.remex.ui.files.FileClipboard
import com.clindsay94.remex.ui.files.FilePreviewPane
import com.clindsay94.remex.ui.files.FileSide
import com.clindsay94.remex.ui.files.FileTreeModel
import com.clindsay94.remex.ui.files.FileTreePane
import com.clindsay94.remex.ui.files.PhoneThumbnails
import com.clindsay94.remex.ui.files.PickMode
import com.clindsay94.remex.ui.files.PreviewActions
import com.clindsay94.remex.ui.files.PreviewTarget
import com.clindsay94.remex.ui.files.TreeRootInfo
import com.clindsay94.remex.ui.files.TreeRow
import com.clindsay94.remex.ui.theme.cardInnerPadding
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** A cross-device picker on screen: what it's for and which files it carries. */
private sealed interface AcrossRequest {
    data class SendToPc(val items: List<PreviewTarget>) : AcrossRequest
    data class SaveToPhone(val items: List<PreviewTarget>) : AcrossRequest
    data class VerifyAgainst(val target: PreviewTarget) : AcrossRequest
}

/**
 * The Files screen (file browser redesign, 2026-10-08): a folder tree with "This phone" and "PC", the folder
 * being browsed, and a live preview of the selected file.
 *
 * - Expanded width (tablet, unfolded): the tree is a permanent pane; contents and preview sit side by side.
 * - Narrower: the tree is a drawer opened from the top bar, and a file opens its preview full screen, with
 *   (predictive) back returning to the list.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class, ExperimentalMaterial3AdaptiveApi::class)
@Composable
fun FileTransferScreen(
    onNavigateToConnection: () -> Unit = {},
    vm: FileTransferViewModel = viewModel(),
) {
    // A transfer started here is invisible without this permission (RemEx-pp4cm.8).
    com.clindsay94.remex.ui.components.AskForTransferNotifications()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val needsPairing by RemexClientManager.needsPairing.collectAsStateWithLifecycle()
    val side by vm.side.collectAsStateWithLifecycle()
    val treeRows by vm.tree.rows.collectAsStateWithLifecycle()
    val previewUi by vm.preview.ui.collectAsStateWithLifecycle()
    val phoneState by vm.phoneBrowser.state.collectAsStateWithLifecycle()
    val phoneFolders by vm.phoneFolders.collectAsStateWithLifecycle()
    val phoneNames by vm.phoneFolderNames.collectAsStateWithLifecycle()
    val remoteRoots by vm.remoteRoots.collectAsStateWithLifecycle()
    val volumes by vm.volumes.collectAsStateWithLifecycle()
    val selectedRootId by vm.selectedRootId.collectAsStateWithLifecycle()
    val remotePath by vm.remotePath.collectAsStateWithLifecycle()
    val capabilities by vm.capabilities.collectAsStateWithLifecycle()
    val volumesPending by vm.volumesPending.collectAsStateWithLifecycle()
    val transferQueue by vm.transferQueue.collectAsStateWithLifecycle()
    val conflictPrompt by vm.conflictPrompt.collectAsStateWithLifecycle()

    val adaptive = currentWindowAdaptiveInfoV2()
    val expanded = adaptive.windowSizeClass.isWidthAtLeastBreakpoint(WindowSizeClass.WIDTH_DP_EXPANDED_LOWER_BOUND)
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val navigator = rememberListDetailPaneScaffoldNavigator<String>()
    val detailHidden = navigator.scaffoldValue[ListDetailPaneScaffoldRole.Detail] == PaneAdaptedValue.Hidden
    val listHidden = navigator.scaffoldValue[ListDetailPaneScaffoldRole.List] == PaneAdaptedValue.Hidden

    // A preview nobody can see stops: no live tail, no image still downloading, no fingerprint still running.
    LaunchedEffect(detailHidden) { if (detailHidden) vm.preview.show(null) }

    fun openPreview(target: PreviewTarget) {
        vm.preview.show(target)
        scope.launch { navigator.navigateTo(ListDetailPaneScaffoldRole.Detail, "${target.side}|${target.rootId}|${target.path}") }
    }

    fun closePreview() {
        scope.launch { navigator.navigateBack() }
    }

    // ── Roots as the tree and pickers see them ─────────────────────────────────
    val pcRoots = remember(remoteRoots, volumes) {
        remoteRoots.map { TreeRootInfo(it.rootId, it.displayName, isWritable = it.isWritable) } +
            volumes.map { TreeRootInfo(it.id, it.label, isVolume = true) }
    }
    val phoneRoots = remember(phoneFolders, phoneNames) {
        phoneFolders.map { TreeRootInfo(it.uri, phoneNames[it.uri].orEmpty(), isShared = it.isShared, isBookmark = it.isBookmark) }
    }

    // Read in composition: a callback that calls getString misses a language change (LocalContextGetResourceValueCall).
    val addFolderFailed = stringResource(R.string.files_add_folder_failed)
    val openFailed = stringResource(R.string.files_open_failed)

    // ── Pickers ────────────────────────────────────────────────────────────────
    val addFolderLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            // The grant is what lets the app list the folder after a restart; without it the bookmark is dead.
            val granted = runCatching {
                context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
                )
            }.isSuccess
            if (granted) vm.addPhoneBookmark(uri)
            else Toast.makeText(context, addFolderFailed, Toast.LENGTH_LONG).show()
        }
    }
    var across by remember { mutableStateOf<AcrossRequest?>(null) }

    across?.let { request ->
        val (title, confirm, pickSide, mode) = when (request) {
            is AcrossRequest.SendToPc -> Quad(stringResource(R.string.files_send_to_pc), stringResource(R.string.files_send_here), FileSide.Pc, PickMode.Folder)
            is AcrossRequest.SaveToPhone -> Quad(stringResource(R.string.files_save_to_phone), stringResource(R.string.files_save_here), FileSide.Phone, PickMode.Folder)
            is AcrossRequest.VerifyAgainst -> Quad(
                stringResource(R.string.files_verify_against, stringResource(if (request.target.side == FileSide.Phone) R.string.files_pc else R.string.files_this_phone)),
                "",
                if (request.target.side == FileSide.Phone) FileSide.Pc else FileSide.Phone,
                PickMode.File,
            )
        }
        CrossDevicePicker(
            title = title,
            confirmLabel = confirm,
            side = pickSide,
            mode = mode,
            roots = if (pickSide == FileSide.Pc) pcRoots else phoneRoots,
            start = vm.lastFolderOn(pickSide),
            list = vm::listForPicker,
            onPicked = { rootId, folder, file ->
                across = null
                when (request) {
                    is AcrossRequest.SendToPc -> vm.sendToPc(request.items, rootId, folder)
                    is AcrossRequest.SaveToPhone -> vm.saveToPhone(request.items, rootId, folder)
                    is AcrossRequest.VerifyAgainst -> if (file != null) {
                        val path = if (folder.isEmpty()) file.name else "$folder/${file.name}"
                        vm.preview.verifyAgainst(PreviewTarget(pickSide, rootId, path, file.name, false, file.sizeBytes, file.modifiedUnixMs))
                    }
                }
            },
            onDismiss = { across = null },
        )
    }

    val copiedText = stringResource(R.string.file_manager_sha256_copied)
    val copyLabel = stringResource(R.string.file_manager_copy_sha256)
    val previewActions = PreviewActions(
        onBack = if (listHidden) ::closePreview else null,
        onLive = vm.preview::setLive,
        onComputeHash = vm.preview::computeHash,
        onCompare = vm.preview::compare,
        onVerifyAgainst = { previewUi.target?.let { across = AcrossRequest.VerifyAgainst(it) } },
        onCopyHash = { hex -> if (FileClipboard.copy(context, copyLabel, hex)) Toast.makeText(context, copiedText, Toast.LENGTH_SHORT).show() },
        onOpenWith = previewUi.target?.takeIf { it.side == FileSide.Phone && !it.isDirectory }?.let { t ->
            {
                scope.launch {
                    val uri = runCatching { vm.phoneDocumentUri(t.rootId, t.path) }.getOrNull()
                    if (uri == null || !FileOpener.open(context, uri, t.name)) {
                        Toast.makeText(context, openFailed, Toast.LENGTH_SHORT).show()
                    }
                }
            }
        },
        onSendAcross = previewUi.target?.takeIf { !it.isDirectory }?.let { t ->
            { across = if (t.side == FileSide.Phone) AcrossRequest.SendToPc(listOf(t)) else AcrossRequest.SaveToPhone(listOf(t)) }
        },
        onRetry = { previewUi.target?.let(vm.preview::show) },
    )

    val selectedKey = when (side) {
        FileSide.Phone -> phoneState.rootId?.let { FileTreeModel.folderKey(FileSide.Phone, it, phoneState.path) }
        FileSide.Pc -> selectedRootId?.let { FileTreeModel.folderKey(FileSide.Pc, it, remotePath) }
    }
    val tree: @Composable (Modifier) -> Unit = { modifier ->
        FileTreePane(
            rows = treeRows,
            selectedKey = selectedKey,
            pcConnected = isConnected && !needsPairing,
            canBrowsePcDevice = capabilities?.fullBrowse == true,
            browsePcDevicePending = volumesPending,
            onOpen = { row ->
                if (row.kind == TreeRow.Kind.Device) vm.showSide(row.side)
                else vm.openLocation(row.side, row.rootId!!, row.path)
                scope.launch {
                    drawerState.close()
                    // A full-screen preview would otherwise stay up, now empty, over the folder just picked.
                    if (listHidden) navigator.navigateBack()
                }
            },
            onToggle = vm.tree::toggle,
            onAddPhoneFolder = { addFolderLauncher.launch(null) },
            onSetShared = vm::setPhoneFolderShared,
            onRemoveBookmark = vm::removePhoneBookmark,
            onBrowsePcDevice = vm::loadVolumes,
            modifier = modifier,
        )
    }

    val topBarScrollBehavior = rememberRemexTopBarScrollBehavior()
    val locationSubtitle = when (side) {
        FileSide.Pc -> FileManagerLogic.buildLocationSubtitle(
            rootLabel = pcRoots.firstOrNull { it.rootId == selectedRootId }?.label ?: "/",
            path = remotePath,
            selectedRootIsWritable = remoteRoots.firstOrNull { it.rootId == selectedRootId }?.isWritable,
            readOnlyLabel = stringResource(R.string.file_transfer_read_only_root),
        ).takeIf { selectedRootId != null }
        FileSide.Phone -> phoneState.rootId?.let { root ->
            val label = phoneNames[root].orEmpty()
            if (phoneState.path.isEmpty()) label else "$label/${phoneState.path}"
        }
    }

    val body: @Composable () -> Unit = {
        Scaffold(
            modifier = Modifier.nestedScroll(topBarScrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                    title = stringResource(R.string.screen_file_transfer_title),
                    // Before a folder is open there is no location to name, so the page shows the
                    // same plain subtitle as the PC's Files page, in the display font (RemEx-kq10x.4).
                    // A location is data (folder names in any script), so it keeps the body font.
                    subtitle = locationSubtitle ?: stringResource(R.string.screen_file_transfer_subtitle),
                    subtitleInDisplayFont = locationSubtitle == null,
                    navigationIcon = if (expanded) null else {
                        {
                            IconButton(onClick = { scope.launch { drawerState.open() } }, shapes = rememberRemexIconButtonShapes()) {
                                Icon(Icons.Default.Menu, contentDescription = stringResource(R.string.files_show_folders))
                            }
                        }
                    },
                    scrollBehavior = topBarScrollBehavior,
                )
            },
        ) { innerPadding ->
            Row(Modifier.fillMaxSize().padding(innerPadding)) {
                if (expanded) {
                    tree(Modifier.width(280.dp).fillMaxHeight())
                    VerticalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
                }
                NavigableListDetailPaneScaffold(
                    navigator = navigator,
                    modifier = Modifier.weight(1f),
                    listPane = {
                        AnimatedPane {
                            Column(Modifier.fillMaxSize()) {
                                if (!expanded) SideSwitcher(side, vm::showSide)
                                Box(Modifier.weight(1f)) {
                                    when (side) {
                                        FileSide.Pc -> PcContents(
                                            vm = vm,
                                            isConnected = isConnected,
                                            needsPairing = needsPairing,
                                            onNavigateToConnection = onNavigateToConnection,
                                            onOpenFile = ::openPreview,
                                            onSaveToPhone = { across = AcrossRequest.SaveToPhone(it) },
                                        )
                                        FileSide.Phone -> PhoneContents(
                                            vm = vm,
                                            hasFolders = phoneFolders.isNotEmpty(),
                                            rootLabel = phoneState.rootId?.let { phoneNames[it] }.orEmpty(),
                                            onAddFolder = { addFolderLauncher.launch(null) },
                                            onOpenFile = ::openPreview,
                                            onSendToPc = { across = AcrossRequest.SendToPc(it) },
                                        )
                                    }
                                }
                                FileManagerQueuePanel(
                                    transfers = transferQueue,
                                    onPause = vm::pauseTransfer,
                                    onResume = vm::resumeTransfer,
                                    onCancel = vm::cancelTransfer,
                                    onCancelAll = vm::cancelAllTransfers,
                                    onClearFinished = vm::clearFinishedTransfers,
                                )
                            }
                        }
                    },
                    detailPane = {
                        AnimatedPane {
                            Surface(color = MaterialTheme.colorScheme.surface, modifier = Modifier.fillMaxSize()) {
                                FilePreviewPane(previewUi, previewActions)
                            }
                        }
                    },
                )
            }
        }
        // Raised by the copy/move loop when the host reports a filename collision (RemEx-agpn).
        conflictPrompt?.let { prompt -> FileConflictSheet(prompt = prompt, onResolved = vm::onConflictResolved) }
    }

    if (expanded) {
        body()
    } else {
        ModalNavigationDrawer(
            drawerState = drawerState,
            // Edge swipes stay with the list (and predictive back); the drawer opens from the top-bar button.
            gesturesEnabled = drawerState.isOpen,
            drawerContent = {
                ModalDrawerSheet {
                    Text(
                        stringResource(R.string.files_folders_title),
                        style = MaterialTheme.typography.titleMediumEmphasized,
                        modifier = Modifier.padding(start = 24.dp, top = 20.dp, bottom = 8.dp),
                    )
                    tree(Modifier.fillMaxSize())
                }
            },
            content = body,
        )
    }
}

private data class Quad<A, B, C, D>(val a: A, val b: B, val c: C, val d: D)

/** One-tap switch between the two devices on narrow screens, where the tree is in a drawer. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun SideSwitcher(side: FileSide, onSide: (FileSide) -> Unit) {
    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
        val options = listOf(FileSide.Phone to R.string.files_this_phone, FileSide.Pc to R.string.files_pc)
        options.forEachIndexed { index, (value, label) ->
            SegmentedButton(
                selected = side == value,
                onClick = { onSide(value) },
                shape = SegmentedButtonDefaults.itemShape(index, options.size),
            ) { Text(stringResource(label)) }
        }
    }
}

// ── PC side ───────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PcContents(
    vm: FileTransferViewModel,
    isConnected: Boolean,
    needsPairing: Boolean,
    onNavigateToConnection: () -> Unit,
    onOpenFile: (PreviewTarget) -> Unit,
    onSaveToPhone: (List<PreviewTarget>) -> Unit,
) {
    val remotePath by vm.remotePath.collectAsStateWithLifecycle()
    val displayedEntries by vm.displayedEntries.collectAsStateWithLifecycle()
    val remoteRoots by vm.remoteRoots.collectAsStateWithLifecycle()
    val volumes by vm.volumes.collectAsStateWithLifecycle()
    val supportsFolderTransfer by vm.supportsFolderTransfer.collectAsStateWithLifecycle()
    val selectedRootId by vm.selectedRootId.collectAsStateWithLifecycle()
    val isLoading by vm.isLoading.collectAsStateWithLifecycle()
    val isRefreshing by vm.isRefreshing.collectAsStateWithLifecycle()
    val statusText by vm.statusText.collectAsStateWithLifecycle()
    val isSelectionMode by vm.isSelectionMode.collectAsStateWithLifecycle()
    val selectedEntryNames by vm.selectedEntryNames.collectAsStateWithLifecycle()
    val sortOption by vm.sortOption.collectAsStateWithLifecycle()
    val viewMode by vm.viewMode.collectAsStateWithLifecycle()
    val showHidden by vm.showHidden.collectAsStateWithLifecycle()
    val hiddenItemCount by vm.hiddenItemCount.collectAsStateWithLifecycle()
    val searchQuery by vm.searchQuery.collectAsStateWithLifecycle()
    val searchActive by vm.searchActive.collectAsStateWithLifecycle()
    val searchTruncated by vm.searchTruncated.collectAsStateWithLifecycle()
    val thumbnails by vm.thumbnails.collectAsStateWithLifecycle()
    val properties by vm.properties.collectAsStateWithLifecycle()
    val propertiesLoading by vm.propertiesLoading.collectAsStateWithLifecycle()
    val destinationPath by vm.destinationPath.collectAsStateWithLifecycle()
    val destinationEntries by vm.destinationEntries.collectAsStateWithLifecycle()
    val destinationLoading by vm.destinationLoading.collectAsStateWithLifecycle()
    val isTransferring by vm.isTransferring.collectAsStateWithLifecycle()
    val transferProgress by vm.transferProgress.collectAsStateWithLifecycle()
    val haptics = rememberRemexHaptics()
    // A long-press that starts selecting says so under the finger (phase 6, RemEx-wqo7a.8).
    val startSelecting: (RemoteFileEntry) -> Unit = { entry ->
        haptics.perform(RemexHapticEvent.LongPress)
        vm.enterSelectionMode(entry)
    }

    val selectedRoot = remoteRoots.firstOrNull { it.rootId == selectedRootId }
    val selectedVolume = volumes.firstOrNull { it.id == selectedRootId }
    val rootLabel = selectedRoot?.displayName ?: selectedVolume?.label ?: "/"
    val canWrite = selectedRoot?.isWritable ?: (selectedVolume != null)
    val canDelete = selectedRoot?.canDelete ?: (selectedVolume != null)
    val canRename = selectedRoot?.canRename ?: (selectedVolume != null)
    val padding = cardInnerPadding()

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
    fun startDownload(entry: RemoteFileEntry) {
        pendingDownloadEntry = entry
        createDocumentLauncher.launch(entry.name)
    }
    // A folder needs a TREE grant, not a single created document: the whole subtree is written under
    // it, so the picker has to hand back somewhere this app may create files and folders (RemEx-q3twg).
    val uploadTreeLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) vm.uploadFolderFromTree(uri)
    }
    var pendingFolderEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    val openTreeLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        val entry = pendingFolderEntry
        pendingFolderEntry = null
        if (uri != null && entry != null) vm.downloadFolderTo(entry, uri)
    }
    fun startFolderDownload(entry: RemoteFileEntry) {
        pendingFolderEntry = entry
        openTreeLauncher.launch(null)
    }

    // ── Dialogs / sheets ────────────────────────────────────────────────────────
    var renameTarget by remember { mutableStateOf<RemoteFileEntry?>(null) }
    var showNewFolder by remember { mutableStateOf(false) }
    var contextMenuEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    var destinationMode by remember { mutableStateOf<String?>(null) } // FileManageOperations.COPY / MOVE

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
        PcContextMenuSheet(
            entry = entry,
            canRename = canRename,
            canDelete = canDelete,
            supportsFolderTransfer = supportsFolderTransfer,
            onDismiss = { contextMenuEntry = null },
            onPreview = { contextMenuEntry = null; vm.previewTargetFor(FileSide.Pc, entry)?.let(onOpenFile) },
            onSaveToPhone = { contextMenuEntry = null; vm.previewTargetFor(FileSide.Pc, entry)?.let { onSaveToPhone(listOf(it)) } },
            onDownload = { contextMenuEntry = null; startDownload(entry) },
            onDownloadFolder = { contextMenuEntry = null; startFolderDownload(entry) },
            onRename = { contextMenuEntry = null; renameTarget = entry },
            onDelete = { contextMenuEntry = null; vm.deleteEntry(entry) },
            onProperties = { contextMenuEntry = null; vm.showProperties(entry) },
            onPin = { contextMenuEntry = null; vm.navigateInto(entry); vm.pinCurrentFolder() },
        )
    }

    // Connected/disconnected cross-fades instead of hard-swapping via an early return (RemEx-xgc7).
    val connectionFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    // A PC that no longer recognises this phone refuses the roots request, so Files says so
    // and offers Pair instead of spinning (sweep P3).
    val gate = when {
        !isConnected -> FilesGate.Disconnected
        needsPairing -> FilesGate.NeedsPairing
        else -> FilesGate.Ready
    }
    AnimatedContent(
        targetState = gate,
        transitionSpec = { fadeIn(connectionFadeSpec) togetherWith fadeOut(connectionFadeSpec) },
        modifier = Modifier.fillMaxSize().padding(horizontal = padding),
        label = "file_manager_connection",
    ) { shown ->
        when (shown) {
            FilesGate.Disconnected -> DisconnectedContent(onNavigateToConnection)
            FilesGate.NeedsPairing -> Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                NeedsPairingContent(
                    onPair = {
                        ConnectionOpenRequests.requestAddPc()
                        onNavigateToConnection()
                    },
                )
            }
            FilesGate.Ready -> Column(modifier = Modifier.fillMaxSize()) {
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
                    onUploadFolder = { uploadTreeLauncher.launch(null) },
                    modifier = Modifier.padding(vertical = 4.dp),
                    showHidden = showHidden,
                    onShowHiddenChange = vm::setShowHidden,
                )

                AnimatedVisibility(
                    visible = !searchActive && selectedRootId != null,
                    enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                    exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                ) {
                    FileManagerBreadcrumbs(
                        crumbs = FileManagerLogic.buildBreadcrumbs(rootLabel, remotePath),
                        onNavigate = vm::navigateToPath,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                    )
                }

                AnimatedVisibility(
                    visible = isSelectionMode,
                    enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                    exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                ) {
                    val saveLabel = stringResource(R.string.files_save_to_phone)
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
                        acrossAction = ToolbarAction(Icons.Default.PhoneAndroid, saveLabel) {
                            val items = displayedEntries.filter { it.name in selectedEntryNames }
                                .mapNotNull { vm.previewTargetFor(FileSide.Pc, it) }
                            vm.clearSelection()
                            onSaveToPhone(items)
                        },
                    )
                }

                // The stacked header notices grow/shrink instead of shoving the list in one frame (RemEx-z01v).
                AnimatedVisibility(
                    visible = searchActive && searchTruncated,
                    enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                    exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                ) {
                    Text(
                        text = stringResource(R.string.file_manager_search_truncated),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 2.dp),
                    )
                }
                StatusLine(statusText)

                AnimatedVisibility(
                    visible = isTransferring,
                    enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                    exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                ) {
                    Column {
                        RemexLinearWavyProgress(progress = transferProgress, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp))
                        OutlinedButton(onClick = vm::cancelLegacyTransfer, modifier = Modifier.fillMaxWidth(), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                            Text(stringResource(R.string.file_transfer_cancel))
                        }
                    }
                }

                Box(modifier = Modifier.weight(1f)) {
                    PullToRefreshBox(isRefreshing = isRefreshing, onRefresh = vm::refresh, modifier = Modifier.fillMaxSize()) {
                        // The body states cross-fade through AnimatedContent, keyed on kind + view mode. Entry-list
                        // changes deliberately do NOT re-key the region: rows animate individually via animateItem
                        // (RemEx-598q reconciling RemEx-h9rd).
                        val bodyState = when {
                            isLoading && displayedEntries.isEmpty() -> FileManagerBodyState(FileManagerBodyState.Kind.Loading, viewMode)
                            remoteRoots.isEmpty() && volumes.isEmpty() -> FileManagerBodyState(FileManagerBodyState.Kind.NoRoots, viewMode)
                            selectedRootId == null -> FileManagerBodyState(FileManagerBodyState.Kind.PickFolder, viewMode)
                            displayedEntries.isEmpty() -> FileManagerBodyState(FileManagerBodyState.Kind.Empty, viewMode)
                            else -> FileManagerBodyState(FileManagerBodyState.Kind.Content, viewMode)
                        }
                        val thumbFor: (RemoteFileEntry) -> String? = { thumbnails[it.relativePath ?: FileManagerLogic.combinePath(remotePath, it.name)] }
                        val onTap: (RemoteFileEntry) -> Unit = { entry ->
                            when {
                                isSelectionMode -> vm.toggleEntrySelection(entry)
                                entry.isDirectory -> vm.navigateInto(entry)
                                // A file opens its preview: in the old screen a tap in list view did nothing.
                                else -> vm.previewTargetFor(FileSide.Pc, entry)?.let(onOpenFile)
                            }
                        }
                        EntriesBody(
                            state = bodyState,
                            entries = displayedEntries,
                            emptyText = when {
                                hiddenItemCount > 0 -> stringResource(R.string.file_manager_only_hidden_items)
                                searchActive -> stringResource(R.string.file_manager_no_results)
                                else -> stringResource(R.string.file_transfer_empty)
                            },
                            noRootsText = stringResource(R.string.file_transfer_no_shared_folders),
                            pickFolderText = stringResource(R.string.files_pick_pc_folder),
                            isSelectionMode = isSelectionMode,
                            isSelected = { it.name in selectedEntryNames },
                            thumbnailBase64 = thumbFor,
                            thumbnail = { null },
                            onRequestThumbnail = vm::requestThumbnail,
                            onTap = onTap,
                            onLongPress = startSelecting,
                            onOverflow = { contextMenuEntry = it },
                            onDownload = ::startDownload,
                        )
                    }
                }
            }
        }
    }
}

/** What the Files body shows: the browser itself, or why it can't be shown yet. */
private enum class FilesGate { Disconnected, NeedsPairing, Ready }

// ── Phone side ────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PhoneContents(
    vm: FileTransferViewModel,
    hasFolders: Boolean,
    rootLabel: String,
    onAddFolder: () -> Unit,
    onOpenFile: (PreviewTarget) -> Unit,
    onSendToPc: (List<PreviewTarget>) -> Unit,
) {
    val state by vm.phoneBrowser.state.collectAsStateWithLifecycle()
    val sortOption by vm.sortOption.collectAsStateWithLifecycle()
    val viewMode by vm.viewMode.collectAsStateWithLifecycle()
    val showHidden by vm.showHidden.collectAsStateWithLifecycle()
    val statusText by vm.statusText.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val haptics = rememberRemexHaptics()
    val padding = cardInnerPadding()
    val openFailed = stringResource(R.string.files_open_failed)

    var query by remember { mutableStateOf("") }
    var selection by remember(state.rootId, state.path) { mutableStateOf(emptySet<String>()) }
    var menuEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    val visible = remember(state.entries, sortOption, showHidden, query) {
        FileManagerLogic.sortEntries(FileManagerLogic.visibleEntries(state.entries, showHidden), sortOption)
            .filter { query.isBlank() || it.name.contains(query, ignoreCase = true) }
    }
    fun targetOf(entry: RemoteFileEntry) = vm.previewTargetFor(FileSide.Phone, entry)

    menuEntry?.let { entry ->
        PhoneContextMenuSheet(
            entry = entry,
            onDismiss = { menuEntry = null },
            onPreview = { menuEntry = null; targetOf(entry)?.let(onOpenFile) },
            onSendToPc = { menuEntry = null; targetOf(entry)?.let { onSendToPc(listOf(it)) } },
            onOpenWith = {
                menuEntry = null
                val target = targetOf(entry) ?: return@PhoneContextMenuSheet
                scope.launch {
                    val uri = runCatching { vm.phoneDocumentUri(target.rootId, target.path) }.getOrNull()
                    if (uri == null || !FileOpener.open(context, uri, target.name)) {
                        Toast.makeText(context, openFailed, Toast.LENGTH_SHORT).show()
                    }
                }
            },
        )
    }

    if (!hasFolders) {
        NoPhoneFolders(onAddFolder)
        return
    }
    Column(Modifier.fillMaxSize().padding(horizontal = padding)) {
        FileManagerToolbar(
            searchQuery = query,
            onSearchChange = { query = it },
            sortOption = sortOption,
            onSort = vm::setSort,
            viewMode = viewMode,
            onToggleViewMode = vm::toggleViewMode,
            canWrite = false,
            onNewFolder = {},
            onUpload = {},
            onUploadFolder = {},
            modifier = Modifier.padding(vertical = 4.dp),
            showHidden = showHidden,
            onShowHiddenChange = vm::setShowHidden,
            primaryAction = ToolbarAction(Icons.Default.CreateNewFolder, stringResource(R.string.files_add_folder), onAddFolder),
        )
        if (state.rootId != null) {
            FileManagerBreadcrumbs(
                crumbs = FileManagerLogic.buildBreadcrumbs(rootLabel.ifBlank { "/" }, if (state.path.isEmpty()) "/" else state.path),
                onNavigate = { path -> vm.openLocation(FileSide.Phone, state.rootId!!, FileTreeModel.normalize(path)) },
                modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
            )
        }
        AnimatedVisibility(
            visible = selection.isNotEmpty(),
            enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
            exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
        ) {
            val sendLabel = stringResource(R.string.files_send_to_pc)
            FileManagerSelectionBar(
                selectedCount = selection.size,
                canWrite = false,
                canDelete = false,
                onClose = { selection = emptySet() },
                onSelectAll = { selection = visible.filter { !it.isDirectory }.map { it.name }.toSet() },
                onCopy = {},
                onMove = {},
                onDelete = {},
                modifier = Modifier.padding(vertical = 4.dp),
                acrossAction = ToolbarAction(Icons.Default.SwapHoriz, sendLabel) {
                    val items = visible.filter { it.name in selection }.mapNotNull(::targetOf)
                    selection = emptySet()
                    onSendToPc(items)
                },
            )
        }
        StatusLine(statusText)
        Box(Modifier.weight(1f)) {
            PullToRefreshBox(isRefreshing = state.loading && state.entries.isNotEmpty(), onRefresh = vm.phoneBrowser::refresh, modifier = Modifier.fillMaxSize()) {
                val bodyState = when {
                    state.rootId == null -> FileManagerBodyState(FileManagerBodyState.Kind.PickFolder, viewMode)
                    state.loading && state.entries.isEmpty() -> FileManagerBodyState(FileManagerBodyState.Kind.Loading, viewMode)
                    state.failed -> FileManagerBodyState(FileManagerBodyState.Kind.Failed, viewMode)
                    visible.isEmpty() -> FileManagerBodyState(FileManagerBodyState.Kind.Empty, viewMode)
                    else -> FileManagerBodyState(FileManagerBodyState.Kind.Content, viewMode)
                }
                EntriesBody(
                    state = bodyState,
                    entries = visible,
                    emptyText = stringResource(if (query.isNotBlank()) R.string.file_manager_no_results else R.string.file_transfer_empty),
                    noRootsText = "",
                    pickFolderText = stringResource(R.string.files_pick_phone_folder),
                    failedText = stringResource(R.string.files_phone_folder_failed),
                    isSelectionMode = selection.isNotEmpty(),
                    isSelected = { it.name in selection },
                    thumbnailBase64 = { null },
                    thumbnail = { entry ->
                        val root = state.rootId
                        if (root == null || entry.isDirectory || !FileManagerLogic.isThumbnailCandidate(entry.name)) null
                        else rememberPhoneThumbnail(vm, root, FileManagerLogic.combinePath(state.path, entry.name), entry.modifiedUnixMs)
                    },
                    onRequestThumbnail = {},
                    onTap = { entry ->
                        when {
                            selection.isNotEmpty() -> if (!entry.isDirectory) selection = FileManagerLogic.toggleSelection(selection, entry.name)
                            entry.isDirectory -> vm.openLocation(FileSide.Phone, state.rootId!!, FileManagerLogic.combinePath(state.path, entry.name))
                            else -> targetOf(entry)?.let(onOpenFile)
                        }
                    },
                    onLongPress = { entry ->
                        if (!entry.isDirectory) {
                            haptics.perform(RemexHapticEvent.LongPress)
                            selection = selection + entry.name
                        }
                    },
                    onOverflow = { menuEntry = it },
                    onDownload = null,
                )
            }
        }
    }
}

/** A phone image's thumbnail from the system's thumbnail cache, decoded off the main thread and kept across scrolls. */
@Composable
private fun rememberPhoneThumbnail(vm: FileTransferViewModel, rootId: String, path: String, modifiedMs: Long): ImageBitmap? {
    val context = LocalContext.current
    // The modified time is part of the key, so a photo replaced under the same name gets a new thumbnail.
    val key = "$rootId|$path|$modifiedMs"
    PhoneThumbnails.get(key)?.let { return it }
    val loaded by produceState<ImageBitmap?>(null, key) {
        value = withContext(Dispatchers.IO) {
            try {
                val uri = Uri.parse(vm.phoneDocumentUri(rootId, path))
                context.contentResolver.loadThumbnail(uri, Size(PhoneThumbnails.SIZE_PX, PhoneThumbnails.SIZE_PX), null).asImageBitmap()
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (_: Exception) {
                null // not an image the system can thumbnail, or gone: the row keeps its icon
            }
        }?.also { PhoneThumbnails.put(key, it) }
    }
    return loaded
}

@Composable
private fun NoPhoneFolders(onAddFolder: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
    ) {
        Icon(Icons.Default.PhoneAndroid, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(48.dp))
        Text(stringResource(R.string.files_no_phone_folders), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Button(onClick = onAddFolder, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
            Text(stringResource(R.string.files_add_folder))
        }
    }
}

// ── Shared pieces ─────────────────────────────────────────────────────────────

@Composable
private fun StatusLine(statusText: String) {
    // Remembers its last non-blank value so the shrink-out exit still has text.
    var lastStatusText by remember { mutableStateOf(statusText) }
    if (statusText.isNotBlank()) lastStatusText = statusText
    AnimatedVisibility(
        visible = statusText.isNotBlank(),
        enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
        exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
    ) {
        Text(
            text = lastStatusText,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(vertical = 2.dp),
        )
    }
}

/** The list or grid of one folder, on either device, with its loading, empty and error states. */
@Composable
private fun EntriesBody(
    state: FileManagerBodyState,
    entries: List<RemoteFileEntry>,
    emptyText: String,
    noRootsText: String,
    pickFolderText: String,
    isSelectionMode: Boolean,
    isSelected: (RemoteFileEntry) -> Boolean,
    thumbnailBase64: (RemoteFileEntry) -> String?,
    thumbnail: @Composable (RemoteFileEntry) -> ImageBitmap?,
    onRequestThumbnail: (RemoteFileEntry) -> Unit,
    onTap: (RemoteFileEntry) -> Unit,
    onLongPress: (RemoteFileEntry) -> Unit,
    onOverflow: (RemoteFileEntry) -> Unit,
    onDownload: ((RemoteFileEntry) -> Unit)?,
    failedText: String = "",
) {
    val effectsSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    val itemPlacementSpec = MaterialTheme.motionScheme.fastSpatialSpec<IntOffset>()
    val fileGridState = androidx.compose.foundation.lazy.grid.rememberLazyGridState()
    val fileListState = androidx.compose.foundation.lazy.rememberLazyListState()
    // Tapping Files while on Files goes back to the top of whichever view is showing (RemEx-pp4cm.3).
    com.clindsay94.remex.ui.navigation.TabReselectEffect(com.clindsay94.remex.ui.navigation.Screen.FileTransfer) {
        if (state.viewMode == FileViewMode.GRID) fileGridState.animateScrollToItem(0)
        else fileListState.animateScrollToItem(0)
    }
    AnimatedContent(
        targetState = state,
        transitionSpec = { fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec) },
        modifier = Modifier.fillMaxSize(),
        label = "file_body",
    ) { shown ->
        when (shown.kind) {
            FileManagerBodyState.Kind.Loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                RemexLoadingIndicator(contained = true)
            }
            FileManagerBodyState.Kind.NoRoots -> CenteredMessage(noRootsText)
            FileManagerBodyState.Kind.PickFolder -> CenteredMessage(pickFolderText)
            FileManagerBodyState.Kind.Failed -> CenteredMessage(failedText)
            FileManagerBodyState.Kind.Empty -> CenteredMessage(emptyText)
            FileManagerBodyState.Kind.Content -> if (shown.viewMode == FileViewMode.GRID) {
                LazyVerticalGrid(columns = GridCells.Adaptive(minSize = 100.dp), state = fileGridState, modifier = Modifier.fillMaxSize()) {
                    items(entries, key = ::rowKey) { entry ->
                        FileManagerGridItem(
                            modifier = Modifier.animateItem(fadeInSpec = effectsSpec, placementSpec = itemPlacementSpec, fadeOutSpec = effectsSpec),
                            entry = entry,
                            isSelectionMode = isSelectionMode,
                            isSelected = isSelected(entry),
                            thumbnailBase64 = thumbnailBase64(entry),
                            showOverflow = entry.name != FileManagerLogic.PARENT_ENTRY,
                            onRequestThumbnail = { onRequestThumbnail(entry) },
                            onTap = { onTap(entry) },
                            onLongPress = { onLongPress(entry) },
                            onOverflow = { onOverflow(entry) },
                            thumbnail = thumbnail(entry),
                        )
                    }
                }
            } else {
                LazyColumn(state = fileListState, modifier = Modifier.fillMaxSize()) {
                    items(entries, key = ::rowKey) { entry ->
                        FileManagerListItem(
                            modifier = Modifier.animateItem(fadeInSpec = effectsSpec, placementSpec = itemPlacementSpec, fadeOutSpec = effectsSpec),
                            entry = entry,
                            isSelectionMode = isSelectionMode,
                            isSelected = isSelected(entry),
                            thumbnailBase64 = thumbnailBase64(entry),
                            showDownload = onDownload != null,
                            showOverflow = entry.name != FileManagerLogic.PARENT_ENTRY,
                            onRequestThumbnail = { onRequestThumbnail(entry) },
                            onTap = { onTap(entry) },
                            onLongPress = { onLongPress(entry) },
                            onDownload = { onDownload?.invoke(entry) },
                            onOverflow = { onOverflow(entry) },
                            thumbnail = thumbnail(entry),
                        )
                        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
                    }
                }
            }
        }
    }
}

/**
 * A row's list key. Phone rows carry their document id: a provider may list two files with one name, and two
 * rows sharing a key crash the list.
 */
private fun rowKey(entry: RemoteFileEntry): String = entry.id ?: entry.relativePath ?: entry.name

@Composable
private fun DisconnectedContent(onNavigateToConnection: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Icon(Icons.Default.CloudOff, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(48.dp))
            Text(
                text = stringResource(R.string.file_transfer_not_connected),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(onClick = onNavigateToConnection, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                Text(stringResource(R.string.button_connect))
            }
        }
    }
}

/**
 * Key for the file-list region's AnimatedContent: state kind + view mode. The entry list is deliberately NOT part
 * of the key — list changes animate per row via animateItem instead of cross-fading the whole region (RemEx-598q).
 */
private data class FileManagerBodyState(val kind: Kind, val viewMode: FileViewMode) {
    enum class Kind { Loading, NoRoots, PickFolder, Failed, Empty, Content }
}

@Composable
private fun CenteredMessage(message: String) {
    // Scrollable so pull-to-refresh still fires when the listing is empty.
    Column(modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()), horizontalAlignment = Alignment.CenterHorizontally) {
        Spacer(Modifier.size(96.dp))
        Text(message, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 24.dp))
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PcContextMenuSheet(
    entry: RemoteFileEntry,
    canRename: Boolean,
    canDelete: Boolean,
    supportsFolderTransfer: Boolean,
    onDismiss: () -> Unit,
    onPreview: () -> Unit,
    onSaveToPhone: () -> Unit,
    onDownload: () -> Unit,
    onDownloadFolder: () -> Unit,
    onRename: () -> Unit,
    onDelete: () -> Unit,
    onProperties: () -> Unit,
    onPin: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(modifier = Modifier.padding(bottom = 24.dp)) {
            SheetTitle(entry.name)
            if (!entry.isDirectory) {
                DropdownMenuItem(text = { Text(stringResource(R.string.files_preview_title)) }, leadingIcon = { Icon(Icons.Default.Visibility, null) }, onClick = onPreview)
                DropdownMenuItem(text = { Text(stringResource(R.string.files_save_to_phone)) }, leadingIcon = { Icon(Icons.Default.PhoneAndroid, null) }, onClick = onSaveToPhone)
                DropdownMenuItem(text = { Text(stringResource(R.string.files_download_to)) }, leadingIcon = { Icon(Icons.Default.Download, null) }, onClick = onDownload)
            }
            if (entry.isDirectory && entry.name != FileManagerLogic.PARENT_ENTRY && supportsFolderTransfer) {
                DropdownMenuItem(text = { Text(stringResource(R.string.file_manager_download_folder)) }, leadingIcon = { Icon(Icons.Default.FolderZip, null) }, onClick = onDownloadFolder)
            }
            DropdownMenuItem(text = { Text(stringResource(R.string.file_manager_properties)) }, leadingIcon = { Icon(Icons.Default.Info, null) }, onClick = onProperties)
            if (canRename) {
                DropdownMenuItem(text = { Text(stringResource(R.string.file_transfer_rename)) }, leadingIcon = { Icon(Icons.Default.DriveFileRenameOutline, null) }, onClick = onRename)
            }
            if (canDelete) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.file_transfer_delete), color = MaterialTheme.colorScheme.error) },
                    leadingIcon = { Icon(Icons.Default.Delete, null, tint = MaterialTheme.colorScheme.error) },
                    onClick = onDelete,
                )
            }
            if (entry.isDirectory && entry.name != FileManagerLogic.PARENT_ENTRY) {
                DropdownMenuItem(text = { Text(stringResource(R.string.file_transfer_pin_folder)) }, leadingIcon = { Icon(Icons.Default.PushPin, null) }, onClick = onPin)
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PhoneContextMenuSheet(
    entry: RemoteFileEntry,
    onDismiss: () -> Unit,
    onPreview: () -> Unit,
    onSendToPc: () -> Unit,
    onOpenWith: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(modifier = Modifier.padding(bottom = 24.dp)) {
            SheetTitle(entry.name)
            if (!entry.isDirectory) {
                DropdownMenuItem(text = { Text(stringResource(R.string.files_preview_title)) }, leadingIcon = { Icon(Icons.Default.Visibility, null) }, onClick = onPreview)
                DropdownMenuItem(text = { Text(stringResource(R.string.files_send_to_pc)) }, leadingIcon = { Icon(Icons.Default.SwapHoriz, null) }, onClick = onSendToPc)
                DropdownMenuItem(text = { Text(stringResource(R.string.files_open_with)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.OpenInNew, null) }, onClick = onOpenWith)
            } else {
                Text(
                    stringResource(R.string.files_folders_not_sent),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 24.dp, vertical = 12.dp),
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun SheetTitle(name: String) {
    Text(
        text = name,
        style = MaterialTheme.typography.titleMediumEmphasized,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.padding(horizontal = 24.dp, vertical = 12.dp),
    )
    HorizontalDivider()
}
