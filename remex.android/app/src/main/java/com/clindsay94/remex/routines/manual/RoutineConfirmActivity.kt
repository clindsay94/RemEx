package com.clindsay94.remex.routines.manual

import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.lifecycleScope
import com.clindsay94.remex.routines.RoutineItem
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.ui.routines.RunConfirmDialog
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.launch

/**
 * The phone-side confirm for a routine with a destructive step started from a shortcut, the widget
 * or an NFC tag (routines spec 1.7 "Manual runs", §8.3.3, L12): the translucent-dialog pattern of
 * `TileConfirmActivity`, with the same dialog the in-app Run shows.
 *
 * It does not replace the PC's countdown: the PC still counts down 15 s after this (D1).
 *
 * `exported="false"`: only RemEx's own surfaces open it. It re-reads the routine and re-decides on
 * Confirm, so an extra naming a routine that changed in between runs what is stored now.
 */
class RoutineConfirmActivity : ComponentActivity() {
    private var item by mutableStateOf<RoutineItem?>(null)
    private var pcName by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val routineId = runCatching { intent.getStringExtra(EXTRA_ROUTINE_ID) }.getOrNull()
        val source = runCatching { intent.getStringExtra(EXTRA_SOURCE) }.getOrNull()?.takeIf { it in ALLOWED_SOURCES }
        if (routineId == null || source == null) {
            finish()
            return
        }
        lifecycleScope.launch {
            val repository = Routines.repository(applicationContext)
            repository.load()
            val found = repository.routines.value.firstOrNull { it.routine.id == routineId }
            if (found == null) {
                RoutineManualSurfaces.toast(applicationContext, getString(com.clindsay94.remex.R.string.routines_shortcut_invalid))
                finish()
                return@launch
            }
            pcName = found.routine.hostIdentity?.let { Routines.pcDisplayName(applicationContext, it) }
            item = found
        }
        setContent {
            RemExTheme {
                item?.let { current ->
                    RunConfirmDialog(
                        item = current,
                        pcName = pcName,
                        onConfirm = {
                            item = null
                            lifecycleScope.launch {
                                RoutineManualSurfaces.start(this@RoutineConfirmActivity, routineId, source, confirmed = true)
                                finish()
                            }
                        },
                        onDismiss = { finish() },
                    )
                }
            }
        }
    }

    companion object {
        private const val EXTRA_ROUTINE_ID = "com.clindsay94.remex.routines.CONFIRM_ROUTINE_ID"
        private const val EXTRA_SOURCE = "com.clindsay94.remex.routines.CONFIRM_SOURCE"
        private val ALLOWED_SOURCES = setOf(RoutineRunSources.MANUAL_SHORTCUT, RoutineRunSources.MANUAL_WIDGET, RoutineRunSources.NFC_TAP)

        fun intentFor(context: Context, routineId: String, source: String): Intent =
            Intent(context, RoutineConfirmActivity::class.java)
                .putExtra(EXTRA_ROUTINE_ID, routineId)
                .putExtra(EXTRA_SOURCE, source)
    }
}
