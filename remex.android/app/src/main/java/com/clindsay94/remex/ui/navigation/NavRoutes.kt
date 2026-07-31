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

/**
 * A navigable route.
 *
 * A plain [Screen] carries only its route, because that is all most destinations need. Splash,
 * Connection, Tutorial, QrScanner and Pairing are reached programmatically and never appear in a
 * navigation surface, so they have no label or icon to give TO ONE.
 *
 * That is not the same as being untitled: Connection, QrScanner and Pairing all render their own
 * heading from their own screen. What they do not need is a second copy of it here, feeding a
 * navigation item that does not exist — which is precisely what this split removed.
 */
sealed class Screen(val route: String) {
    object Splash : Screen("splash")
    object Connection : Screen("connection")
    object Tutorial : Screen("tutorial")
    object QrScanner : Screen("qr_scanner")
    object Pairing : Screen("pairing")

    // ── Destinations that appear in a navigation surface ──────────────────────
    object Dashboard :
            NavDestination("dashboard", R.string.screen_dashboard_title, Icons.Default.Dashboard)
    object RemoteControl :
            NavDestination(
                    "remote_control",
                    R.string.screen_remote_control_title,
                    Icons.Default.TouchApp
            )
    object RemoteMouse :
            NavDestination("remote_mouse", R.string.screen_remote_mouse_title, Icons.Default.Mouse)
    object AppLauncher :
            NavDestination(
                    "app_launcher",
                    R.string.screen_app_launcher_title,
                    Icons.AutoMirrored.Filled.Launch
            )
    object TaskManager :
            NavDestination(
                    "task_manager",
                    R.string.screen_task_manager_title,
                    Icons.AutoMirrored.Filled.List
            )
    object RemoteDesktop :
            NavDestination(
                    "remote_desktop",
                    R.string.screen_remote_desktop_title,
                    Icons.Default.Computer
            )
    object Personalization :
            NavDestination(
                    "personalization",
                    R.string.screen_personalization_title,
                    Icons.Default.Palette
            )
    object Settings :
            NavDestination("settings", R.string.screen_settings_title, Icons.Default.Settings)
    object Faq :
            NavDestination("faq", R.string.screen_faq_title, Icons.AutoMirrored.Filled.HelpOutline)
    object About : NavDestination("about", R.string.screen_about_title, Icons.Default.Info)
    object FileTransfer :
            NavDestination(
                    "file_transfer",
                    R.string.screen_file_transfer_title,
                    Icons.Default.FolderOpen,
            )
}

/**
 * A route that is rendered in a navigation surface, and therefore needs a label and an icon.
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
 * Now a plain route cannot carry a title at all, and the lists below are typed so a destination
 * without one cannot be added to them.
 */
sealed class NavDestination(
        route: String,
        @param:StringRes val titleRes: Int,
        val icon: ImageVector,
) : Screen(route)

/**
 * Primary navigation destinations — shown in NavigationBar / NavigationRail / NavigationDrawer.
 *
 * ORDER IS LOAD-BEARING: `AppNavigation` derives pager indices from `navItems.indexOfFirst`, so
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
