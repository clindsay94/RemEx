package com.clindsay94.remex.ui.components

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.longClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileViewMode
import com.clindsay94.remex.ui.screens.SortField
import com.clindsay94.remex.ui.screens.SortOption
import com.clindsay94.remex.ui.theme.RemExTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * RemEx-31wq: icon-only toolbar buttons must show a visible label on long-press (M3 tooltip
 * requirement). Long-presses the toolbar's search icon and asserts the tooltip text appears.
 */
@RunWith(AndroidJUnit4::class)
class FileManagerToolbarTooltipTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun searchIcon_longPress_showsTooltipLabel() {
        composeTestRule.setContent {
            RemExTheme {
                FileManagerToolbar(
                    searchQuery = "",
                    onSearchChange = {},
                    sortOption = SortOption(SortField.NAME, ascending = true),
                    onSort = {},
                    viewMode = FileViewMode.LIST,
                    onToggleViewMode = {},
                    canWrite = true,
                    onNewFolder = {},
                    onUpload = {},
                )
            }
        }

        val label = composeTestRule.activity.getString(R.string.file_manager_search)
        composeTestRule.onNodeWithContentDescription(label).performTouchInput { longClick() }
        composeTestRule.waitForIdle()
        // The tooltip renders the same string as visible text in a popup.
        composeTestRule.onNodeWithText(label).assertExists()
    }
}
