package com.clindsay94.remex.ui.screens

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class DisplayModePickerSheetTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    private fun setContent(showValueOverlay: Boolean, onSetValueOverlay: (String, Boolean) -> Unit) {
        composeTestRule.setContent {
            RemExTheme {
                DisplayModePickerSheet(
                    cardId = "sensor:cpu",
                    sensor = null,
                    history = emptyList(),
                    currentMode = TelemetryDisplayMode.AUTO,
                    currentTitle = "CPU",
                    currentShowValueOverlay = showValueOverlay,
                    otherSensors = emptyList(),
                    onDismiss = {},
                    onPickDisplayMode = { _, _, _ -> },
                    onSetTitle = { _, _ -> },
                    onSetValueOverlay = onSetValueOverlay
                )
            }
        }
    }

    @Test
    fun valueOverlayRow_onState_isToggleableAndOn() {
        setContent(showValueOverlay = true, onSetValueOverlay = { _, _ -> })

        composeTestRule.onNodeWithText(
            composeTestRule.activity.getString(R.string.dashboard_show_value_overlay)
        ).assertIsOn()
    }

    @Test
    fun valueOverlayRow_offState_isToggleableAndOff() {
        setContent(showValueOverlay = false, onSetValueOverlay = { _, _ -> })

        composeTestRule.onNodeWithText(
            composeTestRule.activity.getString(R.string.dashboard_show_value_overlay)
        ).assertIsOff()
    }

    @Test
    fun valueOverlayRow_click_reportsToggledValue() {
        var reportedCardId: String? = null
        var reportedEnabled: Boolean? = null
        setContent(showValueOverlay = false, onSetValueOverlay = { cardId, enabled ->
            reportedCardId = cardId
            reportedEnabled = enabled
        })

        composeTestRule.onNodeWithText(
            composeTestRule.activity.getString(R.string.dashboard_show_value_overlay)
        ).performClick()

        assertEquals("sensor:cpu", reportedCardId)
        assertEquals(true, reportedEnabled)
    }
}
