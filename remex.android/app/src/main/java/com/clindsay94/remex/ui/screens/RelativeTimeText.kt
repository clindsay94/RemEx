package com.clindsay94.remex.ui.screens

import android.text.format.DateUtils
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.R
import kotlinx.coroutines.delay

/**
 * "Just now" versus the system's "5 minutes ago" (3.0 comb, zero-minutes-ago).
 *
 * DateUtils with MINUTE_IN_MILLIS resolution prints "0 minutes ago" for anything under a minute,
 * which is the common case right after connecting. Pure, so the rule is tested on the JVM.
 */
object RelativeTime {
    const val JUST_NOW_WINDOW_MS = 60_000L

    /**
     * Under a minute old - or slightly in the future, which a clock adjusted after the stamp was taken
     * can produce, and which "in 0 minutes" would describe no better.
     */
    fun isJustNow(atMillis: Long, nowMillis: Long): Boolean = nowMillis - atMillis < JUST_NOW_WINDOW_MS

    /** How long until the minute-resolution text can next change, so a ticker wakes only then. */
    fun millisUntilNextMinute(nowMillis: Long): Long =
        JUST_NOW_WINDOW_MS - Math.floorMod(nowMillis, JUST_NOW_WINDOW_MS)
}

/**
 * The current time, advanced once a minute on the minute, so relative times on screen ("2 minutes
 * ago") move on their own instead of only when something else recomposes.
 */
@Composable
fun rememberMinuteClock(): Long {
    val now by
        produceState(System.currentTimeMillis()) {
            while (true) {
                delay(RelativeTime.millisUntilNextMinute(System.currentTimeMillis()))
                value = System.currentTimeMillis()
            }
        }
    return now
}

/** [atMillis] relative to [nowMillis] in the phone's own words, or "just now" under a minute. */
@Composable
fun relativeTimeText(atMillis: Long, nowMillis: Long): String =
    if (RelativeTime.isJustNow(atMillis, nowMillis)) {
        stringResource(R.string.relative_time_just_now)
    } else {
        DateUtils.getRelativeTimeSpanString(atMillis, nowMillis, DateUtils.MINUTE_IN_MILLIS).toString()
    }
