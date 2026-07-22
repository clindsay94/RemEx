package com.clindsay94.remex.ui.components

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.SizeTransform
import androidx.compose.animation.expandHorizontally
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.DriveFileMove
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.SelectAll
import androidx.compose.material.icons.filled.Sort
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileViewMode
import com.clindsay94.remex.ui.screens.SortField
import com.clindsay94.remex.ui.screens.SortOption

/**
 * Primary file-manager toolbar (plan WP7): expandable debounced search, sort menu (name/size/date ×
 * asc/desc), list/grid toggle, new-folder, and upload. Purely presentational — the ViewModel owns the
 * debounce + persistence.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileManagerToolbar(
    searchQuery: String,
    onSearchChange: (String) -> Unit,
    sortOption: SortOption,
    onSort: (SortField) -> Unit,
    viewMode: FileViewMode,
    onToggleViewMode: () -> Unit,
    canWrite: Boolean,
    onNewFolder: () -> Unit,
    onUpload: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var searchExpanded by remember { mutableStateOf(false) }
    var sortMenuOpen by remember { mutableStateOf(false) }

    // The icon row and the full-width search field swap through AnimatedContent: the field grows
    // out of the search icon's start edge while the row fades, and shrinks back on close.
    val effectsSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    val spatialSpec = MaterialTheme.motionScheme.defaultSpatialSpec<IntSize>()
    AnimatedContent(
        targetState = searchExpanded,
        transitionSpec = {
            val transform = if (targetState) {
                (fadeIn(effectsSpec) + expandHorizontally(spatialSpec, expandFrom = Alignment.Start)) togetherWith
                    fadeOut(effectsSpec)
            } else {
                fadeIn(effectsSpec) togetherWith
                    (fadeOut(effectsSpec) + shrinkHorizontally(spatialSpec, shrinkTowards = Alignment.Start))
            }
            transform using SizeTransform(sizeAnimationSpec = { _, _ -> spatialSpec })
        },
        modifier = modifier.fillMaxWidth(),
    ) { expanded ->
        if (expanded) {
            OutlinedTextField(
                value = searchQuery,
                onValueChange = onSearchChange,
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                placeholder = { Text(stringResource(R.string.file_manager_search_hint)) },
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                trailingIcon = {
                    IconButton(onClick = {
                        onSearchChange("")
                        searchExpanded = false
                    }) {
                        Icon(Icons.Default.Close, contentDescription = stringResource(R.string.file_manager_search_close))
                    }
                },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(),
            )
        } else {
            ToolbarIconRow(
                onExpandSearch = { searchExpanded = true },
                sortMenuOpen = sortMenuOpen,
                onSortMenuOpenChange = { sortMenuOpen = it },
                sortOption = sortOption,
                onSort = onSort,
                viewMode = viewMode,
                onToggleViewMode = onToggleViewMode,
                canWrite = canWrite,
                onNewFolder = onNewFolder,
                onUpload = onUpload,
            )
        }
    }
}

@Composable
private fun ToolbarIconRow(
    onExpandSearch: () -> Unit,
    sortMenuOpen: Boolean,
    onSortMenuOpenChange: (Boolean) -> Unit,
    sortOption: SortOption,
    onSort: (SortField) -> Unit,
    viewMode: FileViewMode,
    onToggleViewMode: () -> Unit,
    canWrite: Boolean,
    onNewFolder: () -> Unit,
    onUpload: () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(0.dp),
    ) {
        IconButton(onClick = onExpandSearch) {
            Icon(Icons.Default.Search, contentDescription = stringResource(R.string.file_manager_search))
        }

        // Sort menu
        IconButton(onClick = { onSortMenuOpenChange(true) }) {
            Icon(Icons.Default.Sort, contentDescription = stringResource(R.string.file_manager_sort))
        }
        DropdownMenu(expanded = sortMenuOpen, onDismissRequest = { onSortMenuOpenChange(false) }) {
            SortMenuRow(R.string.file_manager_sort_name, SortField.NAME, sortOption, onSort) { onSortMenuOpenChange(false) }
            SortMenuRow(R.string.file_manager_sort_size, SortField.SIZE, sortOption, onSort) { onSortMenuOpenChange(false) }
            SortMenuRow(R.string.file_manager_sort_date, SortField.DATE, sortOption, onSort) { onSortMenuOpenChange(false) }
        }

        IconButton(onClick = onToggleViewMode) {
            Icon(
                imageVector = if (viewMode == FileViewMode.LIST) Icons.Default.GridView else Icons.AutoMirrored.Filled.List,
                contentDescription = stringResource(
                    if (viewMode == FileViewMode.LIST) R.string.file_manager_view_grid else R.string.file_manager_view_list
                ),
            )
        }

        Row(modifier = Modifier.weight(1f), horizontalArrangement = Arrangement.End) {
            if (canWrite) {
                IconButton(onClick = onNewFolder) {
                    Icon(Icons.Default.CreateNewFolder, contentDescription = stringResource(R.string.file_manager_new_folder))
                }
                IconButton(onClick = onUpload) {
                    Icon(Icons.Default.Upload, contentDescription = stringResource(R.string.file_transfer_upload))
                }
            }
        }
    }
}

@Composable
private fun SortMenuRow(
    labelRes: Int,
    field: SortField,
    sortOption: SortOption,
    onSort: (SortField) -> Unit,
    dismiss: () -> Unit,
) {
    val active = sortOption.field == field
    DropdownMenuItem(
        text = { Text(stringResource(labelRes)) },
        leadingIcon = {
            if (active) Icon(Icons.Default.Check, contentDescription = null)
            else Icon(Icons.Default.Check, contentDescription = null, tint = MaterialTheme.colorScheme.surface)
        },
        trailingIcon = {
            if (active) {
                Icon(
                    imageVector = if (sortOption.ascending) Icons.Default.ArrowUpward else Icons.Default.ArrowDownward,
                    contentDescription = null,
                    modifier = Modifier.size(18.dp),
                )
            }
        },
        onClick = { onSort(field); dismiss() },
    )
}

/**
 * Contextual multi-select action bar (plan WP7): close, count, select-all, then copy / move / delete
 * (each gated by root capability).
 */
@Composable
fun FileManagerSelectionBar(
    selectedCount: Int,
    canWrite: Boolean,
    canDelete: Boolean,
    onClose: () -> Unit,
    onSelectAll: () -> Unit,
    onCopy: () -> Unit,
    onMove: () -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(color = MaterialTheme.colorScheme.secondaryContainer, modifier = modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
            horizontalArrangement = Arrangement.spacedBy(2.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onClose) {
                Icon(Icons.Default.Close, contentDescription = stringResource(R.string.file_transfer_cancel_selection))
            }
            Text(
                text = "$selectedCount ${stringResource(R.string.file_transfer_selected)}",
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.weight(1f),
            )
            IconButton(onClick = onSelectAll) {
                Icon(Icons.Default.SelectAll, contentDescription = stringResource(R.string.file_transfer_select_all))
            }
            if (canWrite) {
                IconButton(onClick = onCopy, enabled = selectedCount > 0) {
                    Icon(Icons.Default.ContentCopy, contentDescription = stringResource(R.string.file_manager_copy))
                }
                IconButton(onClick = onMove, enabled = selectedCount > 0) {
                    Icon(Icons.AutoMirrored.Filled.DriveFileMove, contentDescription = stringResource(R.string.file_manager_move))
                }
            }
            if (canDelete) {
                IconButton(onClick = onDelete, enabled = selectedCount > 0) {
                    Icon(
                        Icons.Default.Delete,
                        contentDescription = stringResource(R.string.file_transfer_delete_selected),
                        tint = if (selectedCount > 0) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}
