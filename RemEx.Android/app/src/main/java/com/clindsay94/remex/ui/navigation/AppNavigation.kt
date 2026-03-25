package com.clindsay94.remex.ui.navigation

import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.clindsay94.remex.ui.screens.AppLauncherScreen
import com.clindsay94.remex.ui.screens.ConnectionScreen
import com.clindsay94.remex.ui.screens.DashboardScreen
import com.clindsay94.remex.ui.screens.PersonalizationScreen
import com.clindsay94.remex.ui.screens.RemoteControlScreen
import com.clindsay94.remex.ui.screens.RemoteDesktopScreen
import com.clindsay94.remex.ui.screens.RemoteMouseScreen
import com.clindsay94.remex.ui.screens.SplashScreen
import com.clindsay94.remex.ui.screens.TaskManagerScreen

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation() {
    val navController = rememberNavController()
    val navBackStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = navBackStackEntry?.destination?.route

    // Hide navigation for splash screen
    val showNav = currentRoute != Screen.Splash.route

    Scaffold(
        bottomBar = {
            if (showNav) {
                NavigationBar {
                    navItems.forEach { screen ->
                        // Only show main screens in bottom bar to avoid overcrowding
                        // (Connection, Dashboard, RemoteControl, RemoteMouse, AppLauncher, TaskManager, RemoteDesktop, Personalization)
                        // We'll show a subset or all if they fit. User mentioned "bottom bar anyway".
                        // Let's show the most important ones.
                        val mainScreens = listOf(
                            Screen.Connection,
                            Screen.Dashboard,
                            Screen.RemoteDesktop,
                            Screen.RemoteControl,
                            Screen.RemoteMouse,
                            Screen.AppLauncher
                        )
                        if (screen in mainScreens) {
                            NavigationBarItem(
                                icon = { Icon(screen.icon, contentDescription = screen.title) },
                                selected = currentRoute == screen.route,
                                onClick = {
                                    navController.navigate(screen.route) {
                                        popUpTo(navController.graph.startDestinationId) {
                                            saveState = true
                                        }
                                        launchSingleTop = true
                                        restoreState = true
                                    }
                                }
                            )
                        }
                    }
                }
            }
        },
        floatingActionButton = {
            if (showNav) {
                FloatingActionButton(
                    onClick = { navController.navigate(Screen.Personalization.route) },
                    containerColor = MaterialTheme.colorScheme.tertiaryContainer,
                    contentColor = MaterialTheme.colorScheme.onTertiaryContainer
                ) {
                    Icon(Icons.Default.Settings, contentDescription = "Settings")
                }
            }
        }
    ) { innerPadding ->
        NavHost(
            navController = navController,
            startDestination = Screen.Splash.route,
            modifier = Modifier.padding(if (showNav) innerPadding else PaddingValues(0.dp))
        ) {
            composable(Screen.Splash.route) {
                SplashScreen(
                    onFinished = {
                        navController.navigate(Screen.Dashboard.route) {
                            popUpTo(Screen.Splash.route) { inclusive = true }
                            launchSingleTop = true
                        }
                    }
                )
            }
            composable(Screen.Dashboard.route) {
                DashboardScreen()
            }
            composable(Screen.Connection.route) {
                ConnectionScreen()
            }
            composable(Screen.RemoteControl.route) {
                RemoteControlScreen()
            }
            composable(Screen.RemoteMouse.route) {
                RemoteMouseScreen()
            }
            composable(Screen.AppLauncher.route) {
                AppLauncherScreen()
            }
            composable(Screen.TaskManager.route) {
                TaskManagerScreen()
            }
            composable(Screen.RemoteDesktop.route) {
                RemoteDesktopScreen()
            }
            composable(Screen.Personalization.route) {
                PersonalizationScreen()
            }
        }
    }
}
