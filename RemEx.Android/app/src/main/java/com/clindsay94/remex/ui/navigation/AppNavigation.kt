package com.clindsay94.remex.ui.navigation

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
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

    // Hide navigation for splash screen and remote desktop
    val showNav = currentRoute != Screen.Splash.route && currentRoute != Screen.RemoteDesktop.route

    Scaffold(
        bottomBar = {
            if (showNav) {
                Surface(
                    tonalElevation = 3.dp,
                    color = MaterialTheme.colorScheme.surface
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(80.dp)
                            .horizontalScroll(rememberScrollState()),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
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
