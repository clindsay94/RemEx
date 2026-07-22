package com.clindsay94.remex.ui.components

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.FileProperties
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.screens.RemoteFileEntry
import java.text.DateFormat
import java.util.Date

/** Properties (metadata) bottom sheet (plan WP7). */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileManagerPropertiesSheet(
    properties: FileProperties?,
    thumbnailBase64: String?,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        // Metadata may still be in flight (v3 host round-trip): show a loading indicator until it
        // lands so the sheet does not pop open blank or, worse, stay invisible while loading.
        val effectsSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
        AnimatedContent(
            targetState = properties,
            transitionSpec = { fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec) },
        ) { props ->
            if (props == null) {
                Box(
                    modifier = Modifier.fillMaxWidth().heightIn(min = 140.dp).padding(24.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    RemexLoadingIndicator()
                }
            } else {
                PropertiesSheetContent(props, thumbnailBase64)
            }
        }
    }
}

@Composable
private fun PropertiesSheetContent(props: FileProperties, thumbnailBase64: String?) {
    val thumb = rememberThumbnail(thumbnailBase64)
    Column(modifier = Modifier.padding(horizontal = 24.dp).padding(bottom = 32.dp)) {
        if (thumb != null) {
            Image(
                bitmap = thumb,
                contentDescription = null,
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 200.dp)
                    .padding(top = 8.dp)
                    .clip(MaterialTheme.shapes.medium),
            )
        }
        Text(
            text = props.name,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = if (thumb != null) 12.dp else 0.dp),
        )
        HorizontalDivider(modifier = Modifier.padding(vertical = 12.dp))
        PropertyRow(
            stringResource(R.string.file_manager_prop_type),
            if (props.isDirectory) stringResource(R.string.file_manager_prop_folder)
            else stringResource(R.string.file_manager_prop_file),
        )
        if (!props.isDirectory) {
            PropertyRow(stringResource(R.string.file_manager_prop_size), FileManagerLogic.formatBytes(props.sizeBytes))
        }
        props.itemCount?.let {
            PropertyRow(stringResource(R.string.file_manager_prop_items), it.toString())
        }
        props.mimeType?.let {
            PropertyRow(stringResource(R.string.file_manager_prop_mime), it)
        }
        if (props.modifiedUtc > 0) {
            PropertyRow(stringResource(R.string.file_manager_prop_modified), formatTimestamp(props.modifiedUtc))
        }
        if (props.createdUtc > 0) {
            PropertyRow(stringResource(R.string.file_manager_prop_created), formatTimestamp(props.createdUtc))
        }
        PropertyRow(
            stringResource(R.string.file_manager_prop_readonly),
            if (props.readOnly) stringResource(R.string.file_manager_prop_yes)
            else stringResource(R.string.file_manager_prop_no),
        )
    }
}

@Composable
private fun PropertyRow(label: String, value: String) {
    Row(modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(0.4f),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.weight(0.6f),
        )
    }
}

private fun formatTimestamp(unixMs: Long): String =
    DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(unixMs))

/** Which of the destination picker's three body states is showing (drives the AnimatedContent swap). */
private enum class DestinationSheetContent { Loading, Empty, Populated }

/**
 * Destination picker sheet (plan WP7) for copy/move: browse folders within the current root and confirm
 * a target. Only folders are shown; the ".." row walks up.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileManagerDestinationSheet(
    isMove: Boolean,
    destinationPath: String,
    entries: List<RemoteFileEntry>,
    loading: Boolean,
    onNavigate: (RemoteFileEntry) -> Unit,
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(modifier = Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text(
                text = stringResource(
                    if (isMove) R.string.file_manager_move_here_title else R.string.file_manager_copy_here_title
                ),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
            )
            Text(
                text = destinationPath,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(vertical = 4.dp),
            )
            HorizontalDivider()
            // Loading / empty / folder-list swap runs through AnimatedContent so a finished fetch
            // cross-fades into the rows instead of replacing the spinner in a single frame.
            val listState = when {
                entries.isNotEmpty() -> DestinationSheetContent.Populated
                loading -> DestinationSheetContent.Loading
                else -> DestinationSheetContent.Empty
            }
            val effectsSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
            AnimatedContent(
                targetState = listState,
                transitionSpec = { fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec) },
            ) { state ->
                when (state) {
                    DestinationSheetContent.Populated ->
                        LazyColumn(modifier = Modifier.heightIn(min = 120.dp, max = 320.dp)) {
                            items(entries, key = { it.name }) { entry ->
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .combinedClickable(onClick = { onNavigate(entry) })
                                        .padding(horizontal = 8.dp, vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                                ) {
                                    Icon(
                                        imageVector = if (entry.name == FileManagerLogic.PARENT_ENTRY) Icons.AutoMirrored.Filled.ArrowBack else Icons.Default.Folder,
                                        contentDescription = null,
                                        tint = MaterialTheme.colorScheme.primary,
                                        modifier = Modifier.size(22.dp),
                                    )
                                    Text(entry.name, style = MaterialTheme.typography.bodyMedium)
                                }
                            }
                        }
                    DestinationSheetContent.Loading ->
                        Box(
                            modifier = Modifier.fillMaxWidth().heightIn(min = 120.dp).padding(24.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            RemexLoadingIndicator()
                        }
                    DestinationSheetContent.Empty ->
                        Box(
                            modifier = Modifier.fillMaxWidth().heightIn(min = 120.dp).padding(24.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                stringResource(R.string.file_transfer_empty),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                }
            }
            HorizontalDivider(modifier = Modifier.padding(vertical = 8.dp))
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = onDismiss) { Text(stringResource(R.string.dialog_cancel)) }
                Button(onClick = { onConfirm(destinationPath) }) {
                    Text(
                        stringResource(
                            if (isMove) R.string.file_manager_move_here else R.string.file_manager_copy_here
                        )
                    )
                }
            }
        }
    }
}

/** Reusable single-field text dialog (plan WP7): new folder / rename. */
@Composable
fun FileManagerTextDialog(
    title: String,
    hint: String,
    confirmLabel: String,
    initialValue: String = "",
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    var text by remember { mutableStateOf(initialValue) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                label = { Text(hint) },
                singleLine = true,
            )
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(text); onDismiss() }, enabled = text.isNotBlank()) {
                Text(confirmLabel)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.dialog_cancel)) } },
    )
}
