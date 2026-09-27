package com.clindsay94.remex.routines.manual

import android.content.Context
import android.content.Intent
import android.widget.Toast
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.RoutineRunStart
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** What a person-initiated surface does with one routine (routines spec §8.3.3, R-SYS-18). */
enum class RoutineManualDecision {
    /** Start it now. */
    RUN,

    /** It has a destructive step: the translucent confirm comes first (spec 1.7, L12). */
    CONFIRM,

    /** No such routine on this phone (deleted, or a stale shortcut): nothing runs. */
    NOT_FOUND,

    /** The routine exists but this surface does not start it (a shortcut for a routine that no longer starts with Tap Run). */
    WRONG_TRIGGER,
}

/**
 * The one decision every manual surface makes (pure JVM, proven by `RoutineManualSurfacesTest`).
 * Shortcuts and widgets start `manual` routines only; an NFC tap starts `nfc.tap` routines only.
 * After it, every surface runs through [com.clindsay94.remex.routines.RoutineRepository.run], so they
 * all enqueue the same unique work (`routine-run-<id>`).
 */
object RoutineManualEntry {
    fun decide(routine: Routine?, source: String, confirmed: Boolean): RoutineManualDecision {
        if (routine == null) return RoutineManualDecision.NOT_FOUND
        val expected =
            when (source) {
                RoutineRunSources.MANUAL_SHORTCUT, RoutineRunSources.MANUAL_WIDGET -> RoutineTriggerTypes.MANUAL
                RoutineRunSources.NFC_TAP -> RoutineTriggerTypes.NFC_TAP
                else -> null
            }
        if (expected != null && routine.trigger?.type != expected) return RoutineManualDecision.WRONG_TRIGGER
        if (!confirmed && routine.steps.orEmpty().any { it?.isDestructive == true }) return RoutineManualDecision.CONFIRM
        return RoutineManualDecision.RUN
    }

    /** Which routines a shortcut or a widget may be made for: phone-run `manual` ones (spec 1.6 "Pin"). */
    fun isPinnable(routine: Routine?): Boolean = routine?.trigger?.type == RoutineTriggerTypes.MANUAL && routine.id != null
}

/**
 * The Android side of the manual surfaces: the shortcut, the widget, the NFC tap and the confirm
 * activity all come through [start], so a destructive routine confirms the same way from each.
 */
object RoutineManualSurfaces {
    /**
     * Starts [routineId] from [source]. A routine that needs confirming opens
     * [RoutineConfirmActivity] instead (from [context], which must be an Activity or allowed to start
     * one). Tells the person what happened with a toast; never throws.
     */
    suspend fun start(context: Context, routineId: String, source: String, confirmed: Boolean = false): RoutineManualDecision {
        val app = context.applicationContext
        return try {
            val repository = Routines.repository(app)
            val routine = repository.routine(routineId)
            val decision = RoutineManualEntry.decide(routine, source, confirmed)
            when (decision) {
                RoutineManualDecision.CONFIRM ->
                    context.startActivity(
                        RoutineConfirmActivity.intentFor(context, routineId, source).apply {
                            if (context !is android.app.Activity) addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        },
                    )
                RoutineManualDecision.RUN -> report(app, routine, repository.run(routineId, source))
                RoutineManualDecision.NOT_FOUND, RoutineManualDecision.WRONG_TRIGGER ->
                    toast(app, app.getString(R.string.routines_shortcut_invalid))
            }
            decision
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Starting routine ${RoutineLog.id(routineId)} from $source failed.", e)
            toast(app, app.getString(R.string.routines_run_unavailable))
            RoutineManualDecision.NOT_FOUND
        }
    }

    private suspend fun report(context: Context, routine: Routine?, start: RoutineRunStart) {
        val args = RoutineReasonArgs(routine = routine?.name, pc = routine?.hostIdentity?.let { Routines.pcDisplayName(context, it) })
        val text =
            when (start) {
                is RoutineRunStart.Started -> context.getString(R.string.routines_toast_running, routine?.name.orEmpty())
                is RoutineRunStart.Skipped -> RoutineReasonText.message(context, start.reasonCode, args)
                is RoutineRunStart.Failed -> RoutineReasonText.message(context, start.reasonCode, args)
                is RoutineRunStart.Invalid ->
                    context.getString(R.string.routines_run_invalid, RoutineReasonText.message(context, start.verdict.reasonCode, args.copy(detail = start.verdict.detail)))
                RoutineRunStart.RunsOnPc, RoutineRunStart.NotFound -> context.getString(R.string.routines_shortcut_invalid)
                RoutineRunStart.Unavailable -> context.getString(R.string.routines_run_unavailable)
            }
        toast(context, text)
    }

    internal suspend fun toast(context: Context, text: String) {
        withContext(Dispatchers.Main) { Toast.makeText(context.applicationContext, text, Toast.LENGTH_SHORT).show() }
    }
}

/** The "most recently run" order dynamic shortcuts follow (R-UX-26). Pure JVM. */
object RoutineShortcutPlan {
    const val MAX_DYNAMIC = 4

    /**
     * Up to [max] pinnable routines, most recently run on this phone first. A routine never run is
     * not offered: the launcher long-press list is "what you run", not a second copy of the list.
     */
    fun dynamicIds(routines: List<Routine>, history: List<RoutineRun>, max: Int = MAX_DYNAMIC): List<String> {
        val pinnable = routines.filter { RoutineManualEntry.isPinnable(it) && !it.isMalformed }.mapNotNull { it.id }.toSet()
        return history
            .asSequence()
            .filter { it.origin != RoutineRunOrigins.PC && it.routineId in pinnable && it.reasonCode != RoutineReasonCodes.STORE_RESET }
            .sortedByDescending { it.triggeredAtUnixMs }
            .mapNotNull { it.routineId }
            .distinct()
            .take(max.coerceAtLeast(0))
            .toList()
    }

    fun shortcutId(routineId: String): String = PREFIX + routineId

    fun routineIdOf(shortcutId: String): String? = shortcutId.takeIf { it.startsWith(PREFIX) }?.removePrefix(PREFIX)

    private const val PREFIX = "routine-"
}

/** Pinned and dynamic shortcuts (spec §8.3.3, R-UX-24, R-UX-26). Every call is best effort. */
object RoutineShortcuts {
    enum class PinResult { REQUESTED, UNSUPPORTED, NOT_PINNABLE }

    suspend fun requestPin(context: Context, routine: Routine): PinResult {
        if (!RoutineManualEntry.isPinnable(routine)) return PinResult.NOT_PINNABLE
        if (!ShortcutManagerCompat.isRequestPinShortcutSupported(context)) return PinResult.UNSUPPORTED
        val info = info(context, routine, Routines.secrets(context).shortcutKey())
        return try {
            if (ShortcutManagerCompat.requestPinShortcut(context, info, null)) PinResult.REQUESTED else PinResult.UNSUPPORTED
        } catch (e: IllegalStateException) {
            RoutineLog.w("The launcher refused a pinned shortcut.", e)
            PinResult.UNSUPPORTED
        }
    }

    /**
     * Brings every shortcut in line with the routines: the dynamic list is rebuilt from recent runs,
     * pinned shortcuts get the routine's current name, and a pinned shortcut whose routine is gone or
     * no longer starts with Tap Run is disabled with "Routine deleted" (spec §8.3.3).
     */
    suspend fun refresh(context: Context, routines: List<Routine>, history: List<RoutineRun>) {
        try {
            val key = Routines.secrets(context).shortcutKey()
            val byId = routines.filter { RoutineManualEntry.isPinnable(it) }.associateBy { it.id.orEmpty() }
            val max = minOf(RoutineShortcutPlan.MAX_DYNAMIC, ShortcutManagerCompat.getMaxShortcutCountPerActivity(context))
            val dynamic = RoutineShortcutPlan.dynamicIds(routines, history, max).mapNotNull { byId[it] }.map { info(context, it, key) }
            ShortcutManagerCompat.setDynamicShortcuts(context, dynamic)

            val pinned = ShortcutManagerCompat.getShortcuts(context, ShortcutManagerCompat.FLAG_MATCH_PINNED)
            val stale = ArrayList<String>()
            val live = ArrayList<ShortcutInfoCompat>()
            for (shortcut in pinned) {
                val routineId = RoutineShortcutPlan.routineIdOf(shortcut.id) ?: continue
                val routine = byId[routineId]
                if (routine == null) stale += shortcut.id else live += info(context, routine, key)
            }
            if (stale.isNotEmpty()) ShortcutManagerCompat.disableShortcuts(context, stale, context.getString(R.string.routines_shortcut_deleted))
            if (live.isNotEmpty()) {
                ShortcutManagerCompat.enableShortcuts(context, live)
                ShortcutManagerCompat.updateShortcuts(context, live)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // Rate limiting and launcher quirks: a shortcut list a little out of date is harmless.
            RoutineLog.w("Updating routine shortcuts failed.", e)
        }
    }

    private fun info(context: Context, routine: Routine, key: ByteArray): ShortcutInfoCompat {
        val id = checkNotNull(routine.id) { "a pinnable routine has an id" }
        val name = routine.name?.takeIf { it.isNotBlank() } ?: context.getString(R.string.routines_new_routine)
        return ShortcutInfoCompat.Builder(context, RoutineShortcutPlan.shortcutId(id))
            .setShortLabel(name)
            .setLongLabel(name)
            .setIcon(IconCompat.createWithResource(context, R.drawable.ic_routine_shortcut))
            .setIntent(RoutineShortcutActivity.intentFor(context, id, RoutineShortcutSignature.sign(key, id)))
            .build()
    }
}
