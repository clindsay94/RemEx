package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.net.Uri
import android.os.Parcelable
import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Help
import androidx.compose.material.icons.automirrored.filled.Input
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.*
import androidx.compose.material3.adaptive.ExperimentalMaterial3AdaptiveApi
import androidx.compose.material3.adaptive.layout.AnimatedPane
import androidx.compose.material3.adaptive.layout.ListDetailPaneScaffoldRole
import androidx.compose.material3.adaptive.navigation.NavigableListDetailPaneScaffold
import androidx.compose.material3.adaptive.navigation.rememberListDetailPaneScaffoldNavigator
import androidx.compose.runtime.Composable
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.FilePeerIdentity
import com.clindsay94.remex.ui.components.RemexScreenHeader
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.parcelize.Parcelize

@Parcelize
enum class SettingsCategory : Parcelable {
    CONNECTION,
    PERSONALIZATION,
    INPUT,
    FILE_TRANSFER,
    HELP;

    val titleRes: Int
        get() = when (this) {
            CONNECTION -> R.string.settings_tab_connection
            PERSONALIZATION -> R.string.settings_tab_personalization
            INPUT -> R.string.settings_tab_input
            FILE_TRANSFER -> R.string.settings_tab_file_transfer
            HELP -> R.string.settings_tab_help
        }

    val icon: ImageVector
        get() = when (this) {
            CONNECTION -> Icons.Default.Wifi
            PERSONALIZATION -> Icons.Default.Palette
            INPUT -> Icons.AutoMirrored.Filled.Input
            FILE_TRANSFER -> Icons.Default.FolderOpen
            HELP -> Icons.AutoMirrored.Filled.Help
        }
}

/**
 * Unified Settings screen using NavigableListDetailPaneScaffold for adaptive multi-pane layout.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3AdaptiveApi::class)
@Composable
fun SettingsScreen(
    onReplayTutorial: (() -> Unit)? = null,
    onNavigateToAbout: (() -> Unit)? = null,
    onNavigateToQrScanner: (() -> Unit)? = null
) {
    val navigator = rememberListDetailPaneScaffoldNavigator<SettingsCategory>()
    val scope = rememberCoroutineScope()

    NavigableListDetailPaneScaffold(
        navigator = navigator,
        listPane = {
            AnimatedPane {
                SettingsCategoryList(
                    selectedCategory = navigator.currentDestination?.contentKey,
                    onCategoryClick = { category ->
                        scope.launch {
                            navigator.navigateTo(ListDetailPaneScaffoldRole.Detail, category)
                        }
                    }
                )
            }
        },
        detailPane = {
            AnimatedPane {
                val category = navigator.currentDestination?.contentKey ?: SettingsCategory.CONNECTION
                SettingsDetailContent(
                    category = category,
                    onReplayTutorial = onReplayTutorial,
                    onNavigateToAbout = onNavigateToAbout,
                    onNavigateToQrScanner = onNavigateToQrScanner
                )
            }
        }
    )
}

@Preview(showBackground = true)
@Composable
private fun SettingsScreenPreview() {
    RemExTheme {
        SettingsScreen()
    }
}

@Preview(showBackground = true)
@Composable
private fun SettingsCategoryListPreview() {
    RemExTheme {
        SettingsCategoryList(
            selectedCategory = SettingsCategory.CONNECTION,
            onCategoryClick = {}
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun InputTabPreview() {
    RemExTheme {
        InputTab()
    }
}

@Preview(showBackground = true)
@Composable
private fun FileTransferSettingsTabPreview() {
    RemExTheme {
        FileTransferSettingsTab()
    }
}

@Preview(showBackground = true)
@Composable
private fun HelpTabPreview() {
    RemExTheme {
        HelpTab(onReplayTutorial = {}, onNavigateToAbout = {})
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun SettingsCategoryList(
    selectedCategory: SettingsCategory?,
    onCategoryClick: (SettingsCategory) -> Unit
) {
    Scaffold(
        topBar = {
            RemexScreenHeader(title = stringResource(R.string.screen_settings_title))
        }
    ) { innerPadding ->
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding),
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            items(SettingsCategory.entries, key = { it.name }) { category ->
                ExpressiveSettingsRow(
                    category = category,
                    selected = category == selectedCategory,
                    onClick = { onCategoryClick(category) },
                    modifier = Modifier.animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec())
                )
            }
        }
    }
}

/**
 * Expressive settings row: a tonal icon badge, animated selection color (effects spring) and a
 * spring-physics press-scale (spatial spring) — M3 Expressive list styling.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ExpressiveSettingsRow(
    category: SettingsCategory,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val view = LocalView.current
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.97f else 1f,
        animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
        label = "settingsRowScale"
    )
    val containerColor by animateColorAsState(
        targetValue =
            if (selected) MaterialTheme.colorScheme.secondaryContainer
            else MaterialTheme.colorScheme.surfaceContainerLow,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "settingsRowColor"
    )
    val badgeColor by animateColorAsState(
        targetValue =
            if (selected) MaterialTheme.colorScheme.primary
            else MaterialTheme.colorScheme.primaryContainer,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "settingsRowBadgeColor"
    )
    val onBadgeColor by animateColorAsState(
        targetValue =
            if (selected) MaterialTheme.colorScheme.onPrimary
            else MaterialTheme.colorScheme.onPrimaryContainer,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "settingsRowOnBadgeColor"
    )

    Surface(
        onClick = {
            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
            onClick()
        },
        interactionSource = interaction,
        shape = MaterialTheme.shapes.large,
        color = containerColor,
        modifier = modifier
            .fillMaxWidth()
            .graphicsLayer { scaleX = scale; scaleY = scale }
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(RoundedCornerShape(percent = 30))
                    .background(badgeColor),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    category.icon,
                    contentDescription = null,
                    tint = onBadgeColor,
                    modifier = Modifier.size(24.dp)
                )
            }
            Text(
                text = stringResource(category.titleRes),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.weight(1f)
            )
            Icon(
                Icons.Default.ChevronRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun SettingsDetailContent(
    category: SettingsCategory,
    onReplayTutorial: (() -> Unit)?,
    onNavigateToAbout: (() -> Unit)?,
    onNavigateToQrScanner: (() -> Unit)?
) {
    // Each tab provides its own layout/scrolling
    when (category) {
        SettingsCategory.CONNECTION -> ConnectionScreen(
            onNavigateToQrScanner = onNavigateToQrScanner ?: {}
        )
        SettingsCategory.PERSONALIZATION -> PersonalizationScreen(showHeader = true)
        SettingsCategory.INPUT -> InputTab()
        SettingsCategory.FILE_TRANSFER -> FileTransferSettingsTab()
        SettingsCategory.HELP -> HelpTab(
            onReplayTutorial = onReplayTutorial,
            onNavigateToAbout = onNavigateToAbout
        )
    }
}

@Composable
private fun InputTab() {
    val view = LocalView.current
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    val preferences by
            settingsManager.remoteDesktopPreferencesFlow.collectAsStateWithLifecycle(
                    initialValue = SettingsManager.RemoteDesktopPreferences()
            )

    Scaffold(
        topBar = {
            RemexScreenHeader(title = stringResource(R.string.settings_tab_input))
        }
    ) { innerPadding ->
        Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding)
                    .padding(16.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(20.dp)
        ) {
            Card(
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                            )
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                ) {
                    // Pointer Speed
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                                text =
                                        String.format(
                                                stringResource(
                                                        R.string.remote_desktop_pointer_speed_label
                                                ),
                                                preferences.pointerSpeed
                                        ),
                                style = MaterialTheme.typography.labelLarge,
                                fontWeight = FontWeight.SemiBold
                        )
                        Slider(
                                value = preferences.pointerSpeed,
                                onValueChange = {
                                    scope.launch { settingsManager.saveRemoteDesktopPointerSpeed(it) }
                                },
                                onValueChangeFinished = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                },
                                valueRange = 0.25f..3.0f,
                                steps = 10
                        )
                    }

                    HorizontalDivider(modifier = Modifier.padding(vertical = 4.dp))

                    // Vertical Scroll Sensitivity
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                                text =
                                        String.format(
                                                stringResource(
                                                        R.string
                                                                .remote_desktop_v_scroll_sensitivity_label
                                                ),
                                                preferences.verticalScrollSensitivity
                                        ),
                                style = MaterialTheme.typography.labelLarge,
                                fontWeight = FontWeight.SemiBold
                        )
                        Slider(
                                value = preferences.verticalScrollSensitivity,
                                onValueChange = {
                                    scope.launch {
                                        settingsManager.saveRemoteDesktopScrollSensitivity(
                                                it,
                                                preferences.horizontalScrollSensitivity
                                        )
                                    }
                                },
                                onValueChangeFinished = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                },
                                valueRange = 0.1f..5.0f,
                                steps = 20
                        )
                    }

                    // Horizontal Scroll Sensitivity
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                                text =
                                        String.format(
                                                stringResource(
                                                        R.string
                                                                .remote_desktop_h_scroll_sensitivity_label
                                                ),
                                                preferences.horizontalScrollSensitivity
                                        ),
                                style = MaterialTheme.typography.labelLarge,
                                fontWeight = FontWeight.SemiBold
                        )
                        Slider(
                                value = preferences.horizontalScrollSensitivity,
                                onValueChange = {
                                    scope.launch {
                                        settingsManager.saveRemoteDesktopScrollSensitivity(
                                                preferences.verticalScrollSensitivity,
                                                it
                                        )
                                    }
                                },
                                onValueChangeFinished = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                },
                                valueRange = 0.1f..5.0f,
                                steps = 20
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun FileTransferSettingsTab() {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    val sharedFolderUris by settingsManager.sharedFolderUrisFlow.collectAsStateWithLifecycle(initialValue = emptySet())

    val folderPickerLauncher = rememberLauncherForActivityResult(
            ActivityResultContracts.OpenDocumentTree()
    ) { uri: Uri? ->
        if (uri != null) {
            context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            )
            scope.launch { settingsManager.addSharedFolderUri(uri.toString()) }
        }
    }

    Scaffold(
            topBar = {
                RemexScreenHeader(title = stringResource(R.string.settings_tab_file_transfer))
            }
    ) { innerPadding ->
        Column(
                modifier = Modifier
                        .fillMaxSize()
                        .padding(innerPadding)
                        .padding(16.dp)
                        .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Text(
                    text = stringResource(R.string.settings_ft_shared_folders_title),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = MaterialTheme.colorScheme.primary
            )

            Card(
                    colors = CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                    )
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    Text(
                            text = stringResource(R.string.settings_ft_shared_folders_desc),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

                    Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(
                                Icons.Default.Info,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.tertiary,
                                modifier = Modifier.size(16.dp)
                        )
                        Text(
                                text = stringResource(R.string.settings_ft_active),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.tertiary
                        )
                    }

                    HorizontalDivider()

                    if (sharedFolderUris.isEmpty()) {
                        Text(
                                text = stringResource(R.string.settings_ft_no_folders),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    } else {
                        sharedFolderUris.forEach { uriString ->
                            val uri = Uri.parse(uriString)
                            val displayName = uri.lastPathSegment
                                    ?.substringAfterLast('/')
                                    ?.substringAfterLast(':')
                                    ?: uriString
                            Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                Icon(
                                        Icons.Default.FolderOpen,
                                        contentDescription = null,
                                        tint = MaterialTheme.colorScheme.primary,
                                        modifier = Modifier.size(20.dp)
                                )
                                Text(
                                        text = displayName,
                                        style = MaterialTheme.typography.bodySmall,
                                        modifier = Modifier.weight(1f),
                                        maxLines = 1
                                )
                                IconButton(
                                        onClick = {
                                            scope.launch { settingsManager.removeSharedFolderUri(uriString) }
                                        },
                                        modifier = Modifier.size(32.dp)
                                ) {
                                    Icon(
                                            Icons.Default.Close,
                                            contentDescription = stringResource(R.string.settings_ft_remove_folder),
                                            modifier = Modifier.size(16.dp)
                                    )
                                }
                            }
                        }
                    }

                    OutlinedButton(
                            onClick = { folderPickerLauncher.launch(null) },
                            modifier = Modifier.fillMaxWidth()
                    ) {
                        Icon(Icons.Default.Add, null, Modifier.size(18.dp))
                        Spacer(Modifier.size(6.dp))
                        Text(stringResource(R.string.settings_ft_add_folder))
                    }
                }
            }

            // Per-device access controls (plan §2 / WP9): consent for the PC to see this phone's
            // whole storage (a SAF root pick = the OS-level consent) and to auto-accept its pushes.
            FileTransferAccessCard(settingsManager)
        }
    }
}

/**
 * "Access from this PC" trust controls (plan §2 / WP9). Full-device browse is a single SAF
 * `ACTION_OPEN_DOCUMENT_TREE` grant of a storage root: the picker is the OS-level consent, mirrored
 * into the per-device `fileTrust_<deviceId>_fullBrowse` key and set/cleared in lock-step with the SAF
 * root URI the host mirror exposes as a volume. Auto-accept toggles the per-device
 * `fileTrust_<deviceId>_autoAccept` grant so incoming pushes skip the prompt.
 */
@Composable
private fun FileTransferAccessCard(settingsManager: SettingsManager) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    val host by settingsManager.hostFlow.collectAsStateWithLifecycle(initialValue = "")
    val deviceId = FilePeerIdentity.deviceId(host)
    val fullBrowseRoot by settingsManager.fullBrowseRootUriFlow.collectAsStateWithLifecycle(initialValue = null)
    val autoAcceptFlow = remember(deviceId) { settingsManager.fileTrustAutoAcceptFlow(deviceId) }
    val autoAccept by autoAcceptFlow.collectAsStateWithLifecycle(initialValue = false)

    val fullBrowseLauncher = rememberLauncherForActivityResult(
            ActivityResultContracts.OpenDocumentTree()
    ) { uri: Uri? ->
        if (uri != null) {
            context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            )
            scope.launch {
                // Re-derive the device id inside the coroutine so a late-loading host is honoured.
                val dev = FilePeerIdentity.deviceId(settingsManager.hostFlow.first())
                settingsManager.setFullBrowseRootUri(uri.toString())
                settingsManager.setFileTrustFullBrowse(dev, true)
            }
        }
    }

    Text(
            text = stringResource(R.string.settings_ft_access_title),
            style = MaterialTheme.typography.titleSmall,
            fontWeight = FontWeight.SemiBold,
            color = MaterialTheme.colorScheme.primary
    )

    Card(
            colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
            )
    ) {
        Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text(
                    text = stringResource(R.string.settings_ft_access_desc),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            HorizontalDivider()

            // Full-device browse.
            Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                            text = stringResource(R.string.settings_ft_full_browse_title),
                            style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                            text = stringResource(R.string.settings_ft_full_browse_desc),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Switch(
                        checked = fullBrowseRoot != null,
                        onCheckedChange = { enabled ->
                            if (enabled) {
                                fullBrowseLauncher.launch(null)
                            } else {
                                scope.launch {
                                    val dev = FilePeerIdentity.deviceId(settingsManager.hostFlow.first())
                                    settingsManager.clearFullBrowseRootUri()
                                    settingsManager.setFileTrustFullBrowse(dev, false)
                                }
                            }
                        }
                )
            }

            HorizontalDivider()

            // Auto-accept incoming pushes.
            Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                            text = stringResource(R.string.settings_ft_auto_accept_title),
                            style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                            text = stringResource(R.string.settings_ft_auto_accept_desc),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Switch(
                        checked = autoAccept,
                        onCheckedChange = { enabled ->
                            scope.launch {
                                val dev = FilePeerIdentity.deviceId(settingsManager.hostFlow.first())
                                settingsManager.setFileTrustAutoAccept(dev, enabled)
                            }
                        }
                )
            }
        }
    }
}

@Composable
private fun HelpTab(onReplayTutorial: (() -> Unit)?, onNavigateToAbout: (() -> Unit)?) {
    val view = LocalView.current
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()

    Scaffold(
        topBar = {
            RemexScreenHeader(title = stringResource(R.string.settings_help_title))
        }
    ) { innerPadding ->
        Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding)
                    .padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Spacer(modifier = Modifier.height(8.dp))

            if (onReplayTutorial != null) {
                OutlinedButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            scope.launch {
                                // Reset onboarding flag so the tutorial shows
                                settingsManager.resetOnboarding()
                                onReplayTutorial()
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                ) { Text(stringResource(R.string.settings_replay_tutorial)) }

                Text(
                        text = stringResource(R.string.settings_replay_tutorial_body),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Text(
                    text = stringResource(R.string.settings_faq_hint),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            if (onNavigateToAbout != null) {
                Spacer(modifier = Modifier.height(8.dp))
                OutlinedButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onNavigateToAbout()
                        },
                        modifier = Modifier.fillMaxWidth()
                ) { Text(stringResource(R.string.screen_about_title)) }
            }
        }
    }
}
