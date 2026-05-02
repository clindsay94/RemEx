package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.RowScope
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MediumFlexibleTopAppBar
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBarColors
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.TopAppBarScrollBehavior
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow

/**
 * Project-wide flexible top app bar.
 *
 * - Uses [MediumFlexibleTopAppBar] from M3 Expressive (dynamic title scaling between
 * collapsed/expanded states).
 * - Container is transparent in both expanded and scrolled states so the surface underneath shows
 * through and content can scroll behind the icons.
 * - Title and action icons stay on theme content colors so they remain visible over any background.
 *
 * Use with [rememberRemexTopBarScrollBehavior] like:
 * ```
 * val scrollBehavior = rememberRemexTopBarScrollBehavior()
 * Scaffold(
 *     modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
 *     topBar = { RemexFlexibleTopBar(title = "...", scrollBehavior = scrollBehavior) }
 * )
 * ```
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemexFlexibleTopBar(
        title: String,
        scrollBehavior: TopAppBarScrollBehavior,
        modifier: Modifier = Modifier,
        subtitle: String? = null,
        navigationIcon: @Composable () -> Unit = {},
        actions: @Composable RowScope.() -> Unit = {},
        colors: TopAppBarColors = remexFlexibleTopBarColors(),
) {
    MediumFlexibleTopAppBar(
            title = {
                Text(
                        text = title,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                )
            },
            modifier = modifier,
            subtitle = {
                if (subtitle != null) {
                    Text(text = subtitle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            },
            navigationIcon = navigationIcon,
            actions = actions,
            colors = colors,
            scrollBehavior = scrollBehavior,
    )
}

/**
 * Pinned scroll behavior so the bar stays anchored at the top while the content scrolls beneath.
 * Pair with [Modifier.nestedScroll] on the [Scaffold].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun rememberRemexTopBarScrollBehavior(): TopAppBarScrollBehavior =
        TopAppBarDefaults.pinnedScrollBehavior()

/**
 * Exit-until-collapsed scroll behavior for screens where the top bar should collapse as the user
 * scrolls down. Pair with [Modifier.nestedScroll] on the [Scaffold].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun rememberRemexCollapsingScrollBehavior(): TopAppBarScrollBehavior =
        TopAppBarDefaults.exitUntilCollapsedScrollBehavior()

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun remexFlexibleTopBarColors(): TopAppBarColors =
        TopAppBarDefaults.topAppBarColors(
                containerColor = Color.Transparent,
                scrolledContainerColor = Color.Transparent,
                titleContentColor = MaterialTheme.colorScheme.onSurface,
                navigationIconContentColor = MaterialTheme.colorScheme.onSurface,
                actionIconContentColor = MaterialTheme.colorScheme.onSurfaceVariant,
        )
