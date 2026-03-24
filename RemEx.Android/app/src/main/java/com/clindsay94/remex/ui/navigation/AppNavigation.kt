package com.clindsay94.remex.ui.navigation

import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.*
import com.clindsay94.remex.ui.screens.DashboardScreen
import com.clindsay94.remex.ui.screens.ConnectionScreen
import com.clindsay94.remex.ui.screens.RemoteControlScreen
import com.clindsay94.remex.ui.screens.AppLauncherScreen
import com.clindsay94.remex.ui.screens.TaskManagerScreen
import com.clindsay94.remex.ui.screens.RemoteDesktopScreen
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation() {
    val navController = rememberNavController()
    val drawerState = rememberDrawerState(initialValue = DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val navBackStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = navBackStackEntry?.destination?.route

    val onMenuClick: () -> Unit = {
        scope.launch { drawerState.open() }
    }

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet {
                Spacer(modifier = Modifier.height(12.dp))
                navItems.forEach { screen ->
                    NavigationDrawerItem(
                        icon = { Icon(screen.icon, contentDescription = null) },
                        label = { Text(screen.title) },
                        selected = currentRoute == screen.route,
                        onClick = {
                            scope.launch {
                                drawerState.close()
                                navController.navigate(screen.route) {
                                    popUpTo(navController.graph.startDestinationId) {
                                        saveState = true
                                    }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            }
                        },
                        modifier = Modifier.padding(NavigationDrawerItemDefaults.ItemPadding)
                    )
                }
            }
        }
    ) {
        NavHost(
            navController = navController,
            startDestination = Screen.Dashboard.route
        ) {
            composable(Screen.Dashboard.route) {
                DashboardScreen(onMenuClick = onMenuClick)
            }
            composable(Screen.Connection.route) {
                ConnectionScreen(onMenuClick = onMenuClick)
            }
            composable(Screen.RemoteControl.route) {
                RemoteControlScreen(onMenuClick = onMenuClick)
            }
            composable(Screen.AppLauncher.route) {
                AppLauncherScreen(onMenuClick = onMenuClick)
            }
            composable(Screen.TaskManager.route) {
                TaskManagerScreen(onMenuClick = onMenuClick)
            }
            composable(Screen.RemoteDesktop.route) {
                RemoteDesktopScreen(onMenuClick = onMenuClick)
            }
        }
    }
}
