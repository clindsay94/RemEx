package com.clindsay94.remex.ui.theme

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExperimentalSharedTransitionApi
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.SharedTransitionScope
import androidx.compose.animation.SizeTransform
import androidx.compose.animation.animateBounds
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ButtonShapes
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.IconButtonShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.LookaheadScope
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import kotlin.math.abs
import kotlin.math.sign

/**
 * The app's motion rules in one place (refresh spec "Principles", phase 6, RemEx-wqo7a.7).
 *
 * Durations and easings are never written here: every spec comes from [MaterialTheme.motionScheme]
 * (expressive springs normally, the calmer standard scheme under "Remove animations"). What lives
 * here is the choreography those specs drive, plus the one rule every piece of it obeys: when the
 * system says "Remove animations" ([LocalReducedMotion]), each of these collapses to an instant
 * change instead of merely playing faster.
 *
 * The pure half ([tabScrollPlan], [valueTrend]) is plain JVM so it is unit-tested; the composable
 * half below only turns those answers into Compose transitions.
 */
object RemexMotion {
    /** Shared-axis travel, the M3 motion guidance's 30 dp. */
    const val SHARED_AXIS_OFFSET_DP = 30

    /** How small a swapped icon gets on its way out (and starts on its way in) in [RemexSwap]. */
    const val SWAP_SCALE = 0.6f

    /**
     * How a bottom-nav tab click moves the pager: [snapTo] first (instantly), then animate the rest
     * of the way when [animate] is true. Null [snapTo] means start from where the pager already is.
     */
    data class TabScrollPlan(val snapTo: Int?, val animate: Boolean)

    /**
     * A tab click moves exactly one page's width in the direction of tab order, the same slide a
     * swipe makes, however far apart the tabs are: Home to Control used to scroll through Desktop
     * and Apps, composing both on the way. A far jump snaps to the neighbour of the target first.
     * Under reduced motion the pager jumps straight to the target and nothing animates.
     */
    fun tabScrollPlan(current: Int, target: Int, reducedMotion: Boolean): TabScrollPlan =
        when {
            current == target -> TabScrollPlan(snapTo = null, animate = false)
            reducedMotion -> TabScrollPlan(snapTo = target, animate = false)
            abs(target - current) > 1 -> TabScrollPlan(snapTo = target - (target - current).sign, animate = true)
            else -> TabScrollPlan(snapTo = null, animate = true)
        }

    /**
     * What a newly settled pager page does to the selected tab: [select] it (null: leave the
     * selection alone), and whether the tab click's scroll target is now [reached] and can be let go.
     */
    data class SettledTabSync(val select: Int?, val reached: Boolean)

    /**
     * Syncs the selected tab from the pager's settled page, except while a tab click's scroll is on
     * its way to [tabScrollTarget].
     *
     * A far click snaps to the target's neighbour first ([tabScrollPlan]), and the snap SETTLES the
     * pager there; the settled page then stays on the neighbour for the whole slide. Copied into the
     * selection, it re-keyed the tab-scroll effect, which cancelled the slide and found the pager
     * already "on" the selected (wrong) tab: tapping Control from Home left the bar on Apps with the
     * pager stuck between pages. So, with a target in flight, only the target settling counts; a
     * user drag or a cancelled slide drops the target (the caller does that), and every settle syncs
     * again.
     *
     * Nothing syncs while [scrollInProgress]: [settledPage] is the page a scroll STARTED from until it
     * ends, so a drag that grabs the pager mid-slide would otherwise copy the old page into the
     * selection, re-key the tab-scroll effect and fight the fling.
     */
    fun settledTabSync(settledPage: Int, tabScrollTarget: Int?, scrollInProgress: Boolean): SettledTabSync =
        when {
            scrollInProgress -> SettledTabSync(select = null, reached = false)
            tabScrollTarget == null -> SettledTabSync(select = settledPage, reached = false)
            tabScrollTarget == settledPage -> SettledTabSync(select = settledPage, reached = true)
            else -> SettledTabSync(select = null, reached = false)
        }

    private val LEADING_NUMBER =Regex("""^\s*([+-]?\d+(?:[.,]\d+)?)""")

    /** The number a formatted sensor reading starts with ("45.2 °C" -> 45.2), or null when none. */
    fun leadingNumber(text: String): Double? =
        LEADING_NUMBER.find(text)?.groupValues?.get(1)?.replace(',', '.')?.toDoubleOrNull()

    /**
     * Which way a changed reading rolls: +1 when it went up (the new value rises in from below),
     * -1 when it went down, 0 when the two can't be compared (a placeholder, or no change in the
     * number), which cross-fades instead.
     */
    fun valueTrend(from: String, to: String): Int {
        val old = leadingNumber(from) ?: return 0
        val new = leadingNumber(to) ?: return 0
        return when {
            new > old -> 1
            new < old -> -1
            else -> 0
        }
    }
}

/**
 * Transitions for the routes on top of the tabs, built once per motion scheme / direction so the
 * NavHost lambdas (which are not @Composable) just hand them back.
 */
@Immutable
class RemexNavMotion(
    /** Shared-axis X forward: the new screen comes in from the end side, the old one leaves to the start. */
    val enter: EnterTransition,
    val exit: ExitTransition,
    /** Shared-axis X back: the reverse. */
    val popEnter: EnterTransition,
    val popExit: ExitTransition,
    /** A plain cross-fade, for routes that must not move (the remote-desktop stream, splash, tutorial). */
    val fadeIn: EnterTransition,
    val fadeOut: ExitTransition,
    /** Modal routes (QR scanner, pairing) rise from the bottom edge. */
    val modalEnter: EnterTransition,
    val modalExit: ExitTransition,
)

/**
 * [RemexNavMotion] for the current motion scheme, layout direction and reduced-motion setting.
 * Under reduced motion every transition is None: the route changes on the next frame.
 */
@Composable
fun rememberRemexNavMotion(): RemexNavMotion {
    val reduced = LocalReducedMotion.current
    val scheme = MaterialTheme.motionScheme
    val density = LocalDensity.current
    // Forward is towards the end edge: right-to-left layouts mirror the axis.
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    return remember(reduced, scheme, density, rtl) {
        if (reduced) {
            RemexNavMotion(
                enter = EnterTransition.None,
                exit = ExitTransition.None,
                popEnter = EnterTransition.None,
                popExit = ExitTransition.None,
                fadeIn = EnterTransition.None,
                fadeOut = ExitTransition.None,
                modalEnter = EnterTransition.None,
                modalExit = ExitTransition.None,
            )
        } else {
            val travel = with(density) { RemexMotion.SHARED_AXIS_OFFSET_DP.dp.roundToPx() } * (if (rtl) -1 else 1)
            // Enters on the default tier and exits on the fast tier: the outgoing screen is gone
            // before the incoming one settles, which is the shared-axis fade-through.
            val slideIn = scheme.defaultSpatialSpec<androidx.compose.ui.unit.IntOffset>()
            val slideOut = scheme.fastSpatialSpec<androidx.compose.ui.unit.IntOffset>()
            val fadeInSpec = scheme.defaultEffectsSpec<Float>()
            val fadeOutSpec = scheme.fastEffectsSpec<Float>()
            RemexNavMotion(
                enter = slideInHorizontally(slideIn) { travel } + fadeIn(fadeInSpec),
                exit = slideOutHorizontally(slideOut) { -travel } + fadeOut(fadeOutSpec),
                popEnter = slideInHorizontally(slideIn) { -travel } + fadeIn(fadeInSpec),
                popExit = slideOutHorizontally(slideOut) { travel } + fadeOut(fadeOutSpec),
                fadeIn = fadeIn(fadeInSpec),
                fadeOut = fadeOut(fadeOutSpec),
                modalEnter = slideInVertically(slideIn) { it } + fadeIn(fadeInSpec),
                modalExit = slideOutVertically(slideOut) { it } + fadeOut(fadeOutSpec),
            )
        }
    }
}

/**
 * A sensor reading that rolls when its number changes (phase 6): up when it rose, down when it fell,
 * a short fade when the two can't be compared. Only the text animates, so a 1 Hz telemetry tick
 * costs one short transition inside this tile and nothing at all while the value holds still.
 * Under reduced motion it is a plain [Text].
 */
@Composable
fun AnimatedValueText(
    text: String,
    style: TextStyle,
    modifier: Modifier = Modifier,
    color: Color = Color.Unspecified,
    fontWeight: FontWeight? = null,
    maxLines: Int = Int.MAX_VALUE,
) {
    if (LocalReducedMotion.current) {
        Text(text, modifier = modifier, color = color, fontWeight = fontWeight, style = style, maxLines = maxLines)
        return
    }
    val scheme = MaterialTheme.motionScheme
    AnimatedContent(
        targetState = text,
        modifier = modifier,
        transitionSpec = {
            val effects = scheme.fastEffectsSpec<Float>()
            when (val trend = RemexMotion.valueTrend(initialState, targetState)) {
                0 -> fadeIn(effects) togetherWith fadeOut(effects)
                else -> {
                    val spatial = scheme.fastSpatialSpec<androidx.compose.ui.unit.IntOffset>()
                    (slideInVertically(spatial) { height -> trend * height / 2 } + fadeIn(effects)) togetherWith
                        (slideOutVertically(spatial) { height -> -trend * height / 2 } + fadeOut(effects))
                }
            }
        },
        label = "animatedValueText",
    ) { value ->
        Text(value, color = color, fontWeight = fontWeight, style = style, maxLines = maxLines)
    }
}

/**
 * The M3 Expressive press morph for a button (phase 6): the pill squares off a little while held.
 * Under reduced motion the pressed shape is the resting one, so nothing moves. For buttons only;
 * cards never morph (refresh spec: shapes are accents, not containers).
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun rememberRemexButtonShapes(): ButtonShapes {
    val shapes = ButtonDefaults.shapes()
    return if (LocalReducedMotion.current) ButtonShapes(shapes.shape, shapes.shape) else shapes
}

/**
 * The icon-button half of [rememberRemexButtonShapes]: the circle squares off a little while held,
 * and holds still under reduced motion.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun rememberRemexIconButtonShapes(): IconButtonShapes {
    val shapes = IconButtonDefaults.shapes()
    return if (LocalReducedMotion.current) IconButtonShapes(shapes.shape, shapes.shape) else shapes
}

/**
 * Swaps one small piece of content for another (play for pause, pin for unpin, add for close) with
 * the outgoing one shrinking away as the incoming one grows in, on the fast tiers of the motion
 * scheme (phase 6). Keyed on [targetState], so it only animates when the state really changes, and
 * a plain swap under reduced motion. Meant for icons and other small, fixed-size content; the
 * container does not resize.
 */
@Composable
fun <T> RemexSwap(
    targetState: T,
    modifier: Modifier = Modifier,
    label: String = "remexSwap",
    content: @Composable (T) -> Unit,
) {
    if (LocalReducedMotion.current) {
        androidx.compose.foundation.layout.Box(modifier, contentAlignment = Alignment.Center) { content(targetState) }
        return
    }
    val scheme = MaterialTheme.motionScheme
    AnimatedContent(
        targetState = targetState,
        modifier = modifier,
        contentAlignment = Alignment.Center,
        transitionSpec = {
            val effects = scheme.fastEffectsSpec<Float>()
            val spatial = scheme.fastSpatialSpec<Float>()
            (fadeIn(effects) + scaleIn(spatial, initialScale = RemexMotion.SWAP_SCALE)) togetherWith
                (fadeOut(effects) + scaleOut(spatial, targetScale = RemexMotion.SWAP_SCALE)) using
                SizeTransform(clip = false)
        },
        label = label,
    ) { state ->
        content(state)
    }
}

/**
 * Lets a card in a plain (non-lazy) list glide to its new place when the list re-sorts or a
 * neighbour comes or goes: the non-lazy counterpart of `animateItem` (phase 6). The caller wraps
 * the list in a [LookaheadScope] and keys each card. Returns [Modifier] unchanged under reduced
 * motion, so cards just appear where they belong.
 */
@OptIn(ExperimentalSharedTransitionApi::class)
@Composable
fun rememberAnimateBoundsModifier(lookaheadScope: LookaheadScope): Modifier {
    if (LocalReducedMotion.current) return Modifier
    val spec = MaterialTheme.motionScheme.fastSpatialSpec<androidx.compose.ui.geometry.Rect>()
    return Modifier.animateBounds(lookaheadScope = lookaheadScope, boundsTransform = { _, _ -> spec })
}

/** The [SharedTransitionScope] around the NavHost, for container transforms; null outside it. */
@OptIn(ExperimentalSharedTransitionApi::class)
val LocalRemexSharedTransitionScope = staticCompositionLocalOf<SharedTransitionScope?> { null }

/** The animated scope of the NavHost route this content belongs to; null outside a route. */
val LocalRemexRouteAnimatedScope = compositionLocalOf<AnimatedVisibilityScope?> { null }

/** The one container-transform key in use: Home's Open Sensors card grows into the Sensors route. */
const val SENSORS_CONTAINER_KEY = "sensors_container"

/**
 * The modifier that makes this element one end of a container transform (phase 6): a card and the
 * screen it opens share [key], and the NavHost route change morphs one into the other.
 *
 * Content is scaled to the moving bounds rather than re-measured every frame, so the Sensors grid
 * is laid out once, at its real size. Returns [Modifier] unchanged (an ordinary route change) under
 * reduced motion or outside the NavHost's shared-transition scope, e.g. in a preview.
 */
@OptIn(ExperimentalSharedTransitionApi::class)
@Composable
fun rememberContainerTransformModifier(key: String, shape: Shape): Modifier {
    val shared = LocalRemexSharedTransitionScope.current ?: return Modifier
    val route = LocalRemexRouteAnimatedScope.current ?: return Modifier
    if (LocalReducedMotion.current) return Modifier
    val state = shared.rememberSharedContentState(key)
    return with(shared) {
        Modifier.sharedBounds(
            sharedContentState = state,
            animatedVisibilityScope = route,
            resizeMode = SharedTransitionScope.ResizeMode.scaleToBounds(),
            clipInOverlayDuringTransition = OverlayClip(shape),
        )
    }
}
