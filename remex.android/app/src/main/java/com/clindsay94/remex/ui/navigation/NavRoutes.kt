package com.clindsay94.remex.ui.navigation

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.ui.graphics.vector.ImageVector
import com.clindsay94.remex.R
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
 * Connection, Tutorial, QrScanner and ShareDiagnostics are reached programmatically and never
 * appear in a navigation surface. That is not the same as being untitled — they render their own
 * headings — they just have no navigation item to feed (RemEx-5reo).
 *
 * Pairing is [PairingRoute] below rather than an object here: it is the one destination that
 * carries arguments.
 */
sealed class Screen {
    @Serializable data object Splash : Screen()

    @Serializable data object Connection : Screen()

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

    // ── Destinations that appear in a navigation surface ──────────────────────
    @Serializable
    data object Dashboard : NavDestination() {
        override val titleRes = R.string.screen_dashboard_title
        override val icon = Icons.Default.Dashboard
    }

    @Serializable
    data object RemoteControl : NavDestination() {
        override val titleRes = R.string.screen_remote_control_title
        override val icon = Icons.Default.TouchApp
    }

    @Serializable
    data object RemoteMouse : NavDestination() {
        override val titleRes = R.string.screen_remote_mouse_title
        override val icon = Icons.Default.Mouse
    }

    @Serializable
    data object AppLauncher : NavDestination() {
        override val titleRes = R.string.screen_app_launcher_title
        override val icon = Icons.AutoMirrored.Filled.Launch
    }

    @Serializable
    data object TaskManager : NavDestination() {
        override val titleRes = R.string.screen_task_manager_title
        override val icon = Icons.AutoMirrored.Filled.List
    }

    @Serializable
    data object RemoteDesktop : NavDestination() {
        override val titleRes = R.string.screen_remote_desktop_title
        override val icon = Icons.Default.Computer
    }

    @Serializable
    data object Personalization : NavDestination() {
        override val titleRes = R.string.screen_personalization_title
        override val icon = Icons.Default.Palette
    }

    @Serializable
    data object Settings : NavDestination() {
        override val titleRes = R.string.screen_settings_title
        override val icon = Icons.Default.Settings
    }

    @Serializable
    data object Faq : NavDestination() {
        override val titleRes = R.string.screen_faq_title
        override val icon = Icons.AutoMirrored.Filled.HelpOutline
    }

    @Serializable
    data object About : NavDestination() {
        override val titleRes = R.string.screen_about_title
        override val icon = Icons.Default.Info
    }

    @Serializable
    data object FileTransfer : NavDestination() {
        override val titleRes = R.string.screen_file_transfer_title
        override val icon = Icons.Default.FolderOpen
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
 * Primary navigation destinations — shown in NavigationBar / NavigationRail / NavigationDrawer.
 *
 * ORDER IS LOAD-BEARING: `AppNavigation` derives pager indices from the position in this list, so
 * reordering silently changes which tab a swipe lands on.
 */
val navItems =
        listOf<NavDestination>(
                Screen.Dashboard,
                Screen.RemoteControl,
                Screen.AppLauncher,
                Screen.TaskManager,
        )

/**
 * Overflow destinations — shown in the "More" bottom sheet on compact (NavigationBar) layout, or
 * appended below a divider in NavigationRail / NavigationDrawer on larger layouts.
 */
val moreItems =
        listOf<NavDestination>(
                Screen.FileTransfer,
                Screen.RemoteDesktop,
                Screen.RemoteMouse,
                Screen.Settings,
                Screen.Personalization,
                Screen.Faq,
                Screen.About,
        )

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
