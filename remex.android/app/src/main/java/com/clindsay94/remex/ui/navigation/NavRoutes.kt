package com.clindsay94.remex.ui.navigation

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.automirrored.filled.Notes
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Route
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.ui.graphics.vector.ImageVector
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.WifiGlyph
import kotlinx.serialization.Serializable

/**
 * A navigable destination, typed (RemEx-mt43).
 *
 * Every destination is a `@Serializable` object handed to navigation-compose's type-safe API —
 * `composable<Screen.Dashboard>`, `navController.navigate(Screen.Settings)` — so there is no route
 * string to concatenate, mistype, or read back with a silent fallback. Identity is the object
 * itself; the navigation surface matches with `NavDestination.hasRoute(screen::class)` rather than
 * comparing strings. (The former `Screen("dashboard")` strings were load-bearing for exactly those
 * comparisons; nothing persisted them, so nothing needed a compatibility path.)
 *
 * A plain [Screen] carries no label or icon, because most destinations need neither: Splash,
 * Tutorial, QrScanner, ShareDiagnostics, RemoteDesktop, Personalization and About are reached
 * programmatically and never appear in a navigation surface. That is not the same as being untitled — they render their own
 * headings — they just have no navigation item to feed (RemEx-5reo).
 *
 * Pairing is [PairingRoute] below rather than an object here: it is the one destination that
 * carries arguments.
 */
sealed class Screen {
    @Serializable data object Splash : Screen()

    @Serializable data object Tutorial : Screen()

    @Serializable data object QrScanner : Screen()

    /**
     * Reached only from Settings → Help, never from a navigation surface (RemEx-0iww).
     *
     * Sharing a diagnostics report is something a user does when they are already being walked
     * through a problem; putting it in the More list would offer it to everyone else, permanently,
     * for the sake of one visit.
     */
    @Serializable data object ShareDiagnostics : Screen()

    /**
     * The full-screen stream (RemEx-wqo7a.2). Not a pager page and not in any navigation surface:
     * the Desktop tab's "Start streaming" opens it on top of the tabs, and Back returns there. It
     * stays its own route so the stream keeps the screen, the SurfaceView and the gestures to
     * itself, with no pager swipe or navigation bar around it (docs/REGRESSION-GUARDS.md, remote
     * desktop UI).
     */
    @Serializable data object RemoteDesktop : Screen()

    /**
     * Reached from Settings → Help → About, so it no longer has a More entry (RemEx-wqo7a.2).
     * Personalization went the same way and has no route of its own any more: Settings shows it
     * as its Personalization category.
     */
    @Serializable data object About : Screen()

    // ── Destinations that appear in a navigation surface ──────────────────────

    /**
     * Home (refresh spec, Navigation slot 1): the PC at a glance. Phase 2 lands the PC card and
     * the Open Sensors card; phase 3 fills in the rest (RemEx-wqo7a.2).
     */
    @Serializable
    data object Home : PrimaryDestination() {
        override val titleRes = R.string.screen_home_title
        override val icon = Icons.Default.Home
    }

    /**
     * Desktop: a Stream | Trackpad page. Stream opens [RemoteDesktop]; Trackpad is the old Remote
     * Mouse screen, now a mode of this tab rather than a destination of its own.
     */
    @Serializable
    data object Desktop : PrimaryDestination() {
        override val titleRes = R.string.screen_remote_desktop_title
        override val icon = Icons.Default.Computer
    }

    @Serializable
    data object AppLauncher : PrimaryDestination() {
        override val titleRes = R.string.screen_app_launcher_title
        override val icon = Icons.AutoMirrored.Filled.Launch
    }

    /** Control: a Commands | Processes segmented page (see [ControlSegment]). */
    @Serializable
    data object Control : PrimaryDestination() {
        override val titleRes = R.string.nav_control_label
        override val icon = Icons.Default.TouchApp
    }

    /** The full Sensors canvas. Reached from Home's Open Sensors card and from More. */
    @Serializable
    data object Dashboard : NavDestination() {
        override val titleRes = R.string.screen_dashboard_title
        override val icon = Icons.Default.Dashboard
    }

    @Serializable
    data object Connection : NavDestination() {
        override val titleRes = R.string.screen_connection_title
        override val icon = WifiGlyph
    }

    @Serializable
    data object Settings : NavDestination() {
        override val titleRes = R.string.screen_settings_title
        override val icon = Icons.Default.Settings
    }

    /**
     * The PC's logs and its own checks, read-only, with Logs | Diagnostics tabs (RemEx-pp4cm.13). In
     * [moreItems], after Connection: it is what you open when the PC is misbehaving, so it sits next to
     * the connection page and ahead of Settings and Help.
     */
    @Serializable
    data object PcDiagnostics : NavDestination() {
        override val titleRes = R.string.screen_pc_diagnostics_title
        override val icon = Icons.AutoMirrored.Filled.Notes
    }

    @Serializable
    data object Faq : NavDestination() {
        override val titleRes = R.string.screen_faq_title
        override val icon = Icons.AutoMirrored.Filled.HelpOutline
    }

    @Serializable
    data object FileTransfer : NavDestination() {
        override val titleRes = R.string.screen_file_transfer_title
        override val icon = Icons.Default.FolderOpen
    }

    /**
     * Routines (RemEx 3.0, routines spec 1.2 and 2.1). First in [moreItems], never a fifth primary:
     * the bar already holds four primaries plus More (Home, Desktop, Apps, Control). The list, gallery, editor, history and run
     * detail all live under this one destination as panes of a list-detail scaffold (see
     * `ui/routines/RoutinesScreen.kt`), so a phone gets one pane at a time and a tablet two.
     */
    @Serializable
    data object Routines : NavDestination() {
        override val titleRes = R.string.screen_routines_title
        override val icon = Icons.Default.Route
    }
}

/**
 * A destination that is rendered in a navigation surface, and therefore needs a label and an icon.
 *
 * Splitting this out of [Screen] is the point of RemEx-5reo. Every destination used to be REQUIRED to
 * supply `titleRes` and `icon`, but only the ones listed in [navItems] or [moreItems] ever had them
 * read — the other five supplied a title and an icon that nothing could display. The compiler could
 * not tell the two cases apart, because the unreachability was expressed by a list literal further
 * down the file rather than by the type, so a reader at the definition site had no way to know.
 *
 * That is not a tidiness complaint; it manufactured work that looked mandatory:
 * - `screen_splash_title` and `screen_tutorial_title` were translated into every locale for a
 *   constructor argument no user could ever see, purely because deleting the keys would have broken
 *   compilation of an argument that was never read.
 * - A QR-scanner title existed only to feed that same unread argument, and was filed as a
 *   screen-reader accessibility bug.
 *
 * Now a plain destination cannot carry a title at all, and the lists below are typed so a
 * destination without one cannot be added to them.
 *
 * ABSTRACT VALS RATHER THAN CONSTRUCTOR PARAMETERS, and it is serialization that decides. The
 * serialization plugin refuses a `@Serializable` object whose superclass has only parameterized
 * constructors, and an `ImageVector` constructor property could never be serializable anyway. As
 * overridden vals they live outside the (empty) object serializers entirely — route identity is
 * the object, display is the property, and neither leaks into the other.
 */
sealed class NavDestination : Screen() {
    @get:StringRes
    abstract val titleRes: Int
    abstract val icon: ImageVector
}

/**
 * A [NavDestination] with a page in `PrimaryDestinationsPager` (RemEx-740mr).
 *
 * Splitting this out of [NavDestination] is what makes a tab without a page a compile error rather
 * than a first-swipe crash. `PrimaryDestinationsPager`'s `when` is over this sealed type, with no
 * `else` branch — the compiler demands a branch for every subclass. Before this, `navItems` was a
 * `List<NavDestination>` and the pager's exhaustive `when` still needed an `else -> error(...)`,
 * because nothing tied "is in navItems" to "the sealed type the pager switches on": a fifth
 * `NavDestination` added to `navItems` compiled fine and only failed on the user's first swipe.
 */
sealed class PrimaryDestination : NavDestination()

/**
 * Primary navigation destinations — shown in NavigationBar / NavigationRail / NavigationDrawer.
 *
 * ORDER IS LOAD-BEARING: `AppNavigation` derives pager indices from the position in this list, so
 * reordering silently changes which tab a swipe lands on.
 */
val navItems: List<PrimaryDestination> =
        listOf(
                Screen.Home,
                Screen.Desktop,
                Screen.AppLauncher,
                Screen.Control,
        )

/**
 * Overflow destinations — shown in the "More" bottom sheet on compact (NavigationBar) layout, or
 * appended below a divider in NavigationRail / NavigationDrawer on larger layouts.
 */
val moreItems =
        listOf<NavDestination>(
                // First on purpose (routines spec 1.2, R-UX-01; NavRoutesRoutinesPlacementTest).
                Screen.Routines,
                // The full Sensors canvas, also one tap from Home's Open Sensors card.
                Screen.Dashboard,
                Screen.FileTransfer,
                Screen.Connection,
                Screen.PcDiagnostics,
                Screen.Settings,
                Screen.Faq,
                // Personalization and About are not here: Settings already links to both
                // (its Personalization category and Help → About).
        )

/**
 * The two halves of the Control tab (refresh spec, Control). Order is the segmented button order.
 * Each segment's page title is its own screen title, so the header reads "Commands" or "Processes"
 * while the tab itself is labelled "Control" (cohesion spec decision 1).
 */
enum class ControlSegment(@get:StringRes val titleRes: Int) {
        Commands(R.string.screen_remote_control_title),
        Processes(R.string.screen_task_manager_title),
}

/** The two modes of the Desktop tab. Order is the segmented button order. */
enum class DesktopMode(@get:StringRes val labelRes: Int) {
        Stream(R.string.desktop_mode_stream),
        // The same word the trackpad surface itself shows.
        Trackpad(R.string.remote_mouse_trackpad_label),
}

/**
 * The pairing destination, a data class because it is the only route that carries arguments
 * (RemEx-mt43).
 *
 * The stringly form this replaces was `"pairing/{host}/{port}"` plus a `navArgument` block and
 * `arguments?.getString(...) ?: ""` reads — the silent fallbacks RemEx-667p spent eleven tests
 * refusing. A typed route cannot arrive with a missing argument at all, so the fallback question
 * disappears on the happy path.
 *
 * CONSTRUCTION IS STILL GATED ON VALIDATION. The type system proves the arguments are present, not
 * that they are usable: an empty host or an out-of-range port still produces a pairing screen that
 * looks operational and cannot succeed. `AppNavigation` runs
 * [com.clindsay94.remex.ui.PairingRouteArgs.parse] before constructing one of these, and
 * `PairingRouteArgsTest` pins that call with a source scan. Deep links (RemEx-z58g), which arrive as
 * strings from outside the type system, go through the same `parse` — that is why it survives the
 * migration.
 *
 * Not a [Screen]: nothing navigates to it from a surface, and keeping it outside the sealed
 * hierarchy keeps the exhaustive `when` over pager pages honest.
 */
@Serializable
data class PairingRoute(val host: String, val port: Int)
