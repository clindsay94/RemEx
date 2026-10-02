package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.RowScope
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalTextStyle
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.Hyphens
import androidx.compose.ui.text.style.LineBreak
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.theme.currentDisplayType

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
    subtitleInDisplayFont: Boolean = true,
) {
    // Page title and subtitle use the PC's display type, picked per UI language (RemEx-kq10x.4).
    // Pass subtitleInDisplayFont = false when the subtitle is data (a folder path), not a sentence.
    val display = currentDisplayType()
    MediumTopAppBar(
        title = {
            Column {
                FittedHeaderText(
                    text = title,
                    style = LocalTextStyle.current.copy(
                        fontFamily = display.titleFamily,
                        fontWeight = display.titleWeight,
                    ),
                    floor = MaterialTheme.typography.titleMedium.fontSize,
                )
                if (subtitle != null) {
                    val base = MaterialTheme.typography.bodySmall
                    FittedHeaderText(
                        text = subtitle,
                        style = if (subtitleInDisplayFont) {
                            base.copy(fontFamily = display.subtitleFamily, fontWeight = display.subtitleWeight)
                        } else {
                            base
                        },
                        floor = MaterialTheme.typography.labelSmall.fontSize,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
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

/** Each shrink step keeps this share of the previous size. */
private const val HEADER_SHRINK_STEP = 0.9f

/**
 * One-line header text that shrinks in steps until it fits, down to [floor], before it may wrap
 * (RemEx-kq10x.4). Titles have to fit on one line at 360dp in all 9 languages; the wide display font
 * and long translations (Polish, Ukrainian) would otherwise end in an ellipsis. Only at the floor is
 * a second line allowed, and then it breaks between words.
 */
@Composable
private fun FittedHeaderText(
    text: String,
    style: TextStyle,
    floor: TextUnit,
    color: Color = Color.Unspecified,
) {
    var fontSize by remember(text, style) { mutableStateOf(style.fontSize) }
    val atFloor = !fontSize.isSp || !floor.isSp || fontSize.value <= floor.value
    Text(
        text = text,
        style = style.copy(fontSize = fontSize, lineBreak = LineBreak.Heading, hyphens = Hyphens.None),
        color = color,
        maxLines = if (atFloor) 2 else 1,
        softWrap = atFloor,
        overflow = TextOverflow.Ellipsis,
        onTextLayout = { layout ->
            val overflowed = layout.hasVisualOverflow ||
                (layout.lineCount > 0 && layout.isLineEllipsized(layout.lineCount - 1))
            if (overflowed && !atFloor) {
                fontSize = (fontSize.value * HEADER_SHRINK_STEP).coerceAtLeast(floor.value).sp
            }
        },
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
