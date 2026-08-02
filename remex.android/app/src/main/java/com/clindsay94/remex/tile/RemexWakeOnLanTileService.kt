package com.clindsay94.remex.tile

import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

class RemexWakeOnLanTileService : TileService() {

    // One scope per service instance: Main.immediate so qsTile is only touched on the main thread,
    // SupervisorJob so a failed WoL send can't kill a pending tile update. The listening-window job
    // is cancelled in onStopListening (tile updates are invalid after that); the whole scope is
    // cancelled in onDestroy so nothing outlives the service. (RemEx-8efl)
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var listenJob: Job? = null

    private fun executeCommand() {
        // WoL doesn't require an active connection — it wakes the PC up.
        //
        // NOT ON serviceScope, WHICH onDestroy CANCELS (RemEx-66rf). The intent recorded when this
        // was written is that "a tapped wake still goes out" after the panel closes, and serviceScope
        // never fully delivered that: the settings read below is a suspension point, so a cancelled
        // scope could always drop the wake before it was sent. The window was small while SendCommand
        // returned the instant it had handed the message off; now that it waits for the PC's answer
        // the coroutine lives for the whole round trip, and a tile is destroyed shortly after the
        // panel closes — which is exactly when somebody taps Wake and swipes away. The shared tile
        // scope outlives the service, which is what the original comment was reaching for.
        //
        // Reading the MAC inside the same block matters for the same reason: a read that completed
        // into a dead scope would never reach the send.
        val settingsManager = SettingsManager(applicationContext)
        launchTileWork {
            val macAddress = settingsManager.macAddressFlow.first()
            if (macAddress.isNotEmpty()) {
                sendTileCommand(
                    "WAKEONLAN",
                    JSONObject().apply {
                        put("MacAddress", macAddress)
                        // Using default broadcast IP and port as per PingPongHandler
                        put("BroadcastIp", "255.255.255.255")
                        put("Port", "9")
                    },
                )
            }
        }
    }

    override fun onClick() {
        super.onClick()
        // Require device unlock before sending WoL magic packet — prevents a bystander
        // from powering on a remote machine from a locked phone screen.
        if (isLocked) {
            unlockAndRun { executeCommand() }
        } else {
            executeCommand()
        }
    }

    override fun onStartListening() {
        super.onStartListening()
        // WoL is available as long as we have a MAC address configured.
        listenJob?.cancel()
        listenJob = serviceScope.launch {
            val macAddress = withContext(Dispatchers.IO) {
                SettingsManager(applicationContext).macAddressFlow.first()
            }
            // qsTile is only valid while listening — it goes null if the panel closed during the
            // settings read, and updating a dead tile would NPE.
            val tile = qsTile ?: return@launch
            tile.state = if (macAddress.isNotEmpty()) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
            tile.label = getString(com.clindsay94.remex.R.string.tile_wol_label)
            tile.updateTile()
        }
    }

    override fun onStopListening() {
        listenJob?.cancel()
        listenJob = null
        super.onStopListening()
    }

    override fun onDestroy() {
        serviceScope.cancel()
        super.onDestroy()
    }
}
