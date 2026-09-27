package com.clindsay94.remex.ui.routines

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexTooltip

/** The template gallery (routines spec A3): filter chips, then cards; a grid on wide panes. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RoutineTemplatesPane(
    viewModel: RoutinesViewModel,
    showBack: Boolean,
    onBack: () -> Unit,
    onPick: (RoutineTemplate) -> Unit,
) {
    val status by viewModel.status.collectAsStateWithLifecycle()
    val selectedMac by viewModel.selectedMac.collectAsStateWithLifecycle()
    val launcher by viewModel.launcher.collectAsStateWithLifecycle()
    val selectedPc by viewModel.selectedPc.collectAsStateWithLifecycle()
    val mediaKeys by viewModel.mediaKeys.collectAsStateWithLifecycle()
    var filter by rememberSaveable { mutableStateOf<String?>(null) }
    val categories = RoutineTemplates.categories()
    val templates = RoutineTemplates.offered().filter { filter == null || it.category.name == filter }
    val canEdit = !status.readOnly

    fun satisfied(requirement: RoutineRequirement): Boolean? =
        when (requirement) {
            RoutineRequirement.MAC_ADDRESS -> selectedMac != null
            RoutineRequirement.LAUNCHER_ENTRY -> launcher.first == selectedPc && !launcher.second.isNullOrEmpty()
            // Known only for the connected PC; a definite "no" shows the token as missing (spec 4.2).
            RoutineRequirement.MEDIA_KEYS -> mediaKeys.second.takeIf { mediaKeys.first != null && mediaKeys.first == selectedPc }
        }

    Scaffold(
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.routines_templates_title),
                navigationIcon = {
                    if (showBack) {
                        val back = stringResource(R.string.cd_back)
                        RemexTooltip(back) { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = back) } }
                    }
                },
            )
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            Row(
                Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                FilterChip(selected = filter == null, onClick = { filter = null }, label = { Text(stringResource(R.string.routines_category_all)) })
                categories.forEach { category ->
                    FilterChip(
                        selected = filter == category.name,
                        onClick = { filter = category.name },
                        label = { Text(stringResource(category.labelRes)) },
                    )
                }
            }
            LazyVerticalGrid(
                columns = GridCells.Adaptive(320.dp),
                contentPadding = PaddingValues(16.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(templates, key = { it.id }) { template ->
                    TemplateCard(
                        template = template,
                        compact = false,
                        enabled = canEdit,
                        satisfied = ::satisfied,
                        onClick = { onPick(template) },
                        modifier = Modifier.animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec()),
                    )
                }
            }
        }
    }
}

/**
 * A template card: icon, name, the "why" line, its chain and (full cards) the "Needs" tokens.
 * Each token is its own text, never glued into a sentence (R-UX-56).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun TemplateCard(
    template: RoutineTemplate,
    compact: Boolean,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    satisfied: (RoutineRequirement) -> Boolean? = { null },
) {
    val name = stringResource(template.nameRes)
    Card(
        onClick = onClick,
        enabled = enabled,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
        modifier = modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(triggerIcon(template.trigger.type), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.size(12.dp))
                Text(name, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            if (!compact) {
                Text(stringResource(template.whyRes), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            RoutineChipChain(triggerType = template.trigger.type, steps = template.steps.map { it.step }, maxSteps = if (compact) 3 else 4)
            if (!compact && template.needs.isNotEmpty()) {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp), itemVerticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.routines_needs_label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    template.needs.forEach { need ->
                        val missing = satisfied(need) == false
                        val label = stringResource(need.labelRes)
                        val missingLabel = stringResource(R.string.routines_need_missing, label)
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier.semantics { contentDescription = if (missing) missingLabel else label },
                        ) {
                            if (missing) {
                                Icon(Icons.Default.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(16.dp))
                                Spacer(Modifier.size(4.dp))
                            }
                            Text(label, style = MaterialTheme.typography.labelMedium)
                        }
                    }
                }
            }
        }
    }
}
