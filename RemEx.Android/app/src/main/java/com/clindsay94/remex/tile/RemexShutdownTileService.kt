package com.clindsay94.remex.tile

import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import org.json.JSONObject

class RemexShutdownTileService : TileService() {
    private fun executeCommand() {
        val commandJson = JSONObject().apply { put("action", "Shutdown") }.toString()
        RemexCoreClient.SendCommand(commandJson).getOrNull()
    }

    override fun onClick() {
        super.onClick()
        if (!RemexClientManager.isConnected.value) {
            qsTile.state = Tile.STATE_INACTIVE
            qsTile.updateTile()
            return
        }
        if (isLocked) {
            unlockAndRun { executeCommand() }
        } else {
            executeCommand()
        }
    }

    override fun onStartListening() {
        super.onStartListening()
        qsTile.state = if (RemexClientManager.isConnected.value) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        qsTile.label = getString(com.clindsay94.remex.R.string.tile_shutdown_label)
        qsTile.updateTile()
    }
}
