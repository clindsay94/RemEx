package com.clindsay94.remex.ui.splash

import android.app.Activity
import android.content.Context
import android.content.pm.ActivityInfo
import android.content.res.Resources
import android.graphics.RuntimeShader
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.ClipOp
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.drawscope.rotateRad
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.TextMeasurer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.hapticCommandAcknowledged
import com.clindsay94.remex.ui.screens.SplashBrand
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Locale
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.floor
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sign
import kotlin.math.sin
import com.clindsay94.remex.ui.splash.LiveHandshakeMotion as M
import com.clindsay94.remex.ui.splash.LiveHandshakeTiming as T

/** Longest the overlay holds its first frame waiting for the system splash to come off. */
private const val SystemSplashWaitMs = 1500L

/** Longest it holds for the paired-PC list (a DataStore read; normally a few ms). */
private const val PeersWaitMs = 400L

/**
 * "Live Handshake", the RemEx 3.0 splash (RemEx-8g6n0).
 *
 * Not a film: the real startup drawn as light. The mark ignites; each pulse is a real
 * reachability sweep rolling across a GPU dot lattice; paired PCs sit on an orbit as ghosts and
 * ignite as they actually answer, settling at a radius set by their measured round trip; the PC
 * the app is connecting to gets a reticle that snaps shut when the host acks the handshake; and
 * then the app opens OUT of that PC through a refractive portal. It ends when the app is ready,
 * never on a timer ([LiveHandshakeDirector]); a tap skips at any moment.
 *
 * Rendering: the backdrop is `res/raw/live_handshake_field.agsl` (byte-identical to the PC's
 * SkSL) through a [RuntimeShader]; everything else is vectors ported from the motion lab
 * (docs/specs/assets/live-handshake-lab.html). Every size is dp x density (bd:
 * splash-canvas-density-trap).
 *
 * @param signals what is actually happening (paired PCs, answers, the lock, readiness).
 * @param lens the content lens to drive during the portal exit, or null (preview: the portal just
 *   reveals whatever is behind the overlay).
 * @param continueFromSystemSplash start the clock when the system splash comes off, and pick the
 *   mark up where it was last drawn; false for the preview.
 */
@Composable
fun LiveHandshakeSplash(
    signals: LiveHandshakeSignals,
    lens: LiveHandshakeLensState?,
    continueFromSystemSplash: Boolean,
    onFinished: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val view = LocalView.current
    val density = LocalDensity.current
    val reduced = LocalReducedMotion.current
    val scheme = MaterialTheme.colorScheme
    val palette = remember(scheme) { LiveHandshakePalette.from(scheme) }
    val locale: Locale = LocalConfiguration.current.locales[0]
    val strings = remember(context.resources, locale) { LiveHandshakeStrings(context.resources, locale) }
    val snapshot by signals.state.collectAsState()
    val systemMarkRect by SystemSplashHandoff.markWindowRect.collectAsState()
    val latestOnFinished by rememberUpdatedState(onFinished)

    // Portrait for the splash's lifetime, like every other splash (SplashScreen's orchestrator).
    DisposableEffect(Unit) {
        val activity = context as? Activity ?: return@DisposableEffect onDispose {}
        val original = activity.requestedOrientation
        activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        onDispose { activity.requestedOrientation = original }
    }

    // Depth: game rotation vector, only while the splash is on screen, never under reduced motion.
    val parallax = remember { GyroParallax() }
    DisposableEffect(reduced) {
        if (!reduced) parallax.start(context)
        onDispose { parallax.stop() }
    }

    val px = density.density
    val textMeasurer = rememberTextMeasurer(cacheSize = 24)
    val monoFamily = FontFamily.Monospace
    val wordmarkFamily = MaterialTheme.typography.titleLarge.fontFamily
    val styles = remember(density, palette, wordmarkFamily) {
        with(density) {
            LiveHandshakeTextStyles(
                ring = TextStyle(fontFamily = monoFamily, fontWeight = FontWeight.Medium, fontSize = 9.dp.toSp()),
                label = TextStyle(
                    fontFamily = monoFamily, fontWeight = FontWeight.Medium,
                    fontSize = 10.5.dp.toSp(), letterSpacing = 0.6.dp.toSp(),
                ),
                wordmark = TextStyle(fontFamily = wordmarkFamily, fontWeight = FontWeight.SemiBold, fontSize = 22.dp.toSp()),
                status = TextStyle(
                    fontFamily = monoFamily, fontWeight = FontWeight.Medium,
                    fontSize = 10.5.dp.toSp(), letterSpacing = 1.6.dp.toSp(),
                ),
            )
        }
    }
    val wordmarkText = remember(context.resources) { context.resources.getString(R.string.app_name) }

    val fieldShader = remember { RuntimeShader(readRawText(context, R.raw.live_handshake_field)) }
    val fieldBrush = remember(fieldShader) { ShaderBrush(fieldShader) }
    val scene = remember { LiveHandshakeScene() }
    var clock by remember { mutableDoubleStateOf(0.0) }

    LaunchedEffect(Unit) {
        // Hold the first frame (the mark where the system splash left it) until the system
        // splash is off and the paired list is in; then t = 0.
        if (continueFromSystemSplash) {
            withTimeoutOrNull(SystemSplashWaitMs) { SystemSplashHandoff.done.first { it } }
        }
        withTimeoutOrNull(PeersWaitMs) { signals.state.first { it.peersLoaded } }
        val origin = androidx.compose.runtime.withFrameNanos { it }
        scene.begin(origin, reduced)
        var hapticDone = false
        while (true) {
            val frame = androidx.compose.runtime.withFrameNanos { it }
            val t = (frame - origin) / 1e9
            scene.step(t, signals.state.value)
            if (!hapticDone && !reduced && scene.lockedNow(t)) {
                hapticDone = true
                view.hapticCommandAcknowledged()
            }
            if (!reduced) parallax.advance(scene, t, px)
            val exit = scene.exitProgress(t)
            if (lens != null) {
                if (!reduced && exit != null) {
                    val e = min(1.0, exit / T.EXIT)
                    lens.set(
                        scene.exitX, scene.exitY,
                        scene.portalRadius(t).toFloat(),
                        M.lerp(1.12, 1.0, M.EmphasizedDecelerate(e)).toFloat(),
                    )
                } else {
                    lens.clear()
                }
            }
            clock = t
            val done = scene.director.doneAt(reduced)
            if (done != null && t >= done) {
                lens?.clear()
                latestOnFinished()
                break
            }
        }
    }

    Canvas(
        modifier = modifier
            .fillMaxSize()
            .onGloballyPositioned { scene.windowOffset = it.positionInWindow() }
            .pointerInput(signals) { detectTapGestures { signals.skip() } },
    ) {
        val t = clock
        scene.reduced = reduced
        scene.layout(size, px, if (continueFromSystemSplash && !reduced) systemMarkRect else null)
        scene.strings = strings
        scene.palette = palette
        if (!scene.started) scene.syncPeers(snapshot, strings)
        scene.ensureBrushes()
        drawField(scene, t, palette, fieldShader, fieldBrush, reduced)
        drawVectors(scene, t, palette, reduced, textMeasurer, styles, strings, wordmarkText)
    }
}

// ───────────────────────────────────────────────────────────────────────── scene

private class NodeState(
    val id: String,
    val name: String,
    val angle: Double,
    val appear: Double,
) {
    var answerAt: Double? = null
    var rttMs: Long? = null
    var radiusFactor = 1.0
    var suffix = ""
    var fullLabel: TextLayoutResult? = null
    var fullLabelText = ""
    var partial: TextLayoutResult? = null
    var partialText = ""
}

/** Mutable per-splash state: geometry, the peers on the orbit, and the director. Main thread only. */
private class LiveHandshakeScene {
    val director = LiveHandshakeDirector(exitFromMark = false)
    var reduced = false
    var originNanos = 0L
    var started = false

    // Geometry (px).
    var w = 0f
    var h = 0f
    var px = 1f
    var cx = 0f
    var cy = 0f
    var markW = 0f
    var rx = 0f
    var ry = 0f
    var windowOffset = Offset.Zero
    private var systemMark: Rect? = null
    private var systemMarkLocal: Rect? = null

    // Peers.
    var nodes: List<NodeState> = emptyList()
    private var peersKey: List<HandshakePeer>? = null
    var target: NodeState? = null
    var snapshot = HandshakeSnapshot()

    // Relative times (s), refreshed each step.
    var connectStartAt: Double? = null
    var linkedAt: Double? = null
    var failedAt: Double? = null
    var readyAt: Double? = null
    var skipAt: Double? = null
    var probeDoneAt: Double? = null

    /** When the lock-on is shown (see [syncPeers]); the real ack time is [linkedAt]. */
    var lockAt: Double? = null
    var strings: LiveHandshakeStrings? = null

    // Hand-off.
    var exitX = 0f
    var exitY = 0f
    private var exitFixed = false

    // Parallax (px).
    var parX = 0f
    var parY = 0f

    // Field rings scratch: x, y, start, strength per ring.
    val ringScratch = FloatArray(4 * 24)
    val rings = FloatArray(40)
    var ringCount = 0

    // Reused paths, dash patterns, strokes and brushes (rebuilt on a palette or density change,
    // never per frame).
    var ringDash: PathEffect? = null
    var ghostDash: PathEffect? = null
    var ringStroke: Stroke = Stroke()
    var reticleStroke: Stroke = Stroke()
    var ghostStroke: Stroke = Stroke()
    var litStroke: Stroke = Stroke()
    val markStroke = Stroke(width = 1.4f)
    val chevronStroke = Stroke(width = 4.8f, cap = StrokeCap.Round, join = StrokeJoin.Round)
    var palette: LiveHandshakePalette? = null
        set(value) {
            if (value != field) {
                field = value
                brushesValid = false
            }
        }
    private var brushesValid = false
    /** Accent glow: a unit-radius radial gradient at the origin, placed by transform. */
    var accentGlow: Brush = SolidColor(Color.Transparent)
    /** The mark's window-card gradient, in the mark's 108-unit space. */
    var windowBrush: Brush = SolidColor(Color.Transparent)
    /** The ignition sheen, centred on x = 0 in the mark's unit space; translated per frame. */
    val sheenBrush: Brush = Brush.linearGradient(
        0f to Color.White.copy(alpha = 0f),
        0.5f to Color.White.copy(alpha = 0.20f),
        1f to Color.White.copy(alpha = 0f),
        start = Offset(-16f, 20f), end = Offset(16f, 36f),
    )
    /** A lit PC glyph's fill, centred on the origin; translated per node. */
    var glyphFill: Brush = SolidColor(Color.Transparent)

    fun ensureBrushes() {
        val p = palette ?: return
        if (brushesValid) return
        brushesValid = true
        accentGlow = Brush.radialGradient(
            0f to p.accent, 1f to p.accent.copy(alpha = 0f),
            center = Offset.Zero, radius = 1f,
        )
        windowBrush = Brush.linearGradient(
            listOf(p.markA, p.markB),
            start = Offset(20f + 68f * 0.056f, 26f + 56f * 0.056f),
            end = Offset(20f + 68f * 0.944f, 26f + 56f * 0.944f),
        )
        val gw = 12f * px
        val gh = 8.5f * px
        glyphFill = Brush.linearGradient(
            listOf(p.primary.copy(alpha = 0.55f), p.accent.copy(alpha = 0.35f)),
            start = Offset(-gw, -gh), end = Offset(gw, gh),
        )
    }
    val holePath = Path()
    val reticlePath = Path()

    // Status line cache.
    var statusKey: HandshakeStatus? = null
    var statusText = ""
    var statusLayout: TextLayoutResult? = null
    var statusDots = -1
    var ringLabels: Array<TextLayoutResult?> = arrayOfNulls(3)
    var ringLabelsKey: Any? = null
    var wordmark: TextLayoutResult? = null

    fun begin(origin: Long, reduced: Boolean) {
        originNanos = origin
        this.reduced = reduced
        started = true
    }

    fun sec(nanos: Long?): Double? = nanos?.let { if (!started) 0.0 else max(0.0, (it - originNanos) / 1e9) }

    private var layoutOffset = Offset.Unspecified

    fun layout(size: Size, px: Float, markRect: Rect?) {
        if (size.width == w && size.height == h && this.px == px && markRect == systemMark &&
            layoutOffset == windowOffset
        ) return
        layoutOffset = windowOffset
        ringDash = PathEffect.dashPathEffect(floatArrayOf(1f * px, 5f * px))
        ghostDash = PathEffect.dashPathEffect(floatArrayOf(2.2f * px, 2.6f * px))
        ringStroke = Stroke(width = 1f * px, pathEffect = ringDash)
        reticleStroke = Stroke(width = 1.8f * px, cap = StrokeCap.Round, join = StrokeJoin.Round)
        ghostStroke = Stroke(width = 1.4f * px, pathEffect = ghostDash)
        litStroke = Stroke(width = 1.4f * px)
        brushesValid = false
        w = size.width
        h = size.height
        this.px = px
        cx = w / 2f
        cy = h * 0.40f
        markW = 104f * px
        rx = w * 0.37f
        ry = h * 0.215f
        systemMark = markRect
        systemMarkLocal = markRect?.translate(-windowOffset)
    }

    /** Mark centre and window width at [t]: eased from where the system splash left it. */
    fun markCenterX(t: Double): Float = continuity(t)?.let { k -> lerpF(systemMarkLocal!!.center.x, cx, k) } ?: cx
    fun markCenterY(t: Double): Float = continuity(t)?.let { k -> lerpF(systemMarkLocal!!.center.y, cy, k) } ?: cy
    fun markWidth(t: Double): Float = continuity(t)?.let { k -> lerpF(systemMarkLocal!!.width, markW, k) } ?: markW

    private fun continuity(t: Double): Float? {
        if (systemMarkLocal == null || reduced) return null
        if (t >= 0.45) return null
        return M.EmphasizedDecelerate(M.clamp01(t / 0.45)).toFloat()
    }

    fun syncPeers(s: HandshakeSnapshot, strings: LiveHandshakeStrings) {
        snapshot = s
        // The snapshot is immutable and copied on change, so identity is a cheap "peers changed".
        if (s.peers !== peersKey) {
            peersKey = s.peers
            val hash = M.hashStr(s.peers.joinToString("|") { it.name })
            nodes = s.peers.mapIndexed { i, p ->
                NodeState(p.id, p.name, M.peerAngle(i, s.peers.size, hash), 0.16 + i * 0.08)
            }
        }
        target = null
        val targetId = s.targetId
        if (targetId != null) for (n in nodes) if (n.id == targetId) target = n
        val realLinkedAt = sec(s.linkedAtNanos)
        val speed = hypot(w, h) / 1.25
        for (n in nodes) {
            val answer = s.answers[n.id]
            val answeredAt = if (answer == null) null else sec(answer.atNanos)
            val implicit = if (n === target) realLinkedAt else null
            // A PC that linked has answered, whatever the probe says (it may still be in flight).
            val at = if (answeredAt == null) implicit else if (implicit == null) answeredAt else min(answeredAt, implicit)
            // Presentation, not simulation: an answer that is already in when the splash appears
            // (the probe and the heartbeat both start under the system splash) is revealed when
            // the first pulse's wavefront reaches that PC, never before. Later answers show the
            // moment they land. The director only ever sees the real times.
            n.answerAt = if (at == null || reduced || speed <= 0.0) at else {
                val arrival = T.FIRST_PULSE + hypot(cos(n.angle) * rx, sin(n.angle) * ry) / speed
                max(at, arrival)
            }
            val rtt = answer?.rttMs
            if (rtt != n.rttMs) {
                n.rttMs = rtt
                n.radiusFactor = rtt?.let { M.radiusFor(it.toDouble()) } ?: 1.0
                n.suffix = rtt?.let { "  " + strings.rtt(it) } ?: ""
                n.fullLabel = null
                n.partial = null
            }
        }
        // The lock is shown once the target is seen to answer, and never before it happened.
        val tg = target
        lockAt = if (tg == null || realLinkedAt == null) null
        else if (reduced) realLinkedAt
        else max(realLinkedAt, (tg.answerAt ?: realLinkedAt) + 0.12)
    }

    fun step(t: Double, s: HandshakeSnapshot) {
        strings?.let { if (w > 0f) syncPeers(s, it) }
        snapshot = s
        connectStartAt = sec(s.connectStartAtNanos)
        linkedAt = sec(s.linkedAtNanos)
        failedAt = sec(s.failedAtNanos)
        readyAt = sec(s.readyAtNanos)
        skipAt = sec(s.skipAtNanos)
        probeDoneAt = sec(s.probeDoneAtNanos)
        val hasTarget = s.targetId != null && s.peers.any { it.id == s.targetId }
        director.step(
            t,
            DirectorInputs(
                peers = s.peers.size,
                hasTarget = hasTarget,
                readyAt = readyAt,
                linkedAt = if (hasTarget) linkedAt else null,
                failedAt = if (hasTarget) failedAt else null,
                skipAt = skipAt,
            ),
        )
        val handoff = director.handoffAt
        if (handoff != null && !exitFixed) {
            exitFixed = true
            val tg = target
            if (director.origin == ExitOrigin.Target && tg != null) {
                exitX = nodeX(tg, handoff)
                exitY = nodeY(tg, handoff)
            } else {
                exitX = cx
                exitY = cy
            }
        }
    }

    /**
     * The lock as shown: the host acked before any hand-off (the director's rule, on the real
     * time) and the reveal time [lockAt] has come.
     */
    fun lockedNow(t: Double): Boolean =
        target != null && director.isLinkedAt(Double.MAX_VALUE, linkedAt) && director.isLinkedAt(t, lockAt)

    /** Seconds since the hand-off, or null while still waiting. */
    fun exitProgress(t: Double): Double? = director.handoffAt?.let { if (t >= it) t - it else null }

    fun answered(n: NodeState, t: Double): Boolean {
        val a = n.answerAt ?: return false
        if (t < a) return false
        val h = director.handoffAt ?: return true
        return a < h || (n === target && lockedNow(t))
    }

    private fun f(n: NodeState, t: Double): Double {
        val a = n.answerAt ?: return 1.0
        if (t < a) return 1.0
        if (reduced) return n.radiusFactor
        return M.lerp(1.0, n.radiusFactor, M.spring(t - a))
    }

    fun nodeX(n: NodeState, t: Double): Float = (cx + cos(n.angle) * rx * f(n, t)).toFloat()
    fun nodeY(n: NodeState, t: Double): Float = (cy + sin(n.angle) * ry * f(n, t)).toFloat()

    fun portalRadius(t: Double): Double {
        val ex = exitX.toDouble()
        val ey = exitY.toDouble()
        val far = maxOf(hypot(ex, ey), hypot(w - ex, ey), hypot(ex, h - ey), hypot(w - ex, h - ey)) + 40.0 * px
        val e = M.clamp01((t - (director.handoffAt ?: t)) / T.EXIT)
        return M.lerp(10.0 * px, far, M.Portal(e))
    }

    /** Where silence becomes evidence: a failed connect, the probe giving up, or a lockless hand-off. */
    fun targetSilentAt(): Double? {
        val tg = target ?: return null
        var silent: Double? = failedAt
        fun consider(x: Double?) {
            if (x != null && (silent == null || x < silent!!)) silent = x
        }
        if (tg.answerAt == null) consider(probeDoneAt)
        val h = director.handoffAt
        val la = linkedAt
        if (h != null && (la == null || la > h)) consider(h)
        return silent
    }

    fun awakeCount(t: Double): Int = nodes.count { answered(it, t) }

    /** Collects the live field rings (most recent first, at most 10) into [rings]. */
    private var scratchCount = 0
    private var scratchNow = 0.0

    private fun addRing(x: Float, y: Float, start: Double, strength: Float) {
        if (start > scratchNow || scratchCount >= 24) return
        val o = scratchCount * 4
        ringScratch[o] = x
        ringScratch[o + 1] = y
        ringScratch[o + 2] = start.toFloat()
        ringScratch[o + 3] = strength
        scratchCount++
    }

    fun collectRings(t: Double) {
        scratchCount = 0
        scratchNow = t
        if (!reduced) {
            val handoff = director.handoffAt
            var k = 0
            while (true) {
                val tp = T.FIRST_PULSE + k * T.PULSE_PERIOD
                if (tp > t || (handoff != null && tp >= handoff)) break
                addRing(cx, cy, tp, 1.0f)
                k++
            }
            for (node in nodes) {
                val a = node.answerAt ?: continue
                if (handoff != null && a >= handoff) continue
                addRing(nodeX(node, a + 0.6), nodeY(node, a + 0.6), a, 0.5f)
            }
            val tg = target
            val la = lockAt
            if (tg != null && la != null && lockedNow(la) && (handoff == null || la <= handoff)) {
                addRing(nodeX(tg, la + 0.6), nodeY(tg, la + 0.6), la, 0.85f)
            }
            if (handoff != null) addRing(exitX, exitY, handoff, 1.4f)
        }
        val n = scratchCount
        // Selection sort by start, descending; n is tiny.
        val count = min(n, 10)
        for (i in 0 until count) {
            var best = i
            for (j in i + 1 until n) if (ringScratch[j * 4 + 2] > ringScratch[best * 4 + 2]) best = j
            if (best != i) for (c in 0 until 4) {
                val tmp = ringScratch[i * 4 + c]
                ringScratch[i * 4 + c] = ringScratch[best * 4 + c]
                ringScratch[best * 4 + c] = tmp
            }
        }
        rings.fill(0f)
        System.arraycopy(ringScratch, 0, rings, 0, count * 4)
        ringCount = count
    }
}

private fun lerpF(a: Float, b: Float, t: Float): Float = a + (b - a) * t

// ───────────────────────────────────────────────────────────────────────── field

private fun DrawScope.drawField(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    shader: RuntimeShader,
    brush: ShaderBrush,
    reduced: Boolean,
) {
    scene.collectRings(t)
    val exit = scene.exitProgress(t)
    val px = scene.px
    shader.setFloatUniform("uRes", size.width, size.height)
    shader.setFloatUniform("uTime", t.toFloat())
    shader.setFloatUniform("uPx", px)
    shader.setFloatUniform("uCenter", scene.markCenterX(t), scene.markCenterY(t))
    shader.setFloatUniform("uBg0", p.bg0.red, p.bg0.green, p.bg0.blue)
    shader.setFloatUniform("uBg1", p.bg1.red, p.bg1.green, p.bg1.blue)
    shader.setFloatUniform("uPri", p.primary.red, p.primary.green, p.primary.blue)
    shader.setFloatUniform("uAcc", p.accent.red, p.accent.green, p.accent.blue)
    shader.setFloatUniform("uRings", scene.rings)
    shader.setFloatUniform("uRingCount", scene.ringCount.toFloat())
    shader.setFloatUniform("uSpeed", (hypot(size.width, size.height) / 1.25f))
    shader.setFloatUniform("uIntro", if (reduced) 1f else M.EmphasizedDecelerate(M.clamp01(t / 0.9)).toFloat())
    if (!reduced && exit != null) {
        val e = M.clamp01(exit / T.EXIT)
        shader.setFloatUniform("uPortal", scene.exitX, scene.exitY, scene.portalRadius(t).toFloat(), 1f)
        shader.setFloatUniform("uZoom", M.Standard(e).toFloat())
    } else {
        shader.setFloatUniform("uPortal", 0f, 0f, 0f, 0f)
        shader.setFloatUniform("uZoom", 0f)
    }
    shader.setFloatUniform("uStill", if (reduced) 1f else 0f)
    shader.setFloatUniform("uPar", scene.parX, scene.parY)
    val alpha = if (reduced && exit != null) (1.0 - M.clamp01(exit / T.FADE_EXIT)).toFloat() else 1f
    shader.setFloatUniform("uAlpha", alpha)
    drawRect(brush)
}

// ───────────────────────────────────────────────────────────────────────── vectors

private class LiveHandshakeTextStyles(
    val ring: TextStyle,
    val label: TextStyle,
    val wordmark: TextStyle,
    val status: TextStyle,
)

private fun DrawScope.drawVectors(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    reduced: Boolean,
    tm: TextMeasurer,
    styles: LiveHandshakeTextStyles,
    strings: LiveHandshakeStrings,
    wordmarkText: String,
) {
    val exit = scene.exitProgress(t)
    var fade = 1f
    var zoom = 1f
    var portal = 0f
    if (exit != null) {
        if (reduced) {
            fade = (1.0 - M.clamp01(exit / T.FADE_EXIT)).toFloat()
        } else {
            val e = M.clamp01(exit / T.EXIT)
            zoom = (1.0 + 0.5 * M.Standard(e)).toFloat()
            fade = (1.0 - M.smooth(0.0, 0.55, e)).toFloat()
            portal = scene.portalRadius(t).toFloat()
        }
    }
    if (fade <= 0.001f) return
    val ex = scene.exitX
    val ey = scene.exitY
    if (exit != null && !reduced) {
        scene.holePath.reset()
        scene.holePath.addOval(Rect(Offset(ex, ey), portal / zoom))
        withTransform({
            scale(zoom, zoom, pivot = Offset(ex, ey))
            clipPath(scene.holePath, ClipOp.Difference)
        }) {
            drawVectorLayer(scene, t, p, reduced, fade, tm, styles, strings, wordmarkText)
        }
    } else {
        drawVectorLayer(scene, t, p, reduced, fade, tm, styles, strings, wordmarkText)
    }
}

private fun DrawScope.drawVectorLayer(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    reduced: Boolean,
    fade: Float,
    tm: TextMeasurer,
    styles: LiveHandshakeTextStyles,
    strings: LiveHandshakeStrings,
    wordmarkText: String,
) {
    val px = scene.px
    val handoff = scene.director.handoffAt
    val lastPulse = LiveHandshakeDirector.lastPulse(handoff, t, reduced)
    val env = if (lastPulse != null) M.pulseEnvelope(t - lastPulse).toFloat() else 0f
    val intro = if (reduced) 1f else M.EmphasizedDecelerate(M.clamp01(t / 0.9)).toFloat()
    val mcx = scene.markCenterX(t)
    val mcy = scene.markCenterY(t)
    val markW = scene.markWidth(t)

    if (scene.nodes.isNotEmpty()) drawRangeRings(scene, p, fade * 0.5f * intro, tm, styles, strings)
    drawBeamAndReticle(scene, t, p, reduced, fade, mcx, mcy, markW)
    drawNodes(scene, t, p, reduced, fade, mcx, mcy, tm, styles)
    drawMark(scene, t, p, reduced, env, mcx, mcy, markW, fade)
    drawWordmarkAndStatus(scene, t, p, fade * intro, tm, styles, strings, wordmarkText)
}

private fun DrawScope.drawRangeRings(
    scene: LiveHandshakeScene,
    p: LiveHandshakePalette,
    alpha: Float,
    tm: TextMeasurer,
    styles: LiveHandshakeTextStyles,
    strings: LiveHandshakeStrings,
) {
    val px = scene.px
    // Put the scale labels in the widest gap between nodes so they never sit under a label.
    var la = 0.0
    var bestGap = -1.0
    for (a in RingLabelAngles) {
        var gap = Double.MAX_VALUE
        for (n in scene.nodes) gap = min(gap, abs(atan2(sin(a - n.angle), cos(a - n.angle))))
        if (gap > bestGap) {
            bestGap = gap
            la = a
        }
    }
    val right = cos(la) < -0.1
    val dir = if (cos(la) == 0.0) 1f else sign(cos(la)).toFloat()
    if (scene.ringLabelsKey !== strings) {
        scene.ringLabelsKey = strings
        scene.ringLabels = Array(3) { i -> tm.measure(strings.rtt(RingMs[i].toLong()), styles.ring) }
    }
    for (i in RingMs.indices) {
        val f = M.radiusFor(RingMs[i]).toFloat()
        val w = scene.rx * f
        val h = scene.ry * f
        drawOval(
            color = p.primary.copy(alpha = 0.16f),
            topLeft = Offset(scene.cx - w, scene.cy - h),
            size = Size(w * 2, h * 2),
            alpha = alpha,
            style = scene.ringStroke,
        )
        val label = scene.ringLabels[i] ?: continue
        val x = scene.cx + (w * cos(la)).toFloat() + dir * 5f * px
        val baseline = scene.cy + (h * sin(la)).toFloat() - 3f * px
        drawText(
            label,
            color = p.primary.copy(alpha = 0.45f),
            topLeft = Offset(if (right) x - label.size.width else x, baseline - label.firstBaseline),
            alpha = alpha,
        )
    }
}

private val RingMs = doubleArrayOf(5.0, 25.0, 150.0)
private val RingLabelAngles = doubleArrayOf(0.0, PI / 4, -PI / 4, 3 * PI / 4, -3 * PI / 4, PI)

private fun DrawScope.drawBeamAndReticle(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    reduced: Boolean,
    fade: Float,
    mcx: Float,
    mcy: Float,
    markW: Float,
) {
    val tg = scene.target ?: return
    val cs = scene.connectStartAt ?: return
    if (t < cs) return
    val px = scene.px
    val nx = scene.nodeX(tg, t)
    val ny = scene.nodeY(tg, t)
    val dx = nx - mcx
    val dy = ny - mcy
    val len = hypot(dx, dy).coerceAtLeast(1f)
    val ux = dx / len
    val uy = dy / len
    val r0 = markW * 0.52f
    val r1 = 16f * px
    val x0 = mcx + ux * r0
    val y0 = mcy + uy * r0
    val x1 = nx - ux * r1
    val y1 = ny - uy * r1
    val grow = if (reduced) 1f else M.EmphasizedDecelerate(M.clamp01((t - cs) / 0.35)).toFloat()
    val locked = scene.lockedNow(t)
    val linkedAt = scene.lockAt ?: 0.0
    if (!locked) {
        val period = 9f * px
        val phase = (((-t * 70.0 * px) % period + period) % period).toFloat()
        drawLine(
            color = p.accent.copy(alpha = 0.55f),
            start = Offset(x0, y0),
            end = Offset(lerpF(x0, x1, grow), lerpF(y0, y1, grow)),
            strokeWidth = 1.3f * px,
            cap = StrokeCap.Round,
            pathEffect = PathEffect.dashPathEffect(floatArrayOf(2f * px, 7f * px), phase),
            alpha = fade,
        )
    } else {
        val la = t - linkedAt
        val flare = exp(-la * 4).toFloat()
        drawLine(
            p.accent.copy(alpha = 0.10f + 0.25f * flare), Offset(x0, y0), Offset(x1, y1),
            strokeWidth = (7f + 10f * flare) * px, cap = StrokeCap.Round, alpha = fade,
        )
        drawLine(
            p.accent.copy(alpha = 0.65f + 0.35f * flare), Offset(x0, y0), Offset(x1, y1),
            strokeWidth = 1.6f * px, cap = StrokeCap.Round, alpha = fade,
        )
        if (!reduced) {
            for (k in 0 until 6) {
                val out = k < 3
                val st = linkedAt + (if (out) 0.0 else 0.2) + (k % 3) * 0.07
                val u = (t - st) / 0.24
                if (u < 0 || u > 1) continue
                val e = M.Standard(u).toFloat()
                val pxX = if (out) lerpF(x0, x1, e) else lerpF(x1, x0, e)
                val pxY = if (out) lerpF(y0, y1, e) else lerpF(y1, y0, e)
                drawGlowDot(scene, Offset(pxX, pxY), 2.4f * px, 10f * px, if (out) Color.White else p.accent, fade)
            }
        }
    }
    // Reticle: hunts while connecting, snaps shut on the lock.
    val sizeDp: Float
    val rot: Float
    val ra: Float
    if (!locked) {
        sizeDp = (28.0 + 2.5 * sin(t * 7)).toFloat()
        rot = (t * 1.1).toFloat()
        ra = 0.75f * grow
    } else {
        val k = if (reduced) 1.0 else M.spring(t - linkedAt)
        sizeDp = M.lerp(28.0, 19.0, k).toFloat()
        rot = M.lerp((linkedAt * 1.1) % (PI / 2), 0.0, k).toFloat()
        ra = 1f
    }
    val s = sizeDp * px
    val l = 7f * px
    val path = scene.reticlePath
    path.reset()
    for (q in 0 until 4) {
        // Corner q, rotated by q * 90 degrees about the node.
        val c = cos(q * PI / 2).toFloat()
        val sn = sin(q * PI / 2).toFloat()
        fun rx(x: Float, y: Float) = x * c - y * sn
        fun ry(x: Float, y: Float) = x * sn + y * c
        path.moveTo(rx(s, s - l), ry(s, s - l))
        path.lineTo(rx(s, s), ry(s, s))
        path.lineTo(rx(s - l, s), ry(s - l, s))
    }
    withTransform({
        translate(nx, ny)
        rotateRad(rot, pivot = Offset.Zero)
    }) {
        drawPath(
            path, p.accent, alpha = fade * ra,
            style = scene.reticleStroke,
        )
    }
}

/** An accent glow of [radius] at [center]: the scene's cached unit gradient, scaled into place. */
private fun DrawScope.drawAccentGlow(scene: LiveHandshakeScene, center: Offset, radius: Float, alpha: Float) {
    if (radius <= 0f || alpha <= 0f) return
    withTransform({
        translate(center.x, center.y)
        scale(radius, radius, pivot = Offset.Zero)
    }) {
        drawCircle(scene.accentGlow, radius = 1f, center = Offset.Zero, alpha = alpha.coerceAtMost(1f))
    }
}

/** A dot with a soft accent glow, standing in for the lab's canvas shadowBlur. */
private fun DrawScope.drawGlowDot(
    scene: LiveHandshakeScene,
    center: Offset,
    radius: Float,
    glow: Float,
    core: Color,
    alpha: Float,
) {
    drawAccentGlow(scene, center, radius + glow, 0.55f * alpha)
    drawCircle(core, radius, center, alpha = alpha)
}

private fun DrawScope.drawNodes(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    reduced: Boolean,
    fade: Float,
    mcx: Float,
    mcy: Float,
    tm: TextMeasurer,
    styles: LiveHandshakeTextStyles,
) {
    val px = scene.px
    for (n in scene.nodes) {
        if (t < n.appear && !reduced) continue
        val ga = if (reduced) 1f else M.smooth(n.appear, n.appear + 0.3, t).toFloat()
        val answered = scene.answered(n, t)
        val la = if (answered) t - (n.answerAt ?: t) else 0.0
        val lit = if (!answered) 0f else if (reduced) 1f else M.smooth(0.0, 0.12, la).toFloat()
        val x = scene.nodeX(n, t)
        val y = scene.nodeY(n, t)
        val center = Offset(x, y)
        if (answered && !reduced) {
            val fl = exp(-la * 5).toFloat()
            drawAccentGlow(scene, center, (26f + 30f * fl) * px, (0.35f * lit + 0.4f * fl).coerceIn(0f, 1f) * fade)
            // The echo: the reply travelling home to the mark.
            val u = la / 0.3
            if (u < 1) {
                val e = M.Standard(u).toFloat()
                drawGlowDot(
                    scene, Offset(lerpF(x, mcx, e), lerpF(y, mcy, e)), 2.6f * px, 12f * px,
                    p.accent, fade * (1f - u.toFloat() * 0.6f),
                )
            }
        } else if (answered) {
            drawAccentGlow(scene, center, 26f * px, 0.3f * fade)
        }
        drawPcGlyph(scene, center, px, lit, p.primary, p.accent, (if (answered) 1f else 0.42f) * ga, fade)

        // Label on the outer side of the orbit, the round trip typing in after the name.
        val above = y < scene.cy - 4f * px
        val baseline = y + (if (above) -20f else 28f) * px
        val suffix = if (answered) n.suffix else ""
        val full = n.name + suffix
        if (n.fullLabel == null || n.fullLabelText != full) {
            n.fullLabelText = full
            n.fullLabel = tm.measure(full, styles.label)
        }
        val fullLayout = n.fullLabel!!
        val shown = if (answered && !reduced) min(suffix.length, floor(la * 40).toInt()) else suffix.length
        val typing = answered && !reduced && shown < suffix.length
        val layout = if (!typing) fullLayout else {
            val txt = n.name + suffix.substring(0, shown)
            if (n.partial == null || n.partialText != txt) {
                n.partialText = txt
                n.partial = tm.measure(txt, styles.label)
            }
            n.partial!!
        }
        val fullW = fullLayout.size.width.toFloat()
        val m = 14f * px
        val lo = m + fullW / 2f
        val hi = scene.w - m - fullW / 2f
        val lx = if (lo <= hi) x.coerceIn(lo, hi) else scene.w / 2f
        val textW = layout.size.width.toFloat()
        val a = fade * ga * (if (answered) 1f else 0.4f)
        drawText(
            layout,
            color = if (answered) p.ink else p.primary,
            topLeft = Offset(lx - textW / 2f, baseline - layout.firstBaseline),
            alpha = a,
        )
        if (typing) {
            drawRect(
                p.accent,
                topLeft = Offset(lx + textW / 2f + 2f * px, baseline - 9f * px),
                size = Size(5f * px, 11f * px),
                alpha = a,
            )
        }
    }
}

/** A paired PC: a small rounded window with an accent dot, dashed while it is only a ghost. */
private fun DrawScope.drawPcGlyph(
    scene: LiveHandshakeScene,
    center: Offset,
    px: Float,
    lit: Float,
    col: Color,
    acc: Color,
    ghost: Float,
    fade: Float,
) {
    val w = 24f * px
    val h = 17f * px
    val topLeft = Offset(center.x - w / 2f, center.y - h / 2f)
    val corner = CornerRadius(4f * px)
    if (lit > 0f) {
        withTransform({ translate(center.x, center.y) }) {
            drawRoundRect(
                brush = scene.glyphFill,
                topLeft = Offset(-w / 2f, -h / 2f), size = Size(w, h), cornerRadius = corner, alpha = lit * fade,
            )
        }
    }
    drawRoundRect(
        color = if (lit > 0.5f) acc else col,
        topLeft = topLeft, size = Size(w, h), cornerRadius = corner, alpha = ghost * fade,
        style = if (lit < 0.5f) scene.ghostStroke else scene.litStroke,
    )
    drawCircle(
        color = if (lit > 0.5f) acc else col,
        radius = 1.4f * px,
        center = Offset(topLeft.x + 4.2f * px, topLeft.y + 4f * px),
        alpha = max(lit, ghost * 0.6f) * fade,
    )
}

/** The brand mark in the lab's 108-unit space, painted in the seed palette, with its ignition. */
private fun DrawScope.drawMark(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    reduced: Boolean,
    env: Float,
    cx: Float,
    cy: Float,
    markW: Float,
    fade: Float,
) {
    drawAccentGlow(scene, Offset(cx, cy), markW * (1.1f + 0.25f * env), (0.10f + 0.22f * env) * fade)
    val s = markW / 68f * (1f + 0.035f * env)
    val blinkOn = floor(t / 0.53).toInt() % 2 == 0
    val cursorA = if (reduced) 1f else if (env > 0f) 1f else if (blinkOn) 1f else 0.18f
    val windowBrush = scene.windowBrush
    withTransform({
        translate(cx, cy)
        scale(s, s, pivot = Offset.Zero)
        translate(-54f, -54f)
    }) {
        drawPath(SplashBrand.WindowPath, windowBrush, alpha = fade)
        drawPath(
            SplashBrand.WindowPath, SplashBrand.WindowStroke,
            alpha = SplashBrand.WindowStrokeAlpha * fade, style = scene.markStroke,
        )
        drawPath(SplashBrand.Dot1Path, p.accent, alpha = fade)
        drawPath(SplashBrand.Dot2Path, SplashBrand.SlateLo, alpha = fade)
        drawPath(SplashBrand.Dot3Path, SplashBrand.SlateHi, alpha = fade)
        drawPath(
            SplashBrand.ChevronPath, p.accent, alpha = fade,
            style = scene.chevronStroke,
        )
        drawPath(SplashBrand.RStemPath, SplashBrand.OffWhite, alpha = fade)
        drawPath(SplashBrand.RBowlPath, SplashBrand.OffWhite, alpha = fade)
        drawPath(SplashBrand.RLegPath, SplashBrand.OffWhite, alpha = fade)
        drawPath(SplashBrand.RHolePath, windowBrush, alpha = fade)
        if (env > 0f) drawAccentGlow(scene, Offset(76f, 61f), 6f + 12f * env, 0.6f * env * fade)
        drawPath(SplashBrand.CursorPath, p.accent, alpha = fade * cursorA)
        // Ignition: a sheen sweeps the window as the mark comes alive.
        if (!reduced && t < 0.6) {
            val x = M.lerp(-10.0, 125.0, M.EmphasizedDecelerate(M.clamp01((t - 0.04) / 0.5))).toFloat()
            clipPath(SplashBrand.WindowPath) {
                withTransform({ translate(x, 0f) }) {
                    drawRect(
                        brush = scene.sheenBrush,
                        topLeft = Offset(-x, 0f), size = Size(108f, 108f), alpha = fade,
                    )
                }
            }
        }
    }
}

private fun DrawScope.drawWordmarkAndStatus(
    scene: LiveHandshakeScene,
    t: Double,
    p: LiveHandshakePalette,
    alpha: Float,
    tm: TextMeasurer,
    styles: LiveHandshakeTextStyles,
    strings: LiveHandshakeStrings,
    wordmarkText: String,
) {
    val px = scene.px
    val by = scene.h * 0.84f
    val wm = scene.wordmark ?: tm.measure(wordmarkText, styles.wordmark).also { scene.wordmark = it }
    drawText(wm, color = p.ink, topLeft = Offset(scene.cx - wm.size.width / 2f, by - wm.firstBaseline), alpha = alpha)

    val tg = scene.target
    val status = LiveHandshakeStatusModel.statusAt(
        t = t,
        peers = scene.nodes.size,
        awake = scene.awakeCount(t),
        targetName = tg?.name,
        linked = scene.lockedNow(t),
        targetRttMs = tg?.rttMs,
        targetSilentAt = scene.targetSilentAt(),
    )
    val dots = if (status is HandshakeStatus.Pinging) 1 + floor(t * 3).toInt() % 3 else 0
    if (status != scene.statusKey || dots != scene.statusDots) {
        scene.statusKey = status
        scene.statusDots = dots
        val base = strings.status(status)
        // Pad to a fixed width so the centred line does not jitter as the dots cycle.
        scene.statusText = if (status is HandshakeStatus.Pinging) base + ".".repeat(dots) + " ".repeat(3 - dots) else base
        scene.statusLayout = tm.measure(scene.statusText, styles.status)
    }
    val sl = scene.statusLayout ?: return
    drawText(
        sl,
        color = if (status.hot) p.accent else p.muted,
        topLeft = Offset(scene.cx - sl.size.width / 2f, by + 26f * px - sl.firstBaseline),
        alpha = alpha,
    )
}

// ───────────────────────────────────────────────────────────────────────── strings

/** The splash's localized text, upper-cased for the status line with the current locale. */
internal class LiveHandshakeStrings(private val res: Resources, private val locale: Locale) {
    fun rtt(ms: Long): String = res.getString(R.string.splash_live_rtt_ms, ms.toInt())

    fun status(status: HandshakeStatus): String = when (status) {
        HandshakeStatus.Starting -> res.getString(R.string.splash_live_starting)
        is HandshakeStatus.Pinging -> res.getQuantityString(R.plurals.splash_live_pinging, status.peers, status.peers)
        is HandshakeStatus.Awake -> res.getString(R.string.splash_live_awake, status.awake, status.peers)
        is HandshakeStatus.Linked -> res.getString(
            R.string.splash_live_linked,
            status.rttMs?.let { status.name + " · " + rtt(it) } ?: status.name,
        )
        is HandshakeStatus.NotAnswering -> res.getString(R.string.splash_live_not_answering, status.name)
        HandshakeStatus.NonePaired -> res.getString(R.string.splash_live_none_paired)
    }.uppercase(locale)
}

// ───────────────────────────────────────────────────────────────────────── parallax

/**
 * Gyro depth: the game rotation vector (no magnetometer, so no compass jumps), relative to how
 * the phone was held when the splash appeared, low-passed and mapped to at most 9 dp of lattice
 * shift. Registered only while the splash is on screen.
 */
private class GyroParallax : SensorEventListener {
    private var manager: SensorManager? = null
    private val rotation = FloatArray(9)
    private val orientation = FloatArray(3)
    private var basePitch = Float.NaN
    private var baseRoll = Float.NaN

    @Volatile private var targetX = 0f
    @Volatile private var targetY = 0f

    fun start(context: Context) {
        val sm = context.getSystemService(Context.SENSOR_SERVICE) as? SensorManager ?: return
        val sensor = sm.getDefaultSensor(Sensor.TYPE_GAME_ROTATION_VECTOR) ?: return
        manager = sm
        sm.registerListener(this, sensor, SensorManager.SENSOR_DELAY_GAME)
    }

    fun stop() {
        manager?.unregisterListener(this)
        manager = null
    }

    override fun onSensorChanged(event: SensorEvent) {
        SensorManager.getRotationMatrixFromVector(rotation, event.values)
        SensorManager.getOrientation(rotation, orientation)
        val pitch = orientation[1]
        val roll = orientation[2]
        if (basePitch.isNaN()) {
            basePitch = pitch
            baseRoll = roll
        }
        targetX = ((roll - baseRoll) / MaxTiltRad).coerceIn(-1f, 1f)
        targetY = ((pitch - basePitch) / MaxTiltRad).coerceIn(-1f, 1f)
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit

    /** One low-pass step toward the tilt, plus the lab's slow ambient drift. */
    fun advance(scene: LiveHandshakeScene, t: Double, px: Float) {
        val ambientX = (sin(t * 1000.0 / 3300.0) * 2.5).toFloat() * px
        val ambientY = (cos(t * 1000.0 / 4100.0) * 2.5).toFloat() * px
        scene.parX = lerpF(scene.parX, targetX * 9f * px + ambientX, 0.06f)
        scene.parY = lerpF(scene.parY, targetY * 9f * px + ambientY, 0.06f)
    }

    private companion object {
        const val MaxTiltRad = 0.35f
    }
}

// ───────────────────────────────────────────────────────────────────────── preview variant

/**
 * The splash as the Personalization preview plays it: live signals, already "ready" (the preview
 * dialog is the app), no lens — the portal simply reveals the dialog's backdrop — and closing on
 * finish. Same variant signature as the other splashes so [com.clindsay94.remex.ui.screens.SplashScreen]
 * can route to it.
 */
@Composable
fun SplashLiveHandshake(onFinished: () -> Unit, skipRequested: Boolean, onSkipConsumed: () -> Unit) {
    val context = LocalContext.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    val signals = remember { androidLiveHandshakeSignals(context.applicationContext, scope).also { it.markReady() } }
    LaunchedEffect(skipRequested) {
        if (skipRequested) {
            signals.skip()
            onSkipConsumed()
        }
    }
    LiveHandshakeSplash(
        signals = signals,
        lens = null,
        continueFromSystemSplash = false,
        onFinished = onFinished,
    )
}
