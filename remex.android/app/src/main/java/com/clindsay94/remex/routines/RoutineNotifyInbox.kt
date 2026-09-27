package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineNotifyPayload

/**
 * How a PC message (`routine_notify`, routines spec §7.3.5, R-UX-33) is presented. Pure, so the
 * queued / expired decisions are unit-tested without a notification manager.
 *
 * A message the PC held while the phone was away arrives on the next connection with its original
 * `queuedAtUnixMs`; anything older than [QUEUED_AFTER_MS] is shown as "Sent at <time>" so a two-hour
 * old "Your GPU is hot" is not read as news. An expired one is never shown: the PC records it in the
 * run's history instead ("only in run detail").
 */
internal object QueuedMessagePresenter {
    /** Older than this on arrival counts as a held message, not a live one. */
    const val QUEUED_AFTER_MS = 60_000L

    fun isExpired(notify: RoutineNotifyPayload, nowUnixMs: Long): Boolean = notify.expiresAtUnixMs in 1..nowUnixMs

    fun wasQueued(notify: RoutineNotifyPayload, nowUnixMs: Long): Boolean =
        notify.queuedAtUnixMs > 0 && nowUnixMs - notify.queuedAtUnixMs >= QUEUED_AFTER_MS
}

/**
 * The last [capacity] notify ids the phone actually SHOWED (§7.3.5: the phone de-duplicates the last
 * 200). Delivery is at least once, so the same message can come live and again in the next
 * connection's flush.
 *
 * **AN ID IS REMEMBERED ONLY AFTER A SUCCESSFUL POST.** Remembering it on arrival would make a post
 * that failed (notifications off, a thrown error) look like "already shown" on the resend, which would
 * then be acknowledged and dropped by the PC unseen.
 */
internal class RoutineNotifyDedup(private val capacity: Int = CAPACITY) {
    private val ids = LinkedHashSet<String>()

    /** True when [id] was already shown. */
    @Synchronized
    fun contains(id: String): Boolean = id in ids

    /** Records [id] as shown. */
    @Synchronized
    fun remember(id: String) {
        if (!ids.add(id)) return
        while (ids.size > capacity) ids.remove(ids.first())
    }

    companion object {
        const val CAPACITY = 200
    }
}

/** Where a PC message is posted: the `routines_messages` channel, redacted on the lock screen (T10). */
internal interface RoutineMessageSink {
    /**
     * Posts [notify]. [queued] adds the "Sent at <time>" line. False when RemEx may not post
     * notifications.
     */
    fun postPcMessage(notify: RoutineNotifyPayload, queued: Boolean): Boolean

    /**
     * A countdown heads-up for a run the phone has no record of yet: the PC's text, gone after
     * [timeoutMs] (when the countdown ends), with a Cancel when the run id is known.
     */
    fun postPcCountdown(notify: RoutineNotifyPayload, timeoutMs: Long): Boolean
}
