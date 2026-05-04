package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.RowScope
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MediumTopAppBar
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBarColors
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.TopAppBarScrollBehavior
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow

/**
 * Project-wide flexible top app bar.
 *
 * Usage:
 *     val scrollBehavior = rememberRemexCollapsingScrollBehavior()
 *     Scaffold(
 *         modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
 *         topBar = { RemexFlexibleTopBar(title = "...", scrollBehavior = scrollBehavior) }
 *     )
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemexFlexibleTopBar(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    navigationIcon: @Composable () -> Unit = {},
    actions: @Composable RowScope.() -> Unit = {},
    scrollBehavior: TopAppBarScrollBehavior? = null,
    colors: TopAppBarColors = remexFlexibleTopBarColors(),
) {
    MediumTopAppBar(
        title = {
            Column {
                Text(
                    text = title,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (subtitle != null) {
                    Text(
                        text = subtitle,
                        style = MaterialTheme.typography.bodySmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        },
        modifier = modifier,
        navigationIcon = navigationIcon,
        actions = actions,
        scrollBehavior = scrollBehavior,
        colors = colors
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun rememberRemexTopBarScrollBehavior(): TopAppBarScrollBehavior =
    TopAppBarDefaults.pinnedScrollBehavior()

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun rememberRemexCollapsingScrollBehavior(): TopAppBarScrollBehavior =
    TopAppBarDefaults.exitUntilCollapsedScrollBehavior()

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun remexFlexibleTopBarColors(): TopAppBarColors =
    TopAppBarDefaults.topAppBarColors()
