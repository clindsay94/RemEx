package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

/**
 * The tab-level switch the Desktop (Stream | Trackpad) and Control (Commands | Processes) tabs
 * put under their header (RemEx-wqo7a.2): an M3 single-choice segmented button row, full width
 * with the page's 16.dp side margin. Two short labels each, so the segmented row fits on a phone;
 * the five-option style picker in Personalization stays chips for the reason noted there.
 *
 * Colours come from [SegmentedButtonDefaults], i.e. the scheme's secondaryContainer and outline
 * roles, so it follows custom seed, dynamic colour and the static fallback alike.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun <T> RemexSegmentedSwitch(
        options: List<T>,
        selected: T,
        /** The string resource id of each option's label. */
        labelRes: (T) -> Int,
        onSelect: (T) -> Unit,
        modifier: Modifier = Modifier,
) {
    val haptics = rememberRemexHaptics()
    SingleChoiceSegmentedButtonRow(
            modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)
    ) {
        options.forEachIndexed { index, option ->
            SegmentedButton(
                    selected = option == selected,
                    onClick = {
                        if (option != selected) {
                            haptics.perform(RemexHapticEvent.Select)
                            onSelect(option)
                        }
                    },
                    shape = SegmentedButtonDefaults.itemShape(index = index, count = options.size),
                    label = {
                        Text(
                                stringResource(labelRes(option)),
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                        )
                    },
            )
        }
    }
}
