package com.clindsay94.remex.ui.components

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertTouchHeightIsEqualTo
import androidx.compose.ui.test.assertTouchWidthIsEqualTo
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.RemoteFileEntry
import com.clindsay94.remex.ui.theme.RemExTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class FileManagerGridItemTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun gridOverflowButton_hasFortyEightDpTouchTarget() {
        composeTestRule.setContent {
            RemExTheme {
                FileManagerGridItem(
                    entry = RemoteFileEntry(name = "report.pdf", isDirectory = false, sizeBytes = 1024L),
                    isSelectionMode = false,
                    isSelected = false,
                    thumbnailBase64 = null,
                    showOverflow = true,
                    onRequestThumbnail = {},
                    onTap = {},
                    onLongPress = {},
                    onOverflow = {},
                )
            }
        }

        val overflowLabel = composeTestRule.activity.getString(R.string.cd_more_options)
        composeTestRule.onNodeWithContentDescription(overflowLabel)
            .assertTouchWidthIsEqualTo(48.dp)
            .assertTouchHeightIsEqualTo(48.dp)
    }
}
