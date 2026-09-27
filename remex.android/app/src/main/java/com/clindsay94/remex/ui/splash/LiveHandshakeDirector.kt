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
    const val FLOOR = 0.9
    const val GRACE = 1.2
    const val CAP = 2.6
    const val LOCK_HOLD = 0.5
    const val EXIT = 0.72
    const val FADE_EXIT = 0.28
    const val FIRST_PULSE = 0.32
    const val PULSE_PERIOD = 1.0
}

/**
 * What the director knows. A null time means the event has not happened (yet, or ever). The
 * director itself only ever looks at events whose time is <= now, so callers may pass future
 * events (the tests do) without leaking them into a decision.
 */
data class DirectorInputs(
    val peers: Int,
    val hasTarget: Boolean,
    val readyAt: Double? = null,
    val linkedAt: Double? = null,
    val failedAt: Double? = null,
    val skipAt: Double? = null,
)

enum class ExitOrigin { Mark, Target }

/**
 * One splash's hand-off decision. Call [step] once per frame with a monotonically increasing
 * `now`; the first frame where `now >= candidate(known)` fixes the hand-off, and nothing that
 * happens afterwards changes it.
 *
 * [exitFromMark] is the PC rule (it always opens from the mark); Android opens from the target
 * node when the target linked before the hand-off.
 */
class LiveHandshakeDirector(private val exitFromMark: Boolean = false) {

    /** When the hand-off started, or null while the splash is still waiting. */
    var handoffAt: Double? = null
        private set

    /** Where the portal opens from; meaningful once [handoffAt] is set. */
    var origin: ExitOrigin = ExitOrigin.Mark
        private set

    /** True once the hand-off has started. */
    fun step(now: Double, inputs: DirectorInputs): Boolean {
        if (handoffAt != null) return true
        if (now >= candidate(inputs, now)) {
            handoffAt = now
            val linkedAt = inputs.linkedAt
            origin = if (!exitFromMark && inputs.hasTarget && inputs.peers > 0 &&
                linkedAt != null && linkedAt <= now
            ) ExitOrigin.Target else ExitOrigin.Mark
        }
        return handoffAt != null
    }

    /** True when the target is shown as locked at [t]: linked, and linked before any hand-off. */
    fun isLinkedAt(t: Double, linkedAt: Double?): Boolean {
        if (linkedAt == null || t < linkedAt) return false
        val h = handoffAt ?: return true
        return linkedAt <= h
    }

    /** When the splash is completely over (hand-off plus the exit), or null while waiting. */
    fun doneAt(reducedMotion: Boolean): Double? =
        handoffAt?.let { it + if (reducedMotion) LiveHandshakeTiming.FADE_EXIT else LiveHandshakeTiming.EXIT }

    companion object {
        /**
         * The spec's `candidate(known)`, with `known` = the events in [inputs] whose time <= [now].
         */
        fun candidate(inputs: DirectorInputs, now: Double): Double {
            fun Double?.known(): Double? = this?.takeIf { it <= now }
            val skipAt = inputs.skipAt.known()
            val readyAt = inputs.readyAt.known()
            val linkedAt = inputs.linkedAt.known()
            val failedAt = inputs.failedAt.known()
            val c = when {
                skipAt != null -> skipAt
                readyAt == null -> LiveHandshakeTiming.CAP
                inputs.peers == 0 || !inputs.hasTarget -> max(LiveHandshakeTiming.FLOOR, readyAt)
                linkedAt != null -> maxOf(linkedAt + LiveHandshakeTiming.LOCK_HOLD, LiveHandshakeTiming.FLOOR, readyAt)
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

/** What the status line under the wordmark says. Rendered localized and upper-cased by the UI. */
sealed interface HandshakeStatus {
    data object Starting : HandshakeStatus
    data class Pinging(val peers: Int) : HandshakeStatus
    data class Awake(val awake: Int, val peers: Int) : HandshakeStatus
    data class Linked(val name: String, val rttMs: Long?) : HandshakeStatus
    data class NotAnswering(val name: String) : HandshakeStatus
    data object NonePaired : HandshakeStatus

    /** Linked is drawn in the accent colour; everything else muted. */
    val hot: Boolean get() = this is Linked
}

object LiveHandshakeStatusModel {
    /**
     * The lab's `statusText`, made live: it cannot know the future, so "not answering" fires
     * on evidence ([targetSilentAt]: the connect attempt failed, the probe gave up on the target,
     * or the hand-off started without a lock) rather than on the lab's foreknowledge.
     */
    fun statusAt(
        t: Double,
        peers: Int,
        awake: Int,
        targetName: String?,
        linked: Boolean,
        targetRttMs: Long?,
        targetSilentAt: Double?,
    ): HandshakeStatus = when {
        peers == 0 -> if (t < LiveHandshakeTiming.FIRST_PULSE) HandshakeStatus.Starting else HandshakeStatus.NonePaired
        linked && targetName != null -> HandshakeStatus.Linked(targetName, targetRttMs)
        targetName != null && targetSilentAt != null && t >= targetSilentAt -> HandshakeStatus.NotAnswering(targetName)
        t < LiveHandshakeTiming.FIRST_PULSE -> HandshakeStatus.Starting
        awake > 0 -> HandshakeStatus.Awake(awake, peers)
        else -> HandshakeStatus.Pinging(peers)
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
