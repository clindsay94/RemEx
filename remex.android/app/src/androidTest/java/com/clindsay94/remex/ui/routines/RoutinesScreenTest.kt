package com.clindsay94.remex.ui.routines

import androidx.activity.ComponentActivity
import androidx.annotation.StringRes
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toPixelMap
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Phone Routines UX (routines spec §13.7, §16 R-UX-07, 08, 09, 54, 58; RemEx-pp0rt.13). Hermetic:
 * the store is in memory (see [routinesViewModel]), nothing is scheduled, and no PC is connected.
 */
@RunWith(AndroidJUnit4::class)
class RoutinesScreenTest {
    @get:Rule
    val rule = createAndroidComposeRule<ComponentActivity>()

    private fun str(@StringRes id: Int, vararg args: Any): String = rule.activity.getString(id, *args)

    private fun showScreen(viewModel: RoutinesViewModel) {
        rule.setContent { RemExTheme { RoutinesScreen(onNavigateToConnection = {}, viewModel = viewModel) } }
        rule.waitForIdle()
    }

    private fun waitForText(text: String) {
        rule.waitUntil(5_000) { rule.onAllNodes(hasText(text)).fetchSemanticsNodes().isNotEmpty() }
    }

    private fun stepCard(index: Int, total: Int): SemanticsMatcher =
        SemanticsMatcher.expectValue(SemanticsProperties.StateDescription, str(R.string.routine_progress_step_count, index, total))

    // R-UX-07
    @Test
    fun emptyState_showsFeaturedTemplatesAndBothWaysIn() {
        val (viewModel, _) = rule.routinesViewModel()
        showScreen(viewModel)

        waitForText(str(R.string.routines_empty_title))
        rule.onNodeWithText(str(R.string.routines_empty_title)).assertIsDisplayed()
        rule.onNodeWithText(str(R.string.routines_browse_templates)).assertIsDisplayed()
        rule.onNodeWithText(str(R.string.routines_start_blank)).assertIsDisplayed()
        val featured = RoutineTemplates.featured(viewModel.hasNfc)
        assertEquals(3, featured.size)
        featured.forEach { template -> rule.onNodeWithText(str(template.nameRes)).performScrollTo().assertIsDisplayed() }
    }

    // R-UX-08 and the template half of 1.8: a template opens with its blanks already flagged.
    @Test
    fun template_opensEditorWithBlanksFlagged_andStoresNothing() {
        val (viewModel, repository) = rule.routinesViewModel()
        showScreen(viewModel)
        val template = RoutineTemplates.featured(viewModel.hasNfc).first { RoutineHomeRules.isHomeTrigger(it.trigger.type) }

        waitForText(str(template.nameRes))
        rule.onNodeWithText(str(template.nameRes)).performScrollTo().performClick()

        // No home is saved in the in-memory store, so the home trigger is a blank flagged before Save.
        waitForText(str(R.string.routines_problem_home_not_set))
        rule.onNodeWithText(str(R.string.routines_problem_home_not_set)).performScrollTo().assertIsDisplayed()
        assertTrue("a template stores nothing until Save", repository.routines.value.isEmpty())
    }

    // R-UX-09: Save with problems stays in the editor, says how many, and stores nothing.
    @Test
    fun blankEditor_saveWithProblems_isBlocked() {
        val (viewModel, repository) = rule.routinesViewModel()
        showScreen(viewModel)
        waitForText(str(R.string.routines_start_blank))
        rule.onNodeWithText(str(R.string.routines_start_blank)).performClick()
        rule.waitUntil(5_000) { rule.onAllNodes(hasContentDescription(str(R.string.routines_save))).fetchSemanticsNodes().isNotEmpty() }

        rule.onNodeWithContentDescription(str(R.string.routines_save)).performClick()

        val counts = (1..8).map { n -> rule.activity.resources.getQuantityString(R.plurals.routines_fix_count, n, n) }
        rule.waitUntil(5_000) { counts.any { rule.onAllNodes(hasText(it)).fetchSemanticsNodes().isNotEmpty() } }
        rule.onNodeWithText(str(R.string.routines_problem_no_steps)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(str(R.string.routines_new_routine)).assertIsDisplayed()
        assertTrue("Save with errors stores nothing", repository.routines.value.isEmpty())
    }

    // R-UX-58 / spec 1.8 "Discard changes?"
    @Test
    fun leavingEditorWithEdits_asksToDiscard() {
        val (viewModel, _) = rule.routinesViewModel()
        showScreen(viewModel)
        waitForText(str(R.string.routines_start_blank))
        rule.onNodeWithText(str(R.string.routines_start_blank)).performClick()
        waitForText(str(R.string.routines_name_label))
        rule.onNode(hasSetTextAction() and hasText(str(R.string.routines_name_label))).performTextInput("Movie night")

        rule.onNodeWithContentDescription(str(R.string.routines_close_editor)).performClick()
        rule.onNodeWithText(str(R.string.routines_discard_title)).assertIsDisplayed()

        // Keep editing stays put.
        rule.onNodeWithText(str(R.string.routines_keep_editing)).performClick()
        rule.waitForIdle()
        assertTrue(rule.onAllNodes(hasText(str(R.string.routines_discard_title))).fetchSemanticsNodes().isEmpty())
        rule.onNodeWithText("Movie night").assertIsDisplayed()

        // Discard leaves for the list.
        rule.onNodeWithContentDescription(str(R.string.routines_close_editor)).performClick()
        rule.onNodeWithText(str(R.string.routines_discard)).performClick()
        waitForText(str(R.string.routines_empty_title))
    }

    // Spec 1.10 / 6.7: Pause all toggles, shows the banner, and Resume clears it.
    @Test
    fun pauseAll_togglesAndShowsBanner() {
        val (viewModel, repository) = rule.routinesViewModel(manualRoutine(lockStep()))
        showScreen(viewModel)
        rule.waitUntil(5_000) { rule.onAllNodes(hasContentDescription(str(R.string.routines_pause_all))).fetchSemanticsNodes().isNotEmpty() }
        assertTrue(rule.onAllNodes(hasText(str(R.string.routines_paused_banner))).fetchSemanticsNodes().isEmpty())

        rule.onNodeWithContentDescription(str(R.string.routines_pause_all)).performClick()

        waitForText(str(R.string.routines_paused_banner))
        rule.onNodeWithText(str(R.string.routines_paused_banner)).assertIsDisplayed()
        rule.waitUntil(5_000) { repository.pausedAll.value }
        rule.onNodeWithContentDescription(str(R.string.routines_resume_all)).assertIsDisplayed()

        rule.onNodeWithContentDescription(str(R.string.routines_resume_all)).performClick()
        rule.waitUntil(5_000) { !repository.pausedAll.value }
        rule.waitUntil(5_000) { rule.onAllNodes(hasText(str(R.string.routines_paused_banner))).fetchSemanticsNodes().isEmpty() }
    }

    // R-UX-54: a step that discards work uses the error role; an ordinary one does not.
    @Test
    fun destructiveStep_isTintedWithErrorRole() {
        val routine = manualRoutine(lockStep(), shutdownStep())
        val (viewModel, _) = rule.routinesViewModel(routine)
        var errorContainer = Color.Unspecified
        var ordinary = Color.Unspecified
        rule.setContent {
            RemExTheme {
                errorContainer = MaterialTheme.colorScheme.errorContainer
                ordinary = MaterialTheme.colorScheme.surfaceContainerLow
                RoutineEditorPane(
                    viewModel = viewModel,
                    source = RoutineDetail.Editor(routineId = routine.id),
                    onClose = {},
                    onOpenHistory = {},
                    onNavigateToConnection = {},
                )
            }
        }
        rule.waitUntil(5_000) { rule.onAllNodes(stepCard(2, 2)).fetchSemanticsNodes().isNotEmpty() }

        assertColorNear(errorContainer, edgePixel(stepCard(2, 2)))
        assertColorNear(ordinary, edgePixel(stepCard(1, 2)))
    }

    // Accessibility: the list's key controls carry labels, and a routine card announces its state.
    @Test
    fun listControls_haveTalkBackDescriptions() {
        val routine = manualRoutine(lockStep(), shutdownStep())
        val (viewModel, _) = rule.routinesViewModel(routine)
        showScreen(viewModel)
        rule.waitUntil(5_000) { rule.onAllNodes(hasContentDescription(routine.name.orEmpty(), substring = true)).fetchSemanticsNodes().isNotEmpty() }

        rule.onNodeWithContentDescription(str(R.string.routines_pause_all)).assertExists()
        rule.onNodeWithContentDescription(str(R.string.routines_new_routine)).assertExists()
        rule.onNode(SemanticsMatcher.expectValue(SemanticsProperties.StateDescription, str(R.string.routines_state_on))).assertExists()
    }

    // Accessibility: the editor's close and save are labelled, and steps reorder without dragging.
    @Test
    fun editorControls_haveTalkBackDescriptions() {
        val routine = manualRoutine(lockStep(), shutdownStep())
        val (viewModel, _) = rule.routinesViewModel(routine)
        rule.setContent {
            RemExTheme {
                RoutineEditorPane(
                    viewModel = viewModel,
                    source = RoutineDetail.Editor(routineId = routine.id),
                    onClose = {},
                    onOpenHistory = {},
                    onNavigateToConnection = {},
                )
            }
        }
        rule.waitUntil(5_000) { rule.onAllNodes(stepCard(1, 2)).fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithContentDescription(str(R.string.routines_close_editor)).assertExists()
        rule.onNodeWithContentDescription(str(R.string.routines_save)).assertExists()
        // Reordering works without dragging: the first step offers Move down, the last Move up.
        assertCustomAction(stepCard(1, 2), str(R.string.routines_move_down))
        assertCustomAction(stepCard(2, 2), str(R.string.routines_move_up))
    }

    private fun assertCustomAction(node: SemanticsMatcher, label: String) {
        val actions = rule.onNode(node).fetchSemanticsNode().config.getOrElse(SemanticsActions.CustomActions) { emptyList() }
        assertTrue("custom action '$label' present, had ${actions.map { it.label }}", actions.any { it.label == label })
    }

    /** A pixel inside the card, left of its content padding, so it shows the container colour. */
    private fun edgePixel(node: SemanticsMatcher): Color {
        val pixels = rule.onNode(node).captureToImage().toPixelMap()
        return pixels[(4 * rule.density.density).toInt(), pixels.height / 2]
    }

    private fun assertColorNear(expected: Color, actual: Color) {
        val close = abs(expected.red - actual.red) < 0.03f && abs(expected.green - actual.green) < 0.03f && abs(expected.blue - actual.blue) < 0.03f
        assertTrue("expected $expected, was $actual", close)
    }
}
