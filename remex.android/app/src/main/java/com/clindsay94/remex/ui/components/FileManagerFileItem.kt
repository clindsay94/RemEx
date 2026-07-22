package com.clindsay94.remex.ui.components

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.Image
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.RadioButtonUnchecked
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemoteFileEntry

/** Decodes a base64 JPEG thumbnail to an [ImageBitmap], memoised on the encoded string. */
@Composable
internal fun rememberThumbnail(base64: String?): ImageBitmap? {
    return remember(base64) {
        if (base64.isNullOrBlank()) null
        else try {
            val bytes = Base64.decode(base64, Base64.DEFAULT)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
        } catch (e: Exception) {
            null
        }
    }
}

private fun subtitleFor(entry: RemoteFileEntry): String =
    if (entry.isDirectory) ""
    else buildString {
        if (entry.sizeBytes > 0) append(FileManagerLogic.formatBytes(entry.sizeBytes))
        entry.relativePath?.let {
            if (isNotEmpty()) append("  •  ")
            append(it)
        }
    }

/** Single-column list row (plan WP7). Long-press enters multi-select; a checkbox replaces the icon there. */
@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileManagerListItem(
    entry: RemoteFileEntry,
    isSelectionMode: Boolean,
    isSelected: Boolean,
    thumbnailBase64: String?,
    showDownload: Boolean,
    showOverflow: Boolean,
    onRequestThumbnail: () -> Unit,
    onTap: () -> Unit,
    onLongPress: () -> Unit,
    onDownload: () -> Unit,
    onOverflow: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (!entry.isDirectory && FileManagerLogic.isThumbnailCandidate(entry.name)) {
        LaunchedEffect(entry.relativePath ?: entry.name) { onRequestThumbnail() }
    }
    val thumb = rememberThumbnail(thumbnailBase64)
    val subtitle = subtitleFor(entry)

    // Selected rows get an animated container highlight; selection-mode entry swaps the
    // leading slot with a fade instead of a hard layout jump (RemEx-40y8).
    val rowColor by animateColorAsState(
        targetValue = if (isSelected) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "row_container",
    )
    val leadingFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(rowColor)
            .combinedClickable(onClick = onTap, onLongClick = onLongPress)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        AnimatedContent(
            targetState = isSelectionMode && entry.name != FileManagerLogic.PARENT_ENTRY,
            transitionSpec = { fadeIn(leadingFadeSpec) togetherWith fadeOut(leadingFadeSpec) },
            label = "leading_slot",
        ) { selecting ->
        if (selecting) {
            SelectionIndicator(isSelected = isSelected, iconSize = 24.dp)
        } else if (thumb != null) {
            Image(
                bitmap = thumb,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.size(40.dp).clip(MaterialTheme.shapes.small),
            )
        } else {
            Icon(
                imageVector = if (entry.isDirectory) Icons.Default.Folder else Icons.AutoMirrored.Filled.InsertDriveFile,
                contentDescription = null,
                tint = if (entry.isDirectory) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(24.dp),
            )
        }
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = entry.name,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = if (entry.isDirectory) FontWeight.SemiBold else FontWeight.Normal,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (subtitle.isNotEmpty()) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }

        if (!isSelectionMode && entry.name != FileManagerLogic.PARENT_ENTRY) {
            if (showDownload && !entry.isDirectory) {
                IconButton(onClick = onDownload) {
                    Icon(
                        Icons.Default.Download,
                        contentDescription = stringResource(R.string.file_transfer_download),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (showOverflow) {
                IconButton(onClick = onOverflow) {
                    Icon(
                        Icons.Default.MoreVert,
                        contentDescription = stringResource(R.string.cd_more_options),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

/** Thumbnail grid cell (plan WP7). Tap opens/selects; long-press toggles multi-select. */
@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileManagerGridItem(
    entry: RemoteFileEntry,
    isSelectionMode: Boolean,
    isSelected: Boolean,
    thumbnailBase64: String?,
    showOverflow: Boolean,
    onRequestThumbnail: () -> Unit,
    onTap: () -> Unit,
    onLongPress: () -> Unit,
    onOverflow: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (!entry.isDirectory && FileManagerLogic.isThumbnailCandidate(entry.name)) {
        LaunchedEffect(entry.relativePath ?: entry.name) { onRequestThumbnail() }
    }
    val thumb = rememberThumbnail(thumbnailBase64)

    Column(
        modifier = modifier
            .combinedClickable(onClick = onTap, onLongClick = onLongPress)
            .padding(6.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Box(modifier = Modifier.fillMaxWidth().aspectRatio(1f), contentAlignment = Alignment.Center) {
            val tileColor by animateColorAsState(
                targetValue = if (isSelected) MaterialTheme.colorScheme.secondaryContainer
                    else MaterialTheme.colorScheme.surfaceContainerHighest,
                animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
                label = "tile_container",
            )
            Surface(
                shape = MaterialTheme.shapes.medium,
                color = tileColor,
                modifier = Modifier.fillMaxSize(),
            ) {
                if (thumb != null) {
                    Image(
                        bitmap = thumb,
                        contentDescription = null,
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                } else {
                    Box(contentAlignment = Alignment.Center) {
                        Icon(
                            imageVector = if (entry.isDirectory) Icons.Default.Folder else Icons.AutoMirrored.Filled.InsertDriveFile,
                            contentDescription = null,
                            tint = if (entry.isDirectory) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(36.dp),
                        )
                    }
                }
            }
            val selectingHere = isSelectionMode && entry.name != FileManagerLogic.PARENT_ENTRY
            // Fully qualified: inside this Box the ColumnScope.AnimatedVisibility extension
            // shadows the top-level overload via an outer implicit receiver.
            androidx.compose.animation.AnimatedVisibility(
                visible = selectingHere,
                enter = fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
                exit = fadeOut(MaterialTheme.motionScheme.defaultEffectsSpec()),
                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp),
            ) {
                // Translucent backing keeps the indicator readable over photo thumbnails,
                // matching the overflow chip below.
                Surface(
                    shape = CircleShape,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.7f),
                ) {
                    SelectionIndicator(
                        isSelected = isSelected,
                        iconSize = 22.dp,
                        modifier = Modifier.padding(2.dp),
                    )
                }
            }
            if (!selectingHere && showOverflow && entry.name != FileManagerLogic.PARENT_ENTRY) {
                // Per-item actions (download/rename/delete/pin) — the list view exposes these via its
                // trailing overflow; the grid needs its own affordance or they were unreachable here.
                // The chip stays 28dp visually; minimumInteractiveComponentSize keeps the clickable
                // bounds at the Material 48dp minimum (same pattern as the dashboard pin toggle).
                Surface(
                    shape = CircleShape,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.7f),
                    onClick = onOverflow,
                    modifier = Modifier.align(Alignment.TopEnd)
                        // Surface(onClick) sets no semantic role; announce as a button (RemEx-qluo).
                        .semantics { role = Role.Button }
                        .minimumInteractiveComponentSize().size(28.dp),
                ) {
                    Box(contentAlignment = Alignment.Center, modifier = Modifier.fillMaxSize()) {
                        Icon(
                            Icons.Default.MoreVert,
                            contentDescription = stringResource(R.string.cd_more_options),
                            tint = MaterialTheme.colorScheme.onSurface,
                            modifier = Modifier.size(18.dp),
                        )
                    }
                }
            }
        }
        Text(
            text = entry.name,
            style = MaterialTheme.typography.labelSmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
            fontWeight = if (entry.isDirectory) FontWeight.SemiBold else FontWeight.Normal,
        )
    }
}

/**
 * Animated selection indicator shared by the list row and grid tile: the check/uncheck icon
 * cross-fades and its tint animates between the selected and unselected roles (RemEx-40y8).
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun SelectionIndicator(
    isSelected: Boolean,
    iconSize: androidx.compose.ui.unit.Dp,
    modifier: Modifier = Modifier,
) {
    val tint by animateColorAsState(
        targetValue = if (isSelected) MaterialTheme.colorScheme.primary
            else MaterialTheme.colorScheme.onSurfaceVariant,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "sel_tint",
    )
    val checkFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    AnimatedContent(
        targetState = isSelected,
        transitionSpec = { fadeIn(checkFadeSpec) togetherWith fadeOut(checkFadeSpec) },
        label = "sel_check",
        modifier = modifier,
    ) { selected ->
        Icon(
            imageVector = if (selected) Icons.Default.CheckCircle else Icons.Default.RadioButtonUnchecked,
            contentDescription = null,
            tint = tint,
            modifier = Modifier.size(iconSize),
        )
    }
}
