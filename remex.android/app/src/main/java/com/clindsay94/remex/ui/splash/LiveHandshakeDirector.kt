package com.clindsay94.remex.ui.splash

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min

/**
 * The rules of the "Live Handshake" splash (RemEx-8g6n0), shared word for word with the PC.
 *
 * Pure and JVM-only on purpose: the hand-off decision is the part of this splash that can go
 * wrong silently (a splash that ends too early hides the lock-on, one that never ends holds the
 * app hostage), so it is proven against `docs/specs/live-handshake-director-vectors.json`, the
 * same file the C# director is tested against. Every number here comes from the spec
 * (`docs/specs/2026-09-26-live-handshake-splash-design.md`) and the motion lab
 * (`docs/specs/assets/live-handshake-lab.html`).
 *
 * Times are seconds since the splash started.
 */
object LiveHandshakeTiming {
    const val FLOOR = 1.4
    const val GRACE = 1.2
    const val CAP = 3.0
    const val LOCK_HOLD = 0.7
    const val EXIT = 0.72
    const val FADE_EXIT = 0.28
    const val FIRST_PULSE = 0.32
    const val PULSE_PERIOD = 1.0

    // Staging (revised 2026-09-26 after the first device test): real events are never faked or
    // reordered, but each is SHOWN no sooner than it can be read.
    const val ANSWER_MIN = 0.62
    const val ANSWER_GAP = 0.18
    const val LOCK_AFTER = 0.5

    // The console.
    const val LINE_GAP = 0.32
    const val LINE_TYPE_RATE = 55.0
    const val LINE_SLIDE = 0.18
    const val NOT_ANSWERING_AFTER_READY = 0.5
    const val NOT_ANSWERING_BEFORE_HANDOFF = 0.3
}

/** A real probe answer: which peer, and when it was heard. */
data class DirectorAnswer(val id: String, val at: Double)

/**
 * What the director knows. A null time means the event has not happened (yet, or ever). The
 * director itself only ever looks at events whose time is <= now, so callers may pass future
 * events (the tests do) without leaking them into a decision.
 *
 * [targetId] is the peer the app is connecting to, or null when there is none; a link
 * ([linkedAt]) also counts as the target answering.
 */
data class DirectorInputs(
    val peers: Int,
    val targetId: String?,
    val answers: List<DirectorAnswer> = emptyList(),
    val readyAt: Double? = null,
    val linkedAt: Double? = null,
    val failedAt: Double? = null,
    val skipAt: Double? = null,
) {
    val hasTarget: Boolean get() = targetId != null
}

enum class ExitOrigin { Mark, Target }

/**
 * When each answer and the lock are SHOWN (the spec's staging). Array-backed and reused, so the
 * per-frame restaging costs no allocation once it has grown to the peer count.
 */
class LiveHandshakeStaging {
    private var ids = arrayOfNulls<String>(8)
    private var effective = DoubleArray(8)
    private var shown = DoubleArray(8)

    /** How many answers are staged. */
    var count = 0
        private set

    /** When the lock-on is shown, or null when it is not (yet, or at all). */
    var lockShown: Double? = null
        internal set

    fun shownAt(id: String): Double? {
        for (i in 0 until count) if (ids[i] == id) return shown[i]
        return null
    }

    fun idAt(index: Int): String = ids[index]!!

    fun shownAtIndex(index: Int): Double = shown[index]

    /** The staged show times, ascending (they are staged in real order). */
    fun shownTimes(): List<Double> = List(count) { shown[it] }

    internal fun restage(inputs: DirectorInputs, now: Double) {
        count = 0
        val targetId = inputs.targetId
        val linkedAt = inputs.linkedAt?.takeIf { it <= now && targetId != null }
        var targetHeard = false
        for (a in inputs.answers) {
            if (a.at > now) continue
            var eff = a.at
            if (a.id == targetId) {
                targetHeard = true
                if (linkedAt != null) eff = min(eff, linkedAt)
            }
            add(a.id, eff)
        }
        // A link with no probe answer (probe lost, link won) still shows an answer first.
        if (linkedAt != null && !targetHeard) add(targetId!!, linkedAt)
        // Real order (stable insertion sort; count is tiny).
        for (i in 1 until count) {
            val id = ids[i]
            val e = effective[i]
            var j = i - 1
            while (j >= 0 && effective[j] > e) {
                ids[j + 1] = ids[j]
                effective[j + 1] = effective[j]
                j--
            }
            ids[j + 1] = id
            effective[j + 1] = e
        }
        var prev = Double.NEGATIVE_INFINITY
        for (i in 0 until count) {
            val s = maxOf(effective[i], LiveHandshakeTiming.ANSWER_MIN, prev + LiveHandshakeTiming.ANSWER_GAP)
            shown[i] = s
            prev = s
        }
        lockShown = if (linkedAt != null) {
            max(linkedAt, shownAt(targetId!!)!! + LiveHandshakeTiming.LOCK_AFTER)
        } else null
    }

    /** At the hand-off: nothing staged past it is shown. */
    internal fun freezeAt(handoff: Double) {
        var n = 0
        for (i in 0 until count) {
            if (shown[i] < handoff) {
                ids[n] = ids[i]
                effective[n] = effective[i]
                shown[n] = shown[i]
                n++
            }
        }
        count = n
        lockShown = lockShown?.takeIf { it <= handoff }
    }

    private fun add(id: String, eff: Double) {
        if (count == ids.size) {
            ids = ids.copyOf(count * 2)
            effective = effective.copyOf(count * 2)
            shown = shown.copyOf(count * 2)
        }
        ids[count] = id
        effective[count] = eff
        count++
    }
}

/**
 * One splash's hand-off decision and staging. Call [step] once per frame with a monotonically
 * increasing `now`; the first frame where `now >= candidate(known)` fixes the hand-off, and
 * nothing that happens afterwards changes it.
 *
 * [exitFromMark] is the PC rule (it always opens from the mark); Android opens from the target
 * node when the lock was SHOWN at or before the hand-off.
 */
class LiveHandshakeDirector(private val exitFromMark: Boolean = false) {

    /** When the hand-off started, or null while the splash is still waiting. */
    var handoffAt: Double? = null
        private set

    /** Where the portal opens from; meaningful once [handoffAt] is set. */
    var origin: ExitOrigin = ExitOrigin.Mark
        private set

    /** The staged show times. Live until the hand-off, frozen (and trimmed) from then on. */
    val staging = LiveHandshakeStaging()

    private var lastInputs: DirectorInputs? = null
    private var lastKnown = -1

    /** True once the hand-off has started. */
    fun step(now: Double, inputs: DirectorInputs): Boolean {
        if (handoffAt != null) return true
        val known = knownCount(inputs, now)
        if (inputs !== lastInputs || known != lastKnown) {
            lastInputs = inputs
            lastKnown = known
            staging.restage(inputs, now)
        }
        if (now >= candidate(inputs, now, staging.lockShown)) {
            handoffAt = now
            staging.freezeAt(now)
            origin = if (!exitFromMark && inputs.hasTarget && inputs.peers > 0 && staging.lockShown != null) {
                ExitOrigin.Target
            } else ExitOrigin.Mark
        }
        return handoffAt != null
    }

    /** When the lock-on is shown; null when it is not (yet, or at all). */
    val lockShown: Double? get() = staging.lockShown

    /** True when the lock is shown at [t]. */
    fun isLockShownAt(t: Double): Boolean = staging.lockShown?.let { t >= it } == true

    /** When the splash is completely over (hand-off plus the exit), or null while waiting. */
    fun doneAt(reducedMotion: Boolean): Double? =
        handoffAt?.let { it + if (reducedMotion) LiveHandshakeTiming.FADE_EXIT else LiveHandshakeTiming.EXIT }

    /** How many staging-relevant events are known at [now]; restaging happens only when it moves. */
    private fun knownCount(inputs: DirectorInputs, now: Double): Int {
        var n = 0
        val answers = inputs.answers
        for (i in answers.indices) if (answers[i].at <= now) n++
        val linked = inputs.linkedAt
        if (linked != null && linked <= now) n += 1000
        return n
    }

    companion object {
        /**
         * The spec's `candidate(known)`, with `known` = the events in [inputs] whose time <= [now],
         * and [lockShown] the staged lock for that same knowledge.
         */
        fun candidate(inputs: DirectorInputs, now: Double, lockShown: Double?): Double {
            fun Double?.known(): Double? = this?.takeIf { it <= now }
            val skipAt = inputs.skipAt.known()
            val readyAt = inputs.readyAt.known()
            val linkedAt = inputs.linkedAt.known()
            val failedAt = inputs.failedAt.known()
            val c = when {
                skipAt != null -> skipAt
                readyAt == null -> LiveHandshakeTiming.CAP
                inputs.peers == 0 || !inputs.hasTarget -> max(LiveHandshakeTiming.FLOOR, readyAt)
                linkedAt != null -> maxOf(
                    (lockShown ?: linkedAt) + LiveHandshakeTiming.LOCK_HOLD, LiveHandshakeTiming.FLOOR, readyAt,
                )
                failedAt != null -> maxOf(LiveHandshakeTiming.FLOOR, readyAt, failedAt)
                else -> max(LiveHandshakeTiming.FLOOR, min(readyAt + LiveHandshakeTiming.GRACE, LiveHandshakeTiming.CAP))
            }
            return min(c, LiveHandshakeTiming.CAP)
        }

        /**
         * Pulse start times: FIRST_PULSE + k * PULSE_PERIOD, strictly before the hand-off. While
         * the hand-off is still open ([handoffAt] null) only pulses that have started by [now] are
         * returned. None at all under reduced motion.
         */
        fun pulses(handoffAt: Double?, now: Double, reducedMotion: Boolean): List<Double> {
            if (reducedMotion) return emptyList()
            val out = ArrayList<Double>(4)
            var k = 0
            while (true) {
                val tp = LiveHandshakeTiming.FIRST_PULSE + k * LiveHandshakeTiming.PULSE_PERIOD
                if (handoffAt != null) {
                    if (tp >= handoffAt) break
                } else if (tp > now) break
                out.add(tp)
                k++
            }
            return out
        }

        /** The most recent pulse that has started by [t] (and is before the hand-off), or null. */
        fun lastPulse(handoffAt: Double?, t: Double, reducedMotion: Boolean): Double? {
            if (reducedMotion) return null
            var last: Double? = null
            var k = 0
            while (true) {
                val tp = LiveHandshakeTiming.FIRST_PULSE + k * LiveHandshakeTiming.PULSE_PERIOD
                if (tp > t || (handoffAt != null && tp >= handoffAt)) break
                last = tp
                k++
            }
            return last
        }
    }
}

/** What one console line says. The UI turns it into localized, sentence-case text. */
enum class ConsoleLineKind { Pinging, NonePaired, Answered, Linked, NotAnswering, Opening }

/** A console line that has appeared: what it says, about which peer, and when it appeared. */
class ConsoleLine(
    val kind: ConsoleLineKind,
    /** The peer the line is about (Answered, Linked, NotAnswering), else null. */
    val peerId: String?,
    /** Peers for Pinging, the round trip in ms for Answered, else 0. */
    val value: Long,
    /** When it was due. */
    val due: Double,
    /** When it appeared: max(due, previous line + LINE_GAP), or the moment it became known. */
    val at: Double,
) {
    /** Accent-coloured lines. */
    val hot: Boolean get() = kind == ConsoleLineKind.Linked || kind == ConsoleLineKind.Opening
}

/**
 * The three-line console under the wordmark (spec, "The console"): it narrates the same staged
 * events, one readable line at a time.
 *
 * Lines are due at their event's shown time and each appears at `max(due, previous + LINE_GAP)`.
 * Live, a line can only be committed once its event is known; one learned after its slot has
 * passed appears when it is learned, never half-typed. Committed lines never move.
 */
class LiveHandshakeConsole {
    private val _lines = ArrayList<ConsoleLine>(8)

    /** Lines that have appeared, oldest first. */
    val lines: List<ConsoleLine> get() = _lines

    private var firstDone = false
    private var lockDone = false
    private var silentDone = false
    private var openDone = false
    private val answered = HashSet<String>()

    /**
     * Commits every line whose slot has come by [now].
     *
     * @param rttFor the measured round trip of a peer that answered the probe, or null (a peer
     *   that only "answered" by linking has no round trip, so no answered line).
     * @param targetSilentAt when there was first evidence the target is not answering (a failed
     *   connect, the probe giving up on it, or a hand-off with no lock), or null.
     */
    fun update(
        now: Double,
        director: LiveHandshakeDirector,
        inputs: DirectorInputs,
        rttFor: (String) -> Long?,
        targetSilentAt: Double?,
    ) {
        while (true) {
            bestKind = null
            bestId = null
            bestValue = 0L
            bestDue = Double.POSITIVE_INFINITY

            if (!firstDone) {
                if (inputs.peers == 0) offer(ConsoleLineKind.NonePaired, null, 0, LiveHandshakeTiming.FIRST_PULSE)
                else offer(ConsoleLineKind.Pinging, null, inputs.peers.toLong(), LiveHandshakeTiming.FIRST_PULSE)
            }
            val staging = director.staging
            for (i in 0 until staging.count) {
                val id = staging.idAt(i)
                if (id in answered) continue
                val rtt = rttFor(id) ?: continue
                offer(ConsoleLineKind.Answered, id, rtt, staging.shownAtIndex(i))
            }
            val handoff = director.handoffAt
            val lock = director.lockShown
            val target = inputs.targetId
            if (!lockDone && lock != null && target != null) offer(ConsoleLineKind.Linked, target, 0, lock)
            if (!silentDone && target != null && lock == null && targetSilentAt != null && targetSilentAt <= now) {
                var due = inputs.readyAt?.let { it + LiveHandshakeTiming.NOT_ANSWERING_AFTER_READY } ?: targetSilentAt
                if (handoff != null) due = min(due, handoff - LiveHandshakeTiming.NOT_ANSWERING_BEFORE_HANDOFF)
                offer(ConsoleLineKind.NotAnswering, target, 0, due)
            }
            if (!openDone && handoff != null) offer(ConsoleLineKind.Opening, null, 0, handoff)

            val kind = bestKind ?: return
            val id = bestId
            val previous = _lines.lastOrNull()?.at ?: Double.NEGATIVE_INFINITY
            var at = max(bestDue, previous + LiveHandshakeTiming.LINE_GAP)
            if (at > now) return
            // Learned after its slot: it appears now, whole, rather than already half-typed.
            if (now - at > LATE) at = now
            _lines.add(ConsoleLine(kind, id, bestValue, bestDue, at))
            when (kind) {
                ConsoleLineKind.Pinging, ConsoleLineKind.NonePaired -> firstDone = true
                ConsoleLineKind.Answered -> answered.add(id!!)
                ConsoleLineKind.Linked -> lockDone = true
                ConsoleLineKind.NotAnswering -> silentDone = true
                ConsoleLineKind.Opening -> openDone = true
            }
        }
    }

    // The earliest-due line not yet committed, found by [offer] during one scan.
    private var bestKind: ConsoleLineKind? = null
    private var bestId: String? = null
    private var bestValue = 0L
    private var bestDue = Double.POSITIVE_INFINITY

    private fun offer(kind: ConsoleLineKind, id: String?, value: Long, due: Double) {
        if (due < bestDue) {
            bestKind = kind
            bestId = id
            bestValue = value
            bestDue = due
        }
    }

    private companion object {
        /** A line whose slot passed more than this before it was known is "learned late". */
        const val LATE = 0.05
    }
}

/** Easing, spring and layout helpers ported from the lab. Pure maths, shared by the renderer. */
object LiveHandshakeMotion {

    /** A CSS-style cubic-bezier timing function (x1, y1, x2, y2). */
    class CubicBezier(x1: Double, y1: Double, x2: Double, y2: Double) {
        private val cx = 3 * x1
        private val bx = 3 * (x2 - x1) - cx
        private val ax = 1 - cx - bx
        private val cy = 3 * y1
        private val by = 3 * (y2 - y1) - cy
        private val ay = 1 - cy - by

        private fun sx(t: Double) = ((ax * t + bx) * t + cx) * t
        private fun sy(t: Double) = ((ay * t + by) * t + cy) * t
        private fun dsx(t: Double) = (3 * ax * t + 2 * bx) * t + cx

        operator fun invoke(x: Double): Double {
            if (x <= 0.0) return 0.0
            if (x >= 1.0) return 1.0
            var t = x
            repeat(8) {
                val d = dsx(t)
                if (abs(d) < 1e-6) return sy(clamp01(t))
                t -= (sx(t) - x) / d
            }
            return sy(clamp01(t))
        }
    }

    val EmphasizedDecelerate = CubicBezier(0.05, 0.7, 0.1, 1.0)
    val Standard = CubicBezier(0.2, 0.0, 0.0, 1.0)
    val Portal = CubicBezier(0.45, 0.0, 0.15, 1.0)

    fun clamp01(x: Double): Double = min(1.0, max(0.0, x))

    fun lerp(a: Double, b: Double, t: Double): Double = a + (b - a) * t

    fun smooth(a: Double, b: Double, x: Double): Double {
        val t = clamp01((x - a) / (b - a))
        return t * t * (3 - 2 * t)
    }

    /** Under-damped spring, 0 -> 1 with about 16 % overshoot, settled by about 0.4 s. */
    fun spring(tau: Double): Double = if (tau <= 0.0) 0.0 else 1 - exp(-tau * 9) * cos(tau * 16)

    /** Orbit radius factor from a measured round trip: 0.58 (instant) .. 1.0 (250 ms or worse). */
    fun radiusFor(rttMs: Double): Double = 0.58 + 0.42 * clamp01(log10(max(rttMs, 1.0)) / log10(250.0))

    /** The lab's FNV-1a over UTF-16 code units (JS `charCodeAt`), mapped to [0, 1). */
    fun hashStr(s: String): Double {
        var h = 2166136261L.toInt()
        for (c in s) {
            h = h xor c.code
            h *= 16777619
        }
        return (h.toUInt() % 10000u).toDouble() / 10000.0
    }

    /** Orbit angle for peer [index] of [count], the spread seeded by the peers' names. */
    fun peerAngle(index: Int, count: Int, namesHash: Double): Double {
        val base = -PI / 2 + 0.62 + namesHash * 0.5
        return base + index * 2 * PI / max(count, 1) + if (count == 2) 0.4 else 0.0
    }

    /** Pulse envelope for the mark breathing: sin(pi * age / 0.3) for the first 0.3 s. */
    fun pulseEnvelope(age: Double): Double = if (age in 0.0..0.3) kotlin.math.sin(PI * age / 0.3) else 0.0
}
