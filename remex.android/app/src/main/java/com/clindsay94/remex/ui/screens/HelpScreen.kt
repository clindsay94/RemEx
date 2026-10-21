package com.clindsay94.remex.ui.screens

import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.Notes
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.IosShare
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.navigation.Screen
import com.clindsay94.remex.ui.navigation.TabReselectEffect
import com.clindsay94.remex.ui.theme.RemExTheme

/** One row of Help & diagnostics, in the order the screen lists them (RemEx-172ba, HelpEntriesTest). */
enum class HelpEntry(@StringRes val titleRes: Int, @StringRes val bodyRes: Int) {
    FAQ(R.string.screen_faq_title, R.string.settings_faq_hint),
    PC_LOGS(R.string.screen_pc_diagnostics_title, R.string.help_pc_logs_body),
    SHARE_DIAGNOSTICS(R.string.settings_share_diagnostics, R.string.help_share_diagnostics_body),
    REPLAY_TUTORIAL(R.string.settings_replay_tutorial, R.string.help_tutorial_body),
    ABOUT(R.string.screen_about_title, R.string.help_about_body),
}

private fun HelpEntry.icon(): ImageVector =
        when (this) {
            HelpEntry.FAQ -> Icons.AutoMirrored.Filled.HelpOutline
            HelpEntry.PC_LOGS -> Icons.AutoMirrored.Filled.Notes
            HelpEntry.SHARE_DIAGNOSTICS -> Icons.Default.IosShare
            HelpEntry.REPLAY_TUTORIAL -> Icons.Default.Replay
            HelpEntry.ABOUT -> Icons.Default.Info
        }

/**
 * Help & diagnostics (RemEx-172ba): the one place for getting unstuck, so More and Settings no longer
 * carry FAQ, PC logs or a Help category. Each row opens its own screen; replaying the tutorial leaves
 * the first-run flag alone, so backing out of a replay never re-arms the first-run tour.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HelpScreen(onOpen: (HelpEntry) -> Unit) {
    val haptics = rememberRemexHaptics()
    val listState = rememberLazyListState()
    TabReselectEffect(Screen.Help) { if (listState.canScrollBackward) listState.animateScrollToItem(0) }
    val topBarScrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
            modifier = Modifier.nestedScroll(topBarScrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_help_title),
                        scrollBehavior = topBarScrollBehavior,
                )
            }
    ) { innerPadding ->
        LazyColumn(
                state = listState,
                modifier = Modifier.fillMaxSize().padding(innerPadding),
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(HelpEntry.entries, key = { it.name }) { entry ->
                Surface(
                        onClick = {
                            haptics.perform(RemexHapticEvent.Press)
                            onOpen(entry)
                        },
                        shape = MaterialTheme.shapes.large,
                        color = MaterialTheme.colorScheme.surfaceContainerLow,
                        modifier = Modifier.fillMaxWidth()
                ) {
                    ListItem(
                            supportingContent = { Text(stringResource(entry.bodyRes)) },
                            leadingContent = { Icon(entry.icon(), contentDescription = null) },
                            trailingContent = {
                                Icon(
                                        Icons.AutoMirrored.Filled.KeyboardArrowRight,
                                        contentDescription = null,
                                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            },
                            colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
                    ) {
                        Text(stringResource(entry.titleRes))
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun HelpScreenPreview() {
    RemExTheme { HelpScreen(onOpen = {}) }
}
