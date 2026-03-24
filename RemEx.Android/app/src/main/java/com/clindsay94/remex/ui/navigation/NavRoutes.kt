package com.clindsay94.remex.ui.navigation

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.Settings
import androidx.compose.ui.graphics.vector.ImageVector

sealed class Screen(val route: String, val title: String, val icon: ImageVector) {
    object Connection : Screen("connection", "Connection", Icons.Default.Settings)
    object Dashboard : Screen("dashboard", "Dashboard", Icons.Default.Dashboard)
    object RemoteControl : Screen("remote_control", "Remote Control", Icons.Default.Mouse)
    object AppLauncher : Screen("app_launcher", "App Launcher", Icons.AutoMirrored.Filled.Launch)
    object TaskManager : Screen("task_manager", "Task Manager", Icons.AutoMirrored.Filled.List)
    object RemoteDesktop : Screen("remote_desktop", "Remote Desktop", Icons.Default.Computer)
}

val navItems = listOf(
    Screen.Connection,
    Screen.Dashboard,
    Screen.RemoteControl,
    Screen.AppLauncher,
    Screen.TaskManager,
    Screen.RemoteDesktop
)
