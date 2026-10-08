package com.clindsay94.remex.ui.files

import android.text.format.DateUtils
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Image
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.rememberTransformableState
import androidx.compose.foundation.gestures.transformable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Fingerprint
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.files.preview.SyntaxKind
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes

/** What the preview pane can do besides showing the file; null hides the button. */
class PreviewActions(
    val onBack: (() -> Unit)?,
    val onLive: (Boolean) -> Unit,
    val onComputeHash: () -> Unit,
    val onCompare: (String) -> Unit,
    val onVerifyAgainst: () -> Unit,
    val onCopyHash: (String) -> Unit,
    val onOpenWith: (() -> Unit)?,
    val onSendAcross: (() -> Unit)?,
    val onRetry: () -> Unit,
)

/**
 * The preview pane (file browser redesign, 2026-10-08): the selected file as a zoomable image or coloured text
 * (with a live tail for logs), or its details, plus the Integrity card for its SHA-256 fingerprint.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FilePreviewPane(ui: PreviewUi<ImageBitmap>, actions: PreviewActions, modifier: Modifier = Modifier) {
    val target = ui.target
    Column(modifier = modifier.fillMaxSize()) {
        Row(
            modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp).padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (actions.onBack != null) {
                IconButton(onClick = actions.onBack, shapes = rememberRemexIconButtonShapes()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.files_preview_back))
                }
            }
            Text(
                text = target?.name ?: stringResource(R.string.files_preview_title),
                style = MaterialTheme.typography.titleMediumEmphasized,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f).padding(horizontal = 12.dp),
            )
            if (ui.canTail) {
                FilterChip(
                    selected = ui.live,
                    onClick = { actions.onLive(!ui.live) },
                    label = { Text(stringResource(R.string.files_preview_live)) },
                    leadingIcon = { Icon(Icons.Default.Visibility, contentDescription = null, modifier = Modifier.size(18.dp)) },
                    modifier = Modifier.padding(end = 8.dp),
                )
            }
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
        Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
            when (ui.state) {
                PreviewState.Empty -> Message(Icons.AutoMirrored.Filled.InsertDriveFile, stringResource(R.string.files_preview_empty))
                PreviewState.Loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    RemexLoadingIndicator(contained = true)
                }
                PreviewState.Image -> ui.image?.let { ZoomableImage(it, target?.name.orEmpty()) }
                PreviewState.Text -> TextBody(ui)
                PreviewState.Binary -> Message(Icons.AutoMirrored.Filled.InsertDriveFile, stringResource(R.string.files_preview_binary))
                PreviewState.TooLarge -> Message(
                    Icons.AutoMirrored.Filled.InsertDriveFile,
                    stringResource(R.string.files_preview_too_large, FileManagerLogic.formatBytes(ui.fileSize)),
                )
                PreviewState.NeedsUpdate -> Message(
                    Icons.AutoMirrored.Filled.InsertDriveFile,
                    stringResource(R.string.files_preview_needs_update),
                )
                PreviewState.Details -> Message(
                    if (target?.isDirectory == true) Icons.Default.Folder else Icons.AutoMirrored.Filled.InsertDriveFile,
                    target?.name.orEmpty(),
                )
                PreviewState.Failed -> Column(
                    Modifier.fillMaxSize().padding(24.dp),
                    verticalArrangement = Arrangement.Center,
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Message(Icons.Default.ErrorOutline, stringResource(R.string.files_preview_failed), fill = false)
                    OutlinedButton(onClick = actions.onRetry, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                        Text(stringResource(R.string.files_preview_retry))
                    }
                }
            }
        }
        if (target != null) DetailsAndIntegrity(ui, target, actions)
    }
}

@Composable
private fun Message(icon: androidx.compose.ui.graphics.vector.ImageVector, text: String, fill: Boolean = true) {
    Column(
        modifier = (if (fill) Modifier.fillMaxSize() else Modifier).padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(48.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Pinch to zoom (around the fingers), drag to move, double-tap to fit again. */
@Composable
private fun ZoomableImage(image: ImageBitmap, description: String) {
    var scale by remember(image) { mutableFloatStateOf(1f) }
    var offset by remember(image) { mutableStateOf(Offset.Zero) }
    val state = rememberTransformableState { centroid, zoom, pan, _ ->
        val next = (scale * zoom).coerceIn(1f, 8f)
        // Scaled from the top-left corner (see transformOrigin below), so keep the point under the fingers still.
        offset = if (next == 1f) Offset.Zero else (offset - centroid) * (next / scale) + centroid + pan
        scale = next
    }
    Image(
        bitmap = image,
        contentDescription = description,
        contentScale = ContentScale.Fit,
        modifier = Modifier
            .fillMaxSize()
            .pointerInput(image) { detectTapGestures(onDoubleTap = { scale = 1f; offset = Offset.Zero }) }
            .transformable(state)
            .graphicsLayer {
                transformOrigin = TransformOrigin(0f, 0f)
                scaleX = scale
                scaleY = scale
                translationX = offset.x
                translationY = offset.y
            },
    )
}

@Composable
private fun TextBody(ui: PreviewUi<ImageBitmap>) {
    val listState = rememberLazyListState()
    val colors = syntaxColors()
    // A live tail stays at its newest line, like `tail -f`.
    LaunchedEffect(ui.live, ui.lines.size, ui.pendingLine) {
        if (ui.live && ui.lines.isNotEmpty()) listState.scrollToItem(ui.lines.size)
    }
    val numberWidth = (ui.lines.lastOrNull()?.number ?: 0).toString().length
    SelectionContainer {
        LazyColumn(state = listState, modifier = Modifier.fillMaxSize().padding(horizontal = 12.dp, vertical = 8.dp)) {
            items(ui.lines.size) { i ->
                val line = ui.lines[i]
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (line.number != null) {
                        Text(
                            text = line.number.toString().padStart(numberWidth),
                            style = MaterialTheme.typography.bodySmall,
                            fontFamily = FontFamily.Monospace,
                            color = MaterialTheme.colorScheme.outline,
                        )
                    }
                    Text(
                        text = colored(line, colors),
                        style = MaterialTheme.typography.bodySmall,
                        fontFamily = FontFamily.Monospace,
                    )
                }
            }
            if (ui.pendingLine.isNotEmpty()) {
                item {
                    Text(
                        ui.pendingLine,
                        style = MaterialTheme.typography.bodySmall,
                        fontFamily = FontFamily.Monospace,
                        fontStyle = FontStyle.Italic,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (ui.truncated) {
                item {
                    Text(
                        stringResource(R.string.files_preview_truncated),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 12.dp),
                    )
                }
            }
        }
    }
}

/** The theme's colour for each kind of stretch. M3 has no warning role, so warnings take the tertiary colour. */
@Composable
private fun syntaxColors(): Map<SyntaxKind, SpanStyle> {
    val c = MaterialTheme.colorScheme
    return mapOf(
        SyntaxKind.Comment to SpanStyle(color = c.outline, fontStyle = FontStyle.Italic),
        SyntaxKind.String to SpanStyle(color = c.tertiary),
        SyntaxKind.Number to SpanStyle(color = c.secondary),
        SyntaxKind.Keyword to SpanStyle(color = c.primary, fontWeight = FontWeight.SemiBold),
        SyntaxKind.Key to SpanStyle(color = c.primary),
        SyntaxKind.Timestamp to SpanStyle(color = c.outline),
        SyntaxKind.LogError to SpanStyle(color = c.error, fontWeight = FontWeight.SemiBold),
        SyntaxKind.LogWarning to SpanStyle(color = c.tertiary),
        SyntaxKind.LogDebug to SpanStyle(color = c.onSurfaceVariant),
    )
}

private fun colored(line: PreviewLine, colors: Map<SyntaxKind, SpanStyle>): AnnotatedString = buildAnnotatedString {
    append(line.text)
    for (span in line.spans) {
        val style = colors[span.kind] ?: continue
        val end = (span.start + span.length).coerceAtMost(line.text.length)
        if (span.start < end) addStyle(style, span.start, end)
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun DetailsAndIntegrity(ui: PreviewUi<ImageBitmap>, target: PreviewTarget, actions: PreviewActions) {
    var open by rememberSaveable(target.side, target.rootId, target.path) { mutableStateOf(false) }
    Surface(color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
        Column {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 48.dp)
                    .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = detailsLine(target, ui),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                if (actions.onOpenWith != null) {
                    IconButton(onClick = actions.onOpenWith, shapes = rememberRemexIconButtonShapes()) {
                        Icon(Icons.AutoMirrored.Filled.OpenInNew, contentDescription = stringResource(R.string.files_open_with))
                    }
                }
                if (actions.onSendAcross != null) {
                    IconButton(onClick = actions.onSendAcross, shapes = rememberRemexIconButtonShapes()) {
                        Icon(
                            Icons.Default.SwapHoriz,
                            contentDescription = stringResource(if (target.side == FileSide.Phone) R.string.files_send_to_pc else R.string.files_save_to_phone),
                        )
                    }
                }
                if (!target.isDirectory) {
                    IconButton(onClick = { open = !open }, shapes = rememberRemexIconButtonShapes()) {
                        Icon(
                            if (open) Icons.Default.ExpandMore else Icons.Default.ExpandLess,
                            contentDescription = stringResource(R.string.files_integrity_title),
                        )
                    }
                }
            }
            AnimatedVisibility(visible = open && !target.isDirectory) {
                Integrity(ui, target, actions)
            }
        }
    }
}

@Composable
private fun detailsLine(target: PreviewTarget, ui: PreviewUi<ImageBitmap>): String {
    val context = LocalContext.current
    val size = if (target.isDirectory) null else FileManagerLogic.formatBytes(if (ui.fileSize >= 0) ui.fileSize else target.sizeBytes)
    val modified = target.modifiedMs.takeIf { it > 0 }?.let {
        DateUtils.formatDateTime(context, it, DateUtils.FORMAT_SHOW_DATE or DateUtils.FORMAT_SHOW_TIME or DateUtils.FORMAT_ABBREV_MONTH)
    }
    val device = stringResource(if (target.side == FileSide.Phone) R.string.files_this_phone else R.string.files_pc)
    return listOfNotNull(device, size, modified).joinToString("  ·  ")
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun Integrity(ui: PreviewUi<ImageBitmap>, target: PreviewTarget, actions: PreviewActions) {
    var pasted by rememberSaveable(target.side, target.rootId, target.path) { mutableStateOf("") }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(max = 360.dp)
            .verticalScroll(rememberScrollState())
            .padding(start = 16.dp, end = 16.dp, bottom = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(stringResource(R.string.files_integrity_title), style = MaterialTheme.typography.titleSmallEmphasized)
        val hex = ui.hashHex
        when {
            ui.hashing -> Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                RemexLoadingIndicator(modifier = Modifier.size(32.dp))
                Text(stringResource(R.string.files_hashing), style = MaterialTheme.typography.bodyMedium)
            }
            hex != null -> Row(verticalAlignment = Alignment.CenterVertically) {
                SelectionContainer(Modifier.weight(1f)) {
                    Text(hex, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
                }
                IconButton(onClick = { actions.onCopyHash(hex) }, shapes = rememberRemexIconButtonShapes()) {
                    Icon(Icons.Default.ContentCopy, contentDescription = stringResource(R.string.file_manager_copy_sha256))
                }
            }
            !ui.canHash -> Text(
                stringResource(R.string.files_hash_needs_update),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            else -> {
                if (ui.hashFailed) Outcome(false, stringResource(R.string.files_hash_failed))
                FilledTonalButton(onClick = actions.onComputeHash, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                    Icon(Icons.Default.Fingerprint, contentDescription = null, modifier = Modifier.size(18.dp))
                    Text(stringResource(R.string.files_compute_hash), modifier = Modifier.padding(start = 8.dp))
                }
            }
        }
        if (hex != null) {
            OutlinedTextField(
                value = pasted,
                onValueChange = { pasted = it; actions.onCompare(it) },
                label = { Text(stringResource(R.string.files_compare_label)) },
                singleLine = true,
                textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                modifier = Modifier.fillMaxWidth(),
            )
            when (ui.compare) {
                HashCompareOutcome.Match -> Outcome(true, stringResource(R.string.files_compare_match))
                HashCompareOutcome.Mismatch -> Outcome(false, stringResource(R.string.files_compare_mismatch))
                HashCompareOutcome.NotAHash -> Outcome(false, stringResource(R.string.files_compare_not_a_hash))
                HashCompareOutcome.None -> Unit
            }
        }
        val other = stringResource(if (target.side == FileSide.Phone) R.string.files_pc else R.string.files_this_phone)
        // No button when either device can't work out a fingerprint: there'd be nothing to compare (spec §2.4).
        if (ui.canVerifyAcross && ui.verifying) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                RemexLoadingIndicator(modifier = Modifier.size(32.dp))
                Text(stringResource(R.string.files_verifying), style = MaterialTheme.typography.bodyMedium)
            }
        } else if (ui.canVerifyAcross) {
            OutlinedButton(onClick = actions.onVerifyAgainst, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                Text(stringResource(R.string.files_verify_against, other))
            }
        }
        when (ui.counterpart) {
            CounterpartOutcome.Identical -> Outcome(true, stringResource(R.string.files_identical))
            CounterpartOutcome.Different -> Outcome(false, stringResource(R.string.files_different))
            CounterpartOutcome.Failed -> Outcome(false, stringResource(R.string.files_verify_failed))
            CounterpartOutcome.None -> Unit
        }
    }
}

@Composable
private fun Outcome(good: Boolean, text: String) {
    val color: Color = if (good) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Icon(if (good) Icons.Default.CheckCircle else Icons.Default.ErrorOutline, contentDescription = null, tint = color, modifier = Modifier.size(20.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium, color = color, modifier = Modifier.widthIn(max = 480.dp))
    }
}
