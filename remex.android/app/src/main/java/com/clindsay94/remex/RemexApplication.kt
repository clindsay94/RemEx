package com.clindsay94.remex

import android.app.Application
import com.clindsay94.remex.routines.home.HomePresence

/**
 * The process entry point. It exists for one job (routines spec §8.3.1, RemEx-pp0rt.8): home presence
 * re-registers its network callback whenever the process starts, because the registration is the only
 * way a `home.arrive` routine ever hears about a network, and nothing reports that it went missing.
 * [HomePresence.onProcessStart] reads no store unless a home routine exists, and does its work off the
 * main thread.
 */
class RemexApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        HomePresence.onProcessStart(this)
    }
}
