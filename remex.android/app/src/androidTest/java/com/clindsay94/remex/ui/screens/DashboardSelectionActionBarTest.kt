package com.clindsay94.remex.ui.screens

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertTouchHeightIsEqualTo
import androidx.compose.ui.test.assertTouchWidthIsEqualTo
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class DashboardSelectionActionBarTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun allFourActions_haveFortyEightDpTouchTargets() {
        composeTestRule.setContent {
            RemExTheme {
                DashboardSelectionActionBar(
                    selectionCount = 2,
                    allPinned = false,
                    onTogglePin = {},
                    onReshape = {},
                    onRemove = {},
                    onDone = {}
                )
            }
        }

        val labels = listOf(
            R.string.cd_dashboard_pin_selection,
            R.string.cd_dashboard_reshape_selection,
            R.string.cd_dashboard_remove_selection,
            R.string.cd_dashboard_exit_selection
        )
        for (labelRes in labels) {
            val label = composeTestRule.activity.getString(labelRes)
            composeTestRule.onNodeWithContentDescription(label)
                .assertTouchWidthIsEqualTo(48.dp)
                .assertTouchHeightIsEqualTo(48.dp)
        }
    }
}
