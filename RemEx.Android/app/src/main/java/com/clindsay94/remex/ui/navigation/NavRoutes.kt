package com.clindsay94.remex.ui.navigation

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.School
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.ui.graphics.vector.ImageVector
import com.clindsay94.remex.R

sealed class Screen(val route: String, @param:StringRes val titleRes: Int, val icon: ImageVector) {
        object Splash : Screen("splash", R.string.screen_splash_title, Icons.Default.Dashboard)
        object Connection :
                Screen("connection", R.string.screen_connection_title, Icons.Default.Wifi)
        object Dashboard :
                Screen("dashboard", R.string.screen_dashboard_title, Icons.Default.Dashboard)
        object RemoteControl :
                Screen(
                        "remote_control",
                        R.string.screen_remote_control_title,
                        Icons.Default.TouchApp
                )
        object RemoteMouse :
                Screen("remote_mouse", R.string.screen_remote_mouse_title, Icons.Default.Mouse)
        object AppLauncher :
                Screen(
                        "app_launcher",
                        R.string.screen_app_launcher_title,
                        Icons.AutoMirrored.Filled.Launch
                )
        object TaskManager :
                Screen(
                        "task_manager",
                        R.string.screen_task_manager_title,
                        Icons.AutoMirrored.Filled.List
                )
        object RemoteDesktop :
                Screen(
                        "remote_desktop",
                        R.string.screen_remote_desktop_title,
                        Icons.Default.Computer
                )
        object Personalization :
                Screen(
                        "personalization",
                        R.string.screen_personalization_title,
                        Icons.Default.Palette
                )
        object Settings :
                Screen("settings", R.string.screen_settings_title, Icons.Default.Settings)
        object Tutorial : Screen("tutorial", R.string.screen_tutorial_title, Icons.Default.School)
        object Faq :
                Screen("faq", R.string.screen_faq_title, Icons.AutoMirrored.Filled.HelpOutline)
        object About : Screen("about", R.string.screen_about_title, Icons.Default.Info)
        object QrScanner :
                Screen("qr_scanner", R.string.screen_qr_scanner_title, Icons.Default.QrCodeScanner)
        object Pairing :
                Screen("pairing", R.string.pairing_title, Icons.Default.QrCodeScanner)
}

/** Primary navigation destinations — shown in NavigationBar / NavigationRail / NavigationDrawer. */
val navItems =
        listOf(
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
        listOf(
                Screen.RemoteDesktop,
                Screen.RemoteMouse,
                Screen.Settings,
                Screen.Personalization,
                Screen.Faq,
                Screen.About,
        )
