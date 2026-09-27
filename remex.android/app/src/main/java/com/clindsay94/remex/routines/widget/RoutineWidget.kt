package com.clindsay94.remex.routines.widget

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.Action
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.actionStartActivity
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.semantics.contentDescription
import androidx.glance.semantics.semantics
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.manual.RoutineConfirmActivity
import com.clindsay94.remex.routines.manual.RoutineManualEntry
import com.clindsay94.remex.routines.manual.RoutineManualSurfaces
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.ui.routines.RoutineRunViews
import java.text.DateFormat
import java.util.Date
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/** What one routine widget shows (spec A12, R-UX-25). */
internal sealed interface RoutineWidgetModel {
    /** Placed but no routine chosen yet (configuration is optional, so the launcher may skip it). */
    data class NotConfigured(val appWidgetId: Int) : RoutineWidgetModel

    /** The routine it ran was deleted, or no longer starts with Tap Run. */
    data object Removed : RoutineWidgetModel

    data class Ready(
        val routineId: String,
        val name: String,
        val running: Boolean,
        val destructive: Boolean,
        /** "Succeeded 18:02" style line, or null before the first run. */
        val lastResult: String?,
        val lastOk: Boolean,
    ) : RoutineWidgetModel
}

/**
 * The home-screen "Routine" widget (routines spec §8.3.3, A12): one `manual` routine, its last
 * result, and a Run button.
 *
 * **TAPS MUST WORK IN A RELEASE BUILD.** Run is an `actionRunCallback<RunRoutineWidgetAction>`, which
 * Glance instantiates reflectively by class name; `proguard-rules.pro` keeps `<init>()` on every
 * ActionCallback (bd memory `glance-actioncallback-r8-init`), and `RoutineWidgetReleaseShapeTest` pins
 * that this widget taps through such a callback. A routine with a destructive step instead opens
 * [RoutineConfirmActivity] directly from the tap, because an activity started from a broadcast would
 * be a background start.
 */
class RoutineWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val appWidgetId = GlanceAppWidgetManager(context).getAppWidgetId(id)
        val model = load(context, appWidgetId)
        provideContent { GlanceTheme { RoutineWidgetContent(model) } }
    }

    private suspend fun load(context: Context, appWidgetId: Int): RoutineWidgetModel =
        try {
            val routineId = Routines.secrets(context).widgetRoutine(appWidgetId) ?: return RoutineWidgetModel.NotConfigured(appWidgetId)
            val repository = Routines.repository(context)
            repository.load()
            val routine = repository.routines.value.firstOrNull { it.routine.id == routineId }?.routine
            if (!RoutineManualEntry.isPinnable(routine) || routine == null) {
                RoutineWidgetModel.Removed
            } else {
                val last = repository.history.value.firstOrNull { it.routineId == routineId && it.outcome != RoutineRunOutcomes.RUNNING }
                val time = last?.let { DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(it.triggeredAtUnixMs)) }
                RoutineWidgetModel.Ready(
                    routineId = routineId,
                    name = routine.name.orEmpty(),
                    running = repository.activeRuns.value.containsKey(routineId),
                    destructive = routine.steps.orEmpty().any { it?.isDestructive == true },
                    lastResult = last?.let { run -> context.getString(R.string.widget_routine_last, outcomeText(context, run), time) },
                    lastOk = last?.outcome == RoutineRunOutcomes.SUCCEEDED,
                )
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Loading a routine widget failed.", e)
            RoutineWidgetModel.Removed
        }

    private fun outcomeText(context: Context, run: RoutineRun): String =
        if (run.outcome == RoutineRunOutcomes.SUCCEEDED) {
            context.getString(R.string.routines_outcome_steps_done, RoutineRunViews.succeededSteps(run), RoutineRunViews.totalSteps(run))
        } else {
            RoutineReasonText.history(context, run.reasonCode, run.reasonArgs ?: RoutineReasonArgs())
        }

    companion object {
        private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        /** Redraws every routine widget; cheap to call after runs and edits. */
        fun refreshAll(context: Context) {
            val app = context.applicationContext
            scope.launch {
                try {
                    RoutineWidget().updateAll(app)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    RoutineLog.w("Refreshing routine widgets failed.", e)
                }
            }
        }

        /**
         * "Add widget" from a routine's menu (spec 1.6 "Pin"): asks the launcher to place one, bound to
         * [routineId] once the launcher reports the new widget's id. False when it cannot.
         */
        fun requestPin(context: Context, routineId: String): Boolean {
            val manager = AppWidgetManager.getInstance(context)
            if (!manager.isRequestPinAppWidgetSupported) return false
            val callback =
                android.app.PendingIntent.getBroadcast(
                    context,
                    routineId.hashCode(),
                    Intent(context, RoutineWidgetReceiver::class.java)
                        .setAction(RoutineWidgetReceiver.ACTION_PINNED)
                        .putExtra(RoutineWidgetReceiver.EXTRA_ROUTINE_ID, routineId),
                    // Mutable so the launcher can add EXTRA_APPWIDGET_ID; explicit, so nothing else can
                    // receive it.
                    android.app.PendingIntent.FLAG_UPDATE_CURRENT or android.app.PendingIntent.FLAG_MUTABLE,
                )
            return manager.requestPinAppWidget(ComponentName(context, RoutineWidgetReceiver::class.java), null, callback)
        }
    }
}

@Composable
private fun RoutineWidgetContent(model: RoutineWidgetModel) {
    val context = androidx.glance.LocalContext.current
    when (model) {
        is RoutineWidgetModel.NotConfigured ->
            Message(
                text = context.getString(R.string.widget_routine_choose),
                action =
                    actionStartActivity(
                        Intent(context, RoutineWidgetConfigActivity::class.java)
                            .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, model.appWidgetId)
                            .putExtra(RoutineWidgetConfigActivity.EXTRA_FROM_WIDGET, true),
                    ),
            )
        RoutineWidgetModel.Removed -> Message(context.getString(R.string.widget_routine_removed), actionStartActivity<MainActivity>())
        is RoutineWidgetModel.Ready -> ReadyContent(model)
    }
}

@Composable
private fun Message(text: String, action: Action) {
    Box(
        modifier = GlanceModifier.fillMaxSize().background(GlanceTheme.colors.surface).cornerRadius(16.dp).clickable(action).padding(12.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp), maxLines = 3)
    }
}

@Composable
private fun ReadyContent(model: RoutineWidgetModel.Ready) {
    val context = androidx.glance.LocalContext.current
    val size = LocalSize.current
    val run: Action =
        if (model.destructive) {
            actionStartActivity(RoutineConfirmActivity.intentFor(context, model.routineId, RoutineRunSources.MANUAL_WIDGET))
        } else {
            actionRunCallback<RunRoutineWidgetAction>(actionParametersOf(RunRoutineWidgetAction.ROUTINE_ID to model.routineId))
        }
    val runLabel = context.getString(if (model.running) R.string.widget_routine_running else R.string.routines_run)
    val tap = GlanceModifier.clickable(run).semantics { contentDescription = context.getString(R.string.widget_routine_run_cd, model.name) }

    if (size.width < 150.dp) {
        // 1x1: the icon is the button, the name under it.
        Column(
            modifier = GlanceModifier.fillMaxSize().background(GlanceTheme.colors.surface).cornerRadius(16.dp).padding(8.dp).then(tap),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconBadge(model.running)
            Text(
                model.name,
                maxLines = 1,
                style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 12.sp, fontWeight = FontWeight.Medium),
            )
        }
        return
    }
    Row(
        modifier = GlanceModifier.fillMaxSize().background(GlanceTheme.colors.surface).cornerRadius(16.dp).padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconBadge(model.running)
        Spacer(GlanceModifier.width(10.dp))
        Column(modifier = GlanceModifier.defaultWeight()) {
            Text(model.name, maxLines = 1, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 14.sp, fontWeight = FontWeight.Medium))
            Text(
                model.lastResult ?: context.getString(R.string.routines_never_run),
                maxLines = 1,
                style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp),
            )
        }
        Spacer(GlanceModifier.width(8.dp))
        Box(
            modifier =
                GlanceModifier.background(if (model.running) GlanceTheme.colors.secondaryContainer else GlanceTheme.colors.primary)
                    .cornerRadius(20.dp)
                    .padding(horizontal = 16.dp, vertical = 8.dp)
                    .then(tap),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                runLabel,
                style =
                    TextStyle(
                        color = if (model.running) GlanceTheme.colors.onSecondaryContainer else GlanceTheme.colors.onPrimary,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                    ),
            )
        }
    }
}

/** The routine glyph in its container; `primaryContainer` while running (spec A12). */
@Composable
private fun IconBadge(running: Boolean) {
    Box(
        modifier =
            GlanceModifier.size(40.dp)
                .background(if (running) GlanceTheme.colors.primaryContainer else GlanceTheme.colors.secondaryContainer)
                .cornerRadius(20.dp),
        contentAlignment = Alignment.Center,
    ) {
        Image(
            provider = ImageProvider(R.drawable.ic_routine_play),
            contentDescription = null,
            colorFilter = ColorFilter.tint(if (running) GlanceTheme.colors.onPrimaryContainer else GlanceTheme.colors.onSecondaryContainer),
            modifier = GlanceModifier.size(24.dp),
        )
    }
}

/**
 * The widget's Run (R-UX-25). A no-argument class: Glance creates it reflectively, and release keeps
 * its constructor through the ActionCallback keep rule. It re-reads the routine and decides again, so
 * a widget drawn before the routine gained a destructive step still confirms (it then tries the
 * confirm activity, which a widget tap is allowed to open).
 */
class RunRoutineWidgetAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val routineId = parameters[ROUTINE_ID] ?: return
        RoutineManualSurfaces.start(context, routineId, RoutineRunSources.MANUAL_WIDGET)
        RoutineWidget.refreshAll(context)
    }

    companion object {
        val ROUTINE_ID = ActionParameters.Key<String>("routine_id")
    }
}

/**
 * NOT exported (spec T1): the system's AppWidget service still reaches it, because it sends as the
 * system, and nothing else needs to. Also receives the launcher's "widget placed" callback for a
 * widget pinned from a routine's menu.
 */
class RoutineWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = RoutineWidget()

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION_PINNED) {
            val routineId = runCatching { intent.getStringExtra(EXTRA_ROUTINE_ID) }.getOrNull()
            val appWidgetId = intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID)
            if (routineId == null || appWidgetId == AppWidgetManager.INVALID_APPWIDGET_ID) return
            val pending = goAsync()
            receiverScope.launch {
                try {
                    Routines.secrets(context).setWidgetRoutine(appWidgetId, routineId)
                    RoutineWidget().updateAll(context.applicationContext)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    RoutineLog.e("Binding a pinned routine widget failed.", e)
                } finally {
                    pending.finish()
                }
            }
            return
        }
        super.onReceive(context, intent)
    }

    override fun onDeleted(context: Context, appWidgetIds: IntArray) {
        super.onDeleted(context, appWidgetIds)
        val app = context.applicationContext
        receiverScope.launch {
            appWidgetIds.forEach { id ->
                try {
                    Routines.secrets(app).removeWidget(id)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    RoutineLog.w("Forgetting a removed routine widget failed.", e)
                }
            }
        }
    }

    companion object {
        const val ACTION_PINNED = "com.clindsay94.remex.routines.WIDGET_PINNED"
        const val EXTRA_ROUTINE_ID = "com.clindsay94.remex.routines.WIDGET_ROUTINE_ID"
        private val receiverScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    }
}
