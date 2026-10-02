package com.clindsay94.remex.ui.screens

import androidx.compose.runtime.Composable
import com.clindsay94.remex.ui.components.RemexSegmentedSwitch
import com.clindsay94.remex.ui.navigation.ControlSegment

/**
 * The Control tab (refresh spec, Control; RemEx-wqo7a.2): a Commands | Processes switch at the top,
 * hosting the two existing screens unchanged. Each keeps its own header, so the page title is
 * "Commands" or "Processes" while the tab is labelled "Control" (cohesion spec decision 1); the
 * switch sits directly under that header through their `headerAccessory` slot.
 *
 * Only the selected segment is composed, so Processes' auto-refresh runs only while it is the one
 * on screen. [segment] is owned by the navigation shell (rememberSaveable there), so the tab comes
 * back on the segment the user last chose.
 */
@Composable
fun ControlTabScreen(
        segment: ControlSegment,
        onSegmentChange: (ControlSegment) -> Unit,
        isVisible: Boolean,
        onNavigateToConnection: () -> Unit,
) {
    val segmentSwitch: @Composable () -> Unit = {
        RemexSegmentedSwitch(
                options = ControlSegment.entries,
                selected = segment,
                labelRes = { it.titleRes },
                onSelect = onSegmentChange,
        )
    }
    when (segment) {
        ControlSegment.Commands ->
                RemoteControlScreen(
                        onNavigateToConnection = onNavigateToConnection,
                        headerAccessory = segmentSwitch,
                )
        ControlSegment.Processes ->
                TaskManagerScreen(
                        onNavigateToConnection = onNavigateToConnection,
                        isVisible = isVisible,
                        headerAccessory = segmentSwitch,
                )
    }
}
