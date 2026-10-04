package com.clindsay94.remex

import android.app.Application
import com.clindsay94.remex.routines.home.HomePresence
import com.clindsay94.remex.service.FileTransferNotificationManager

/**
 * The process entry point. Its main job (routines spec §8.3.1, RemEx-pp0rt.8): home presence
 * re-registers its network callback whenever the process starts, because the registration is the only
 * way a `home.arrive` routine ever hears about a network, and nothing reports that it went missing.
 * [HomePresence.onProcessStart] reads no store unless a home routine exists, and does its work off the
 * main thread. It also creates the file-transfer notification channel (RemEx-pp4cm.8).
 */
class RemexApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        HomePresence.onProcessStart(this)
        // Creates the transfer channel and deletes the retired silent one (RemEx-pp4cm.8), so the
        // old entry does not sit in the system notification settings until the first transfer.
        FileTransferNotificationManager.ensureTransferChannel(this)
    }
}
