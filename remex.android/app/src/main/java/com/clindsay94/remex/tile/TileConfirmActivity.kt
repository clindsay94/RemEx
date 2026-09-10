package com.clindsay94.remex.tile

import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.annotation.StringRes
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.RemExTheme

/**
 * Confirmation gate for a Quick Settings tile's destructive PC command.
 *
 * The seven destructive tiles (Shutdown, Restart, RestartToUefi, Hibernate, Lock, MonitorOff,
 * Sleep — everything in `tile/` except Wake-on-LAN, which is additive, not destructive) used to
 * run their command on a single tap with no confirmation step at all: `unlockAndRun` gated on
 * device unlock, never on user intent. RemEx-sxfp named this defect on the widget; the tiles
 * share it.
 *
 * Launched by [confirmTileCommand] via `TileService.startActivityAndCollapse(PendingIntent)` —
 * the platform-mandated replacement for the deprecated `TileService.showDialog(Dialog)` as of API
 * 34 (this app's `minSdk`). Themed `Theme.RemEx.TileConfirm` (translucent, floating) so it reads
 * as a dropped-in confirm dialog over whatever was already on screen, not a full-screen app
 * launch for a single yes/no.
 *
 * `exported="false"`: only this app's own tiles construct the launching [PendingIntent], so
 * nothing outside the app should be able to target this Activity directly with an arbitrary
 * `action` extra.
 */
class TileConfirmActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val action = intent.getStringExtra(EXTRA_ACTION)
        val labelRes = intent.getIntExtra(EXTRA_LABEL_RES, 0)
        if (action.isNullOrBlank() || labelRes == 0) {
            finish()
            return
        }

        setContent {
            RemExTheme {
                TileConfirmDialog(
                    labelRes = labelRes,
                    onConfirm = {
                        sendTileCommand(action)
                        finish()
                    },
                    onDismiss = { finish() }
                )
            }
        }
    }

    companion object {
        private const val EXTRA_ACTION = "com.clindsay94.remex.tile.EXTRA_ACTION"
        private const val EXTRA_LABEL_RES = "com.clindsay94.remex.tile.EXTRA_LABEL_RES"

        /** [action] is the wire command verb (e.g. "Shutdown"); [labelRes] is the tile's own label. */
        fun intentFor(context: Context, action: String, @StringRes labelRes: Int): Intent =
            Intent(context, TileConfirmActivity::class.java).apply {
                putExtra(EXTRA_ACTION, action)
                putExtra(EXTRA_LABEL_RES, labelRes)
            }
    }
}

@Composable
private fun TileConfirmDialog(
    @StringRes labelRes: Int,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.tile_confirm_title)) },
        text = { Text(stringResource(R.string.tile_confirm_message, stringResource(labelRes))) },
        confirmButton = {
            TextButton(onClick = onConfirm) { Text(stringResource(R.string.button_confirm)) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.button_cancel)) }
        }
    )
}
