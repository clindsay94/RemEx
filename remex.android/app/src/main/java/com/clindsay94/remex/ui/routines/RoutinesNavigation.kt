package com.clindsay94.remex.ui.routines

import android.content.Intent
import android.os.Parcelable
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.clindsay94.remex.routines.RoutineNotificationPresenter
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.parcelize.Parcelize

/**
 * What the Routines detail pane shows (routines spec 2.1). These are the spec's
 * `RoutineTemplates`, `RoutineEditorRoute`, `RoutineHistoryRoute` and `RoutineRunRoute`, carried as
 * the content key of one `NavigableListDetailPaneScaffold` rather than as separate NavHost routes:
 * the scaffold then gives a phone one pane at a time and a tablet the list beside the detail
 * (spec 2.4, A13) from one code path, the way Settings does.
 */
sealed class RoutineDetail : Parcelable {
    @Parcelize
    data object Templates : RoutineDetail()

    /** Both null = blank (spec 2.1). */
    @Parcelize
    data class Editor(val routineId: String? = null, val templateId: String? = null) : RoutineDetail()

    /** null = every run. */
    @Parcelize
    data class History(val routineId: String? = null) : RoutineDetail()

    @Parcelize
    data class Run(val runId: String) : RoutineDetail()
}

/**
 * A routine notification's "Open" / "See what happened" (S1c `RoutineNotificationPresenter`) asks
 * MainActivity to show a run. MainActivity hands the intent here; AppNavigation opens Routines and
 * the screen consumes the request and shows the run detail.
 */
object RoutineOpenRequests {
    /**
     * @property notice a fixed notice code for the routines list (never text from the intent: any
     *   app can start MainActivity, so it may only choose among RemEx's own messages).
     */
    data class Request(val routineId: String?, val runId: String?, val notice: String? = null)

    /** Opens the routines list with a notice (a stale shortcut, spec §8.3.3). */
    const val EXTRA_OPEN_LIST_NOTICE = "com.clindsay94.remex.routines.OPEN_LIST_NOTICE"
    const val NOTICE_SHORTCUT_INVALID = "shortcut_invalid"
    private val NOTICES = setOf(NOTICE_SHORTCUT_INVALID)

    private val _pending = MutableStateFlow<Request?>(null)
    val pending: StateFlow<Request?> = _pending.asStateFlow()

    /** True when [intent] carried a routine request (it is then remembered until consumed). */
    fun offer(intent: Intent?): Boolean {
        if (intent == null) return false
        // MainActivity is exported (launcher), so any app can start it with extras. Reading ANY
        // extra unparcels the whole bundle, and below API 33 a malformed or unknown Parcelable in it
        // throws (BadParcelableException and friends) from onCreate: a crash on launch. A request
        // we cannot read is simply not a request.
        val request =
            try {
                val runId = intent.getStringExtra(RoutineNotificationPresenter.EXTRA_OPEN_RUN_ID)
                val routineId = intent.getStringExtra(RoutineNotificationPresenter.EXTRA_OPEN_ROUTINE_ID)
                val notice = intent.getStringExtra(EXTRA_OPEN_LIST_NOTICE)?.takeIf { it in NOTICES }
                if (runId == null && routineId == null && notice == null) return false
                // Consumed once: a later configuration change re-delivering the intent must not reopen it.
                intent.removeExtra(RoutineNotificationPresenter.EXTRA_OPEN_RUN_ID)
                intent.removeExtra(RoutineNotificationPresenter.EXTRA_OPEN_ROUTINE_ID)
                intent.removeExtra(EXTRA_OPEN_LIST_NOTICE)
                Request(routineId, runId, notice)
            } catch (e: RuntimeException) {
                Log.w("RoutineOpenRequests", "Ignoring an intent whose extras could not be read.", e)
                return false
            }
        _pending.value = request
        return true
    }

    fun consume(): Request? = _pending.value.also { _pending.value = null }
}

/**
 * The compact editor hides the NavigationBar so its floating toolbar owns the bottom edge (spec
 * 2.1). Snapshot state rather than a route, because the editor is a pane inside [RoutineDetail],
 * not a NavHost destination; AppNavigation reads it only while Routines is the current route.
 */
object RoutineEditorChrome {
    var editorShowing by mutableStateOf(false)
}
