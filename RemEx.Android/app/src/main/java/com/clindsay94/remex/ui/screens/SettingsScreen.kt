package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Tab
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.components.RemexScreenHeader
import kotlinx.coroutines.launch

/**
 * Unified Settings screen that merges Connection, Personalization,
 * and Help into a single tabbed page.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    onReplayTutorial: (() -> Unit)? = null
) {
    val tabs = listOf(
        stringResource(R.string.settings_tab_connection),
        stringResource(R.string.settings_tab_personalization),
        stringResource(R.string.settings_tab_input),
        stringResource(R.string.settings_tab_help)
    )
    val pagerState = rememberPagerState(pageCount = { tabs.size })
    val coroutineScope = rememberCoroutineScope()

    Column(modifier = Modifier.fillMaxSize()) {
        RemexScreenHeader(title = stringResource(R.string.screen_settings_title))
        PrimaryTabRow(
            selectedTabIndex = pagerState.currentPage,
            containerColor = MaterialTheme.colorScheme.surface,
            contentColor = MaterialTheme.colorScheme.primary
        ) {
            val haptic = LocalHapticFeedback.current
            tabs.forEachIndexed { index, title ->
                Tab(
                    selected = pagerState.currentPage == index,
                    onClick = {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        coroutineScope.launch { pagerState.animateScrollToPage(index) }
                    },
                    text = { Text(title) }
                )
            }
        }

        HorizontalPager(
            state = pagerState,
            modifier = Modifier.fillMaxSize()
        ) { page ->
            when (page) {
                0 -> ConnectionScreen()
                1 -> PersonalizationScreen(showHeader = false)
                2 -> InputTab()
                3 -> HelpTab(onReplayTutorial = onReplayTutorial)
            }
        }
    }
}

@Composable
private fun InputTab() {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    val preferences by
            settingsManager.remoteDesktopPreferencesFlow.collectAsState(
                    initial = SettingsManager.RemoteDesktopPreferences()
            )

    Column(
            modifier = Modifier.fillMaxSize().padding(16.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(20.dp)
    ) {
        Text(
                text = stringResource(R.string.settings_tab_input),
                style = MaterialTheme.typography.headlineMedium,
                fontWeight = FontWeight.Black,
                letterSpacing = (-1).sp
        )

        Card(
                colors =
                        CardDefaults.cardColors(
                                containerColor =
                                        MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
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
                                            stringResource(R.string.remote_desktop_pointer_speed_label),
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
                                                    R.string.remote_desktop_v_scroll_sensitivity_label
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
                                                    R.string.remote_desktop_h_scroll_sensitivity_label
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
                            valueRange = 0.1f..5.0f,
                            steps = 20
                    )
                }
            }
        }
    }
}

@Composable
private fun HelpTab(onReplayTutorial: (() -> Unit)?) {
    val haptic = LocalHapticFeedback.current
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Text(
            text = stringResource(R.string.settings_help_title),
            style = MaterialTheme.typography.titleLarge,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onSurface
        )

        Spacer(modifier = Modifier.height(8.dp))

        if (onReplayTutorial != null) {
            OutlinedButton(
                onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    scope.launch {
                        // Reset onboarding flag so the tutorial shows
                        settingsManager.resetOnboarding()
                        onReplayTutorial()
                    }
                },
                modifier = Modifier.fillMaxWidth()
            ) {
                Text(stringResource(R.string.settings_replay_tutorial))
            }

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
    }
}
