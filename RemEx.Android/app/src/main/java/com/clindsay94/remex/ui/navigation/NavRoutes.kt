package com.clindsay94.remex.ui.navigation

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.ui.graphics.vector.ImageVector

sealed class Screen(val route: String, val title: String, val icon: ImageVector) {
    object Splash : Screen("splash", "Splash", Icons.Default.Dashboard)
    object Connection : Screen("connection", "Connection", Icons.Default.Settings)
    object Dashboard : Screen("dashboard", "RemEx Home Base", Icons.Default.Dashboard)
    object RemoteControl : Screen("remote_control", "Remote Control", Icons.Default.Mouse)
    object RemoteMouse : Screen("remote_mouse", "Remote Mouse", Icons.Default.TouchApp)
    object AppLauncher : Screen("app_launcher", "App Launcher", Icons.AutoMirrored.Filled.Launch)
    object TaskManager : Screen("task_manager", "Task Manager", Icons.AutoMirrored.Filled.List)
    object RemoteDesktop : Screen("remote_desktop", "Remote Desktop", Icons.Default.Computer)
    object Personalization : Screen("personalization", "Personalization", Icons.Default.Palette)
}

val navItems = listOf(
    Screen.Connection,
    Screen.Dashboard,
    Screen.RemoteControl,
    Screen.RemoteMouse,
    Screen.AppLauncher,
    Screen.TaskManager,
    Screen.RemoteDesktop,
    Screen.Personalization
)
