package com.clindsay94.remex.ui.screens

import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.ui.theme.RemExTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ViewPickerCellTest {
    @get:Rule
    val composeTestRule = createComposeRule()

    @Test
    fun selectedCell_exposesSelectedSemantics() {
        composeTestRule.setContent {
            RemExTheme {
                ViewPickerCell(label = "Selected mode", selected = true, onClick = {}) {}
            }
        }
        composeTestRule.onNodeWithText("Selected mode").assertIsSelected()
    }

    @Test
    fun unselectedCell_exposesNotSelectedSemantics() {
        composeTestRule.setContent {
            RemExTheme {
                ViewPickerCell(label = "Unselected mode", selected = false, onClick = {}) {}
            }
        }
        composeTestRule.onNodeWithText("Unselected mode").assertIsNotSelected()
    }
}
