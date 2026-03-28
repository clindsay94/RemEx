package com.clindsay94.remex.ui.navigation

import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
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
import com.clindsay94.remex.ui.screens.SettingsScreen
import com.clindsay94.remex.ui.screens.SplashScreen
import com.clindsay94.remex.ui.screens.TaskManagerScreen

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation() {
    val navController = rememberNavController()
    val navBackStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = navBackStackEntry?.destination?.route

    // Hide navigation for splash screen and remote desktop
    val showNav = currentRoute != Screen.Splash.route && currentRoute != Screen.RemoteDesktop.route
    var showOverflowMenu by remember { mutableStateOf(false) }

    Scaffold(
        bottomBar = {
            if (showNav) {
                NavigationBar {
                    navItems.forEach { screen ->
                        val isSelected = currentRoute == screen.route
                        NavigationBarItem(
                            icon = { Icon(screen.icon, contentDescription = screen.title) },
                            label = { Text(screen.title, style = MaterialTheme.typography.labelSmall) },
                            selected = isSelected,
                            alwaysShowLabel = false,
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
                    // Overflow "More" item for secondary destinations
                    NavigationBarItem(
                        icon = {
                            Icon(Icons.Default.MoreVert, contentDescription = "More")
                            DropdownMenu(
                                expanded = showOverflowMenu,
                                onDismissRequest = { showOverflowMenu = false }
                            ) {
                                secondaryNavItems.forEach { screen ->
                                    DropdownMenuItem(
                                        text = { Text(screen.title) },
                                        leadingIcon = { Icon(screen.icon, contentDescription = null) },
                                        onClick = {
                                            showOverflowMenu = false
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
                        },
                        label = { Text("More", style = MaterialTheme.typography.labelSmall) },
                        selected = secondaryNavItems.any { it.route == currentRoute },
                        alwaysShowLabel = false,
                        onClick = { showOverflowMenu = !showOverflowMenu }
                    )
                }
            }
        },
        floatingActionButton = {
            // Only show FAB on Dashboard for quick Personalization access
            if (showNav && currentRoute == Screen.Dashboard.route) {
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
            composable(Screen.Settings.route) {
                SettingsScreen()
            }
        }
    }
}
