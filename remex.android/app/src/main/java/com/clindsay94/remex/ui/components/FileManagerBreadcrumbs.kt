package com.clindsay94.remex.ui.components

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.ui.screens.Breadcrumb

/**
 * Horizontally scrollable, tappable breadcrumb trail (plan WP7). The first crumb is the root; each
 * later crumb jumps straight to that ancestor path. The final (current) crumb is emphasised and inert.
 */
@Composable
fun FileManagerBreadcrumbs(
    crumbs: List<Breadcrumb>,
    onNavigate: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val scrollState = rememberScrollState()
    val trailSizeSpec = MaterialTheme.motionScheme.defaultSpatialSpec<IntSize>()
    val scrollSpec = MaterialTheme.motionScheme.defaultSpatialSpec<Float>()
    // Bring the current crumb into view whenever the trail grows or shrinks — descending
    // into a deep path used to push the current folder silently off-screen (RemEx-dkr6).
    LaunchedEffect(crumbs.size) {
        scrollState.animateScrollTo(scrollState.maxValue, scrollSpec)
    }
    Row(
        modifier = modifier.horizontalScroll(scrollState),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(0.dp),
    ) {
        // The size animation lives on this inner row: inside the scrollable its width is
        // unbounded, so appended/removed crumbs animate instead of snapping the trail.
        Row(
            modifier = Modifier.animateContentSize(animationSpec = trailSizeSpec),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(0.dp),
        ) {
        crumbs.forEachIndexed { index, crumb ->
            val isLast = index == crumbs.lastIndex
            if (isLast) {
                Text(
                    text = crumb.label,
                    style = MaterialTheme.typography.labelLargeEmphasized,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.padding(horizontal = 8.dp),
                )
            } else {
                TextButton(onClick = { onNavigate(crumb.path) }) {
                    Text(
                        text = crumb.label,
                        style = MaterialTheme.typography.labelLarge,
                        color = MaterialTheme.colorScheme.primary,
                    )
                }
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        }
    }
}
