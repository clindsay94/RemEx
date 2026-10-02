package com.clindsay94.remex.ui.screens.sensors

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Text
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertTouchHeightIsEqualTo
import androidx.compose.ui.test.assertTouchWidthIsEqualTo
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The Sensors grid card's edit-mode controls are real 48dp touch targets on a one-cell card, and
 * there are none outside edit mode (RemEx-wqo7a.8). Replaces the free-form canvas's pin-badge check.
 */
@RunWith(AndroidJUnit4::class)
class SensorGridCardTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    private fun setCard(editMode: Boolean) {
        composeTestRule.setContent {
            RemExTheme {
                SensorGridCard(
                        shape = RoundedCornerShape(16.dp),
                        cardOpacity = 1f,
                        editMode = editMode,
                        isDragging = false,
                        pin = CardPinControl(pinned = false, canPin = true),
                        onTogglePin = {},
                        onPinUnavailable = {},
                        onCycleSize = {},
                        onRemove = {},
                        modifier = Modifier.size(170.dp, 145.dp),
                ) { Text("CPU") }
            }
        }
    }

    @Test
    fun editControls_haveFortyEightDpTouchTargets() {
        setCard(editMode = true)
        listOf(R.string.dashboard_pin_to_home, R.string.dashboard_resize_card, R.string.dashboard_remove_card).forEach { res ->
            composeTestRule.onNodeWithContentDescription(composeTestRule.activity.getString(res))
                    .assertTouchWidthIsEqualTo(48.dp)
                    .assertTouchHeightIsEqualTo(48.dp)
        }
    }

    @Test
    fun noControlsOutsideEditMode() {
        setCard(editMode = false)
        listOf(R.string.dashboard_pin_to_home, R.string.dashboard_resize_card, R.string.dashboard_remove_card).forEach { res ->
            composeTestRule.onNodeWithContentDescription(composeTestRule.activity.getString(res)).assertDoesNotExist()
        }
    }
}
