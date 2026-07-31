package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.RowScope
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MediumTopAppBar
import androidx.compose.material3.PlainTooltip
import androidx.compose.material3.Text
import androidx.compose.material3.TooltipAnchorPosition
import androidx.compose.material3.TooltipBox
import androidx.compose.material3.TooltipDefaults
import androidx.compose.material3.TopAppBarColors
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.TopAppBarScrollBehavior
import androidx.compose.material3.rememberTooltipState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme

/**
 * Project-wide flexible top app bar.
 *
 * NOTE: This intentionally uses the stable [MediumTopAppBar] rather than the
 * Expressive `MediumFlexibleTopAppBar`. As of material3 1.5.0-alpha20 the flexible
 * variant measures to an invalid (negative) constraint on some screens and crashes
 * at layout time with `IllegalArgumentException: maxWidth must be >= than minWidth`.
 * Revisit swapping to the flexible variant once that alpha bug is fixed upstream.
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
                    overflow = TextOverflow.Ellipsis,
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

/**
 * Wraps any icon-only control in an M3 [PlainTooltip] so long-press shows a visible label —
 * the M3 requirement for icon-only affordances (RemEx-31wq). Pass the SAME string used for
 * the icon's contentDescription so sighted long-press and TalkBack announce identical labels.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemexTooltip(label: String, content: @Composable () -> Unit) {
    TooltipBox(
        // TooltipAnchorPosition.Above reproduces what the removed rememberPlainTooltipPositionProvider
        // did: prefer above the anchor, fall back below when there is no room.
        positionProvider =
            TooltipDefaults.rememberTooltipPositionProvider(TooltipAnchorPosition.Above),
        tooltip = { PlainTooltip { Text(label) } },
        state = rememberTooltipState(),
        content = content,
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

@OptIn(ExperimentalMaterial3Api::class)
@Preview(showBackground = true)
@Composable
private fun RemexFlexibleTopBarPreview() {
    RemExTheme {
        RemexFlexibleTopBar(
            title = "Title"
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Preview(showBackground = true)
@Composable
private fun RemexFlexibleTopBarWithSubtitleAndActionsPreview() {
    RemExTheme {
        RemexFlexibleTopBar(
            title = "Main Title",
            subtitle = "Secondary subtitle",
            navigationIcon = {
                RemexTooltip(stringResource(R.string.cd_back)) {
                    IconButton(onClick = {}) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = stringResource(R.string.cd_back)
                        )
                    }
                }
            },
            actions = {
                RemexTooltip(stringResource(R.string.nav_more_label)) {
                    IconButton(onClick = {}) {
                        Icon(
                            imageVector = Icons.Default.MoreVert,
                            contentDescription = stringResource(R.string.nav_more_label)
                        )
                    }
                }
            }
        )
    }
}
