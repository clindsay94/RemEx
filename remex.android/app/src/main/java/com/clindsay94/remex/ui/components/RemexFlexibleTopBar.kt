package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.text.TextAutoSize
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
import androidx.compose.runtime.compositionLocalOf
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
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.theme.currentDisplayType
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes

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
/**
 * What the top bar's Up arrow does on the screen below it, or null for no arrow (3.0 comb,
 * no-up-arrow). The navigation shell provides it for routes pushed over the tabs (Sensors, Files,
 * Connection, Settings, FAQ, About, ...), and Settings re-provides it for its detail pane on a phone,
 * where Up means "back to the list". Tabs, dialogs-as-routes and the widget set-up screens get none.
 */
val LocalRemexUpAction = compositionLocalOf<(() -> Unit)?> { null }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemexFlexibleTopBar(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    /** Null takes the Up arrow from [LocalRemexUpAction] when there is one; pass a lambda to override. */
    navigationIcon: (@Composable () -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
    scrollBehavior: TopAppBarScrollBehavior? = null,
    colors: TopAppBarColors = remexFlexibleTopBarColors(),
    subtitleInDisplayFont: Boolean = true,
) {
    // Page title and subtitle use the PC's display type, picked per UI language (RemEx-kq10x.4).
    // Pass subtitleInDisplayFont = false when the subtitle is data (a folder path), not a sentence.
    val display = currentDisplayType()
    val upAction = LocalRemexUpAction.current
    val resolvedNavigationIcon: @Composable () -> Unit =
        navigationIcon
            ?: upAction?.let { up -> { RemexUpButton(onClick = up) } }
            ?: {}
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
        navigationIcon = resolvedNavigationIcon,
        actions = actions,
        scrollBehavior = scrollBehavior,
        colors = colors
    )
}

/** The standard Up arrow: auto-mirrored, with its label as a tooltip, as the QR scanner's is. */
@Composable
fun RemexUpButton(onClick: () -> Unit) {
    val label = stringResource(R.string.cd_back)
    RemexTooltip(label) {
        IconButton(onClick = onClick, shapes = rememberRemexIconButtonShapes()) {
            Icon(imageVector = Icons.AutoMirrored.Filled.ArrowBack, contentDescription = label)
        }
    }
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

/**
 * One-line header text that shrinks until it fits, down to [floor], before it may wrap
 * (RemEx-kq10x.4). Titles have to fit on one line at 360dp in all 9 languages; the wide display font
 * and long translations (Polish, Ukrainian) would otherwise end in an ellipsis. Only at the floor is
 * a second line allowed, and then it breaks between words.
 *
 * The size is found inside ONE measure pass by [TextAutoSize.StepBased] (Leanness K9). It replaced a
 * loop that shrank by 10% per frame from `onTextLayout`, which composed and laid the title out up to
 * four times, visibly stepping down, every time a screen opened. Clip, not Ellipsis, in the
 * single-line pass: auto-size judges fit by overflow, and an ellipsized line does not report one. A
 * title that still overflows at the floor switches, once, to the two-line form.
 */
@Composable
private fun FittedHeaderText(
    text: String,
    style: TextStyle,
    floor: TextUnit,
    color: Color = Color.Unspecified,
) {
    val headerStyle = style.copy(lineBreak = LineBreak.Heading, hyphens = Hyphens.None)
    val canShrink = style.fontSize.isSp && floor.isSp && style.fontSize.value > floor.value
    var wrapAtFloor by remember(text, style, floor) { mutableStateOf(false) }
    if (canShrink && !wrapAtFloor) {
        Text(
            text = text,
            style = headerStyle,
            color = color,
            maxLines = 1,
            softWrap = false,
            overflow = TextOverflow.Clip,
            autoSize = TextAutoSize.StepBased(minFontSize = floor, maxFontSize = style.fontSize),
            onTextLayout = { layout -> if (layout.hasVisualOverflow) wrapAtFloor = true },
        )
    } else {
        Text(
            text = text,
            style = if (canShrink) headerStyle.copy(fontSize = floor) else headerStyle,
            color = color,
            maxLines = 2,
            softWrap = true,
            overflow = TextOverflow.Ellipsis,
        )
    }
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
                    IconButton(onClick = {}, shapes = rememberRemexIconButtonShapes()) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = stringResource(R.string.cd_back)
                        )
                    }
                }
            },
            actions = {
                RemexTooltip(stringResource(R.string.nav_more_label)) {
                    IconButton(onClick = {}, shapes = rememberRemexIconButtonShapes()) {
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
