package com.clindsay94.remex.ui.screens

import androidx.annotation.StringRes
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import kotlin.math.roundToInt

/** Which tip each first-visit step of the Files screen shows, kept out of the composable so it can be tested. */
internal object FilesCoach {
    const val STEP_COUNT = 2

    fun isLast(step: Int): Boolean = step >= STEP_COUNT - 1

    /** Only the narrow layout hides the folder list behind a button worth circling. */
    fun ringsNavIcon(step: Int, expanded: Boolean): Boolean = step <= 0 && !expanded

    @StringRes
    fun bodyRes(step: Int, expanded: Boolean): Int = when {
        step >= 1 -> R.string.files_coach_select
        expanded -> R.string.files_coach_tree_expanded
        else -> R.string.files_coach_tree_compact
    }
}

@Composable
internal fun FilesCoachOverlay(
    step: Int,
    expanded: Boolean,
    navIcon: Rect,
    onAdvance: () -> Unit,
    onDismiss: () -> Unit,
) {
    Box(Modifier.fillMaxSize()) {
        // A non-clickable Surface swallows touches, so the list cannot be used mid-hint and a stray
        // tap on the scrim does not lose the tips (the Dashboard coach's rule).
        Surface(color = MaterialTheme.colorScheme.scrim.copy(alpha = 0.62f), modifier = Modifier.fillMaxSize()) {}
        if (FilesCoach.ringsNavIcon(step, expanded) && navIcon != Rect.Zero) {
            val density = LocalDensity.current
            val pad = with(density) { 6.dp.toPx() }
            Box(
                Modifier.offset { IntOffset((navIcon.left - pad).roundToInt(), (navIcon.top - pad).roundToInt()) }
                    .size(with(density) { (navIcon.width + pad * 2).toDp() }, with(density) { (navIcon.height + pad * 2).toDp() })
                    .border(3.dp, MaterialTheme.colorScheme.primary, CircleShape),
            )
        }
        CoachPanel(
            body = stringResource(FilesCoach.bodyRes(step, expanded)),
            isLast = FilesCoach.isLast(step),
            onAdvance = onAdvance,
            onDismiss = onDismiss,
            modifier =
                Modifier.align(Alignment.BottomCenter)
                    .navigationBarsPadding()
                    .widthIn(max = 560.dp)
                    .padding(horizontal = 20.dp, vertical = 28.dp),
        )
    }
}
