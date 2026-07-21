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
class DraggableDashboardCardTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun pinToggle_hasFortyEightDpTouchTarget() {
        composeTestRule.setContent {
            RemExTheme {
                DraggableDashboardCard(
                    card = HomeCardState(id = "test-card", title = "Test", type = HomeCardType.PC_STATUS),
                    selectionActive = false,
                    isSelected = false,
                    isDragging = false,
                    layoutLocked = false,
                    density = 1f,
                    cornerRadius = 16,
                    cardOpacity = 1f,
                    shapeIndex = 0f,
                    canvasScale = { 1f },
                    onBeginCardDrag = {},
                    onDragCardBy = { _, _ -> },
                    onEndCardDrag = {},
                    onSelectCard = {},
                    onToggleSelect = {},
                    onBeginInteraction = {},
                    onMoveSelection = { _, _ -> },
                    onSaveLayout = {},
                    onTogglePin = {},
                    onResize = { _, _, _ -> },
                ) {}
            }
        }

        val pinLabel = composeTestRule.activity.getString(R.string.dashboard_pin_card)
        composeTestRule.onNodeWithContentDescription(pinLabel)
            .assertTouchWidthIsEqualTo(48.dp)
            .assertTouchHeightIsEqualTo(48.dp)
    }
}
