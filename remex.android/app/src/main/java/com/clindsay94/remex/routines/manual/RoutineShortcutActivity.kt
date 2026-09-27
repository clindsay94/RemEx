package com.clindsay94.remex.routines.manual

import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.ui.routines.RoutineOpenRequests
import kotlinx.coroutines.launch

/**
 * The target of every routine shortcut, pinned or dynamic (routines spec §8.3.3, T1, R-SEC-02).
 *
 * **NOT EXPORTED, AND IT CHECKS `sig` ANYWAY.** Only the launcher starts it, on RemEx's behalf, from a
 * shortcut RemEx published. The extras carry the routine id and `sig = HMAC-SHA256(shortcutKey, id)`;
 * an intent without a valid `sig` (a stale shortcut, a reused id, anything that is not ours) opens
 * the routines list with a message and runs nothing. REGRESSION-GUARDS "routine shortcut sig".
 *
 * No UI of its own (Theme.NoDisplay): it runs the routine and finishes, or hands a destructive routine
 * to [RoutineConfirmActivity], exactly like a tile does.
 */
class RoutineShortcutActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val routineId = runCatching { intent.getStringExtra(EXTRA_ROUTINE_ID) }.getOrNull()
        val sig = runCatching { intent.getStringExtra(EXTRA_SIG) }.getOrNull()
        lifecycleScope.launch {
            try {
                val key = Routines.secrets(applicationContext).shortcutKey()
                if (routineId != null && RoutineShortcutSignature.verify(key, routineId, sig)) {
                    val decision = RoutineManualSurfaces.start(this@RoutineShortcutActivity, routineId, RoutineRunSources.MANUAL_SHORTCUT)
                    if (decision == RoutineManualDecision.NOT_FOUND || decision == RoutineManualDecision.WRONG_TRIGGER) openList()
                } else {
                    RoutineLog.w("A routine shortcut without a valid signature was refused.")
                    openList()
                }
            } finally {
                finish()
            }
        }
    }

    /** The routines list with "That shortcut no longer works" (spec §8.3.3); never a run. */
    private fun openList() {
        startActivity(
            Intent(this, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(RoutineOpenRequests.EXTRA_OPEN_LIST_NOTICE, RoutineOpenRequests.NOTICE_SHORTCUT_INVALID),
        )
    }

    companion object {
        const val ACTION_RUN = "com.clindsay94.remex.routines.RUN_SHORTCUT"
        const val EXTRA_ROUTINE_ID = "com.clindsay94.remex.routines.SHORTCUT_ROUTINE_ID"
        const val EXTRA_SIG = "com.clindsay94.remex.routines.SHORTCUT_SIG"

        /** Shortcut intents need an action; the extras are strings, which a shortcut can persist. */
        fun intentFor(context: Context, routineId: String, sig: String): Intent =
            Intent(context, RoutineShortcutActivity::class.java)
                .setAction(ACTION_RUN)
                .putExtra(EXTRA_ROUTINE_ID, routineId)
                .putExtra(EXTRA_SIG, sig)
    }
}
