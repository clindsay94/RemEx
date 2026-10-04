package com.clindsay94.remex.ui.screens.sensors

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexSegmentedSwitch

/** The label shown on each [GridWidth] segment. */
private fun GridWidth.labelRes(): Int =
        when (this) {
            GridWidth.AUTO -> R.string.dashboard_grid_width_auto
            GridWidth.TWO -> R.string.dashboard_grid_width_2
            GridWidth.THREE -> R.string.dashboard_grid_width_3
            GridWidth.FOUR -> R.string.dashboard_grid_width_4
        }

/**
 * "Grid width": Auto, 2, 3 or 4 columns, a segmented control in the Sensors edit-mode toolbar
 * (RemEx-pp4cm.16). Auto is the width-class rule the grid always had; the others pin the count.
 */
@Composable
fun GridWidthPicker(selected: GridWidth, onSelect: (GridWidth) -> Unit, modifier: Modifier = Modifier) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(
                stringResource(R.string.dashboard_grid_width),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.semantics { heading() }
        )
        RemexSegmentedSwitch(
                options = GridWidth.entries,
                selected = selected,
                labelRes = { it.labelRes() },
                onSelect = onSelect,
                contentPadding = PaddingValues(0.dp),
        )
    }
}
