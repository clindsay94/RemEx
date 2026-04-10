package com.clindsay94.remex.ui.navigation

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.HelpOutline
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.ui.graphics.vector.ImageVector

sealed class Screen(val route: String, val title: String, val icon: ImageVector) {
    object Splash : Screen("splash", "Splash", Icons.Default.Dashboard)
    object Connection : Screen("connection", "Connection", Icons.Default.Settings)
    object Dashboard : Screen("dashboard", "RemEx Home Base", Icons.Default.Dashboard)
    object RemoteControl : Screen("remote_control", "Remote Control", Icons.Default.TouchApp)
    object RemoteMouse : Screen("remote_mouse", "Remote Mouse", Icons.Default.Mouse)
    object AppLauncher : Screen("app_launcher", "App Launcher", Icons.AutoMirrored.Filled.Launch)
    object TaskManager : Screen("task_manager", "Task Manager", Icons.AutoMirrored.Filled.List)
    object RemoteDesktop : Screen("remote_desktop", "Remote Desktop", Icons.Default.Computer)
    object Personalization : Screen("personalization", "Personalization", Icons.Default.Palette)
    object Settings : Screen("settings", "Settings", Icons.Default.Settings)
    object Tutorial : Screen("tutorial", "Tutorial", Icons.Default.Dashboard)
    object Faq : Screen("faq", "FAQ", Icons.AutoMirrored.Filled.HelpOutline)
    object QrScanner : Screen("qr_scanner", "Scan QR Code", Icons.Default.QrCodeScanner)
}

/** Bottom navigation bar items — App Launcher replaces old Connection/Settings tab. */
val navItems = listOf(
    Screen.Dashboard,
    Screen.AppLauncher,
    Screen.RemoteDesktop,
    Screen.TaskManager,
    Screen.RemoteControl
)

/** Overflow (3-dot) menu: Remote Mouse, Settings, and FAQ. */
val secondaryNavItems = listOf(
    Screen.RemoteMouse,
    Screen.Settings,
    Screen.Faq
)
