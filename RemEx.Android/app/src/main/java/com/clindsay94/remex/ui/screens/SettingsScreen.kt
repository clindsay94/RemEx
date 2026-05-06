package com.clindsay94.remex.ui.screens

import android.os.Parcelable
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Help
import androidx.compose.material.icons.automirrored.filled.Input
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.*
import androidx.compose.material3.adaptive.ExperimentalMaterial3AdaptiveApi
import androidx.compose.material3.adaptive.layout.AnimatedPane
import androidx.compose.material3.adaptive.layout.ListDetailPaneScaffoldRole
import androidx.compose.material3.adaptive.navigation.NavigableListDetailPaneScaffold
import androidx.compose.material3.adaptive.navigation.rememberListDetailPaneScaffoldNavigator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
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
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.components.RemexScreenHeader
import kotlinx.coroutines.launch
import kotlinx.parcelize.Parcelize

@Parcelize
enum class SettingsCategory : Parcelable {
    CONNECTION,
    PERSONALIZATION,
    INPUT,
    HELP;

    val titleRes: Int
        get() = when (this) {
            CONNECTION -> R.string.settings_tab_connection
            PERSONALIZATION -> R.string.settings_tab_personalization
            INPUT -> R.string.settings_tab_input
            HELP -> R.string.settings_tab_help
        }

    val icon: ImageVector
        get() = when (this) {
            CONNECTION -> Icons.Default.Wifi
            PERSONALIZATION -> Icons.Default.Palette
            INPUT -> Icons.AutoMirrored.Filled.Input
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

@Composable
private fun SettingsCategoryList(
    selectedCategory: SettingsCategory?,
    onCategoryClick: (SettingsCategory) -> Unit
) {
    val view = LocalView.current
    Scaffold(
        topBar = {
            RemexScreenHeader(title = stringResource(R.string.screen_settings_title))
        }
    ) { innerPadding ->
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
        ) {
            items(SettingsCategory.entries) { category ->
                NavigationDrawerItem(
                    label = { Text(stringResource(category.titleRes)) },
                    icon = { Icon(category.icon, contentDescription = null) },
                    selected = category == selectedCategory,
                    onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onCategoryClick(category)
                    },
                    modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
                    badge = {
                        if (category != selectedCategory) {
                             Icon(Icons.Default.ChevronRight, contentDescription = null, modifier = Modifier.size(16.dp))
                        }
                    }
                )
            }
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
            settingsManager.remoteDesktopPreferencesFlow.collectAsState(
                    initial = SettingsManager.RemoteDesktopPreferences()
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
