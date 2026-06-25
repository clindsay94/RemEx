package com.clindsay94.remex.tile

import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.json.JSONObject

class RemexWakeOnLanTileService : TileService() {

    private fun executeCommand() {
        // WoL doesn't require an active connection — it wakes the PC up.
        // The coroutine runs on IO because we need to read DataStore settings.
        CoroutineScope(Dispatchers.IO).launch {
            val settingsManager = SettingsManager(applicationContext)
            val macAddress = settingsManager.macAddressFlow.first()

            if (macAddress.isNotEmpty()) {
                val commandJson = JSONObject().apply {
                    put("action", "WAKEONLAN")
                    put("parameters", JSONObject().apply {
                        put("MacAddress", macAddress)
                        // Using default broadcast IP and port as per PingPongHandler
                        put("BroadcastIp", "255.255.255.255")
                        put("Port", "9")
                    })
                }.toString()

                RemexCoreClient.SendCommand(commandJson).getOrNull()
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
        // WoL is available as long as we have a MAC address configured
        CoroutineScope(Dispatchers.IO).launch {
            val settingsManager = SettingsManager(applicationContext)
            val macAddress = settingsManager.macAddressFlow.first()

            qsTile.state = if (macAddress.isNotEmpty()) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
            qsTile.label = getString(com.clindsay94.remex.R.string.tile_wol_label)
            qsTile.updateTile()
        }
    }
}
