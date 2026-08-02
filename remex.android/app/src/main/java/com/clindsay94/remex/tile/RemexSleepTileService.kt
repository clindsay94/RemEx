package com.clindsay94.remex.tile

import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.clindsay94.remex.RemexClientManager

class RemexSleepTileService : TileService() {
    private fun executeCommand() = sendTileCommand("Sleep")

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
        qsTile.label = getString(com.clindsay94.remex.R.string.tile_sleep_label)
        qsTile.updateTile()
    }
}
