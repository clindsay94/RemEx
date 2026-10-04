package com.clindsay94.remex.ui.screens

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import android.util.Log

/**
 * Opens Android's "let this app run in the background" screen. Both the first-run tutorial and the
 * routines notice offer this button, and both used to carry their own copy of the launch.
 *
 * GUARDED because some phones (work profiles, trimmed vendor builds) have no screen for the direct
 * request, and an unresolved `startActivity` crashed the very first run. The order is: ask for the
 * exemption for this app directly, fall back to the battery-optimisation list, and if that screen is
 * missing too, log it and do nothing rather than crash.
 *
 * [start] is the launcher, passed in so the fallback chain can be tested on the JVM with one that
 * throws; callers pass `context::startActivity`.
 *
 * @return true if one of the two screens opened.
 */
internal fun openBatteryOptimisationSettings(context: Context, start: (Intent) -> Unit): Boolean {
    val direct = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
        data = Uri.parse("package:" + context.packageName)
    }
    return runCatching { start(direct) }
        .recoverCatching { start(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }
        .onFailure { Log.w("BatteryOptimisation", "No battery optimisation screen on this phone", it) }
        .isSuccess
}
