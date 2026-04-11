package com.clindsay94.remex.ui.navigation

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
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
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.screens.AppLauncherScreen
import com.clindsay94.remex.ui.screens.ConnectionScreen
import com.clindsay94.remex.ui.screens.DashboardScreen
import com.clindsay94.remex.ui.screens.FaqScreen
import com.clindsay94.remex.ui.screens.PersonalizationScreen
import com.clindsay94.remex.ui.screens.RemoteControlScreen
import com.clindsay94.remex.ui.screens.RemoteDesktopScreen
import com.clindsay94.remex.ui.screens.RemoteMouseScreen
import com.clindsay94.remex.ui.screens.SettingsScreen
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.ConnectionViewModel
import com.clindsay94.remex.ui.screens.QrScannerScreen
import com.clindsay94.remex.ui.screens.SplashScreen
import com.clindsay94.remex.ui.screens.TaskManagerScreen
import com.clindsay94.remex.ui.screens.TutorialScreen
import kotlinx.coroutines.launch

// Routes that require an active PC connection to be useful
private val connectionRequiredRoutes = setOf(
    Screen.AppLauncher.route,
    Screen.RemoteDesktop.route,
    Screen.TaskManager.route,
    Screen.RemoteControl.route,
    Screen.RemoteMouse.route,
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation() {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    // Activity-scoped so ConnectionScreen and QrScannerScreen share the same instance
    val connectionViewModel: ConnectionViewModel = viewModel()

        val hasCompletedOnboarding by settingsManager.hasCompletedOnboardingFlow.collectAsState(initial = null)
    val isConnected by RemexClientManager.isConnected.collectAsState()

    // While DataStore hasn't loaded yet, show a plain background to avoid a
    // white flash before the correct start destination is chosen.
    if (hasCompletedOnboarding == null) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(MaterialTheme.colorScheme.background)
        )
        return
    }

    val navController = rememberNavController()
    val navBackStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = navBackStackEntry?.destination?.route

    val showNav = currentRoute != Screen.Splash.route
            && currentRoute != Screen.Tutorial.route
            && currentRoute != Screen.RemoteDesktop.route
            && currentRoute != Screen.QrScanner.route
    var showOverflowMenu by remember { mutableStateOf(false) }

    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()

    val noConnectedMsg = stringResource(R.string.snackbar_no_pc_connected)
    val setupConnectionLabel = stringResource(R.string.snackbar_setup_connection)

    fun navigateToConnection() {
        navController.navigate(Screen.Connection.route) {
            popUpTo(navController.graph.startDestinationId) { saveState = true }
            launchSingleTop = true
            restoreState = true
        }
    }

    fun navigateTo(route: String) {
        if (!isConnected && route in connectionRequiredRoutes) {
            scope.launch {
                val result = snackbarHostState.showSnackbar(
                    message = noConnectedMsg,
                    actionLabel = setupConnectionLabel,
                    withDismissAction = true
                )
                if (result == SnackbarResult.ActionPerformed) {
                    navigateToConnection()
                }
            }
            // Still navigate to the screen so they can preview it
        }
        navController.navigate(route) {
            popUpTo(navController.graph.startDestinationId) { saveState = true }
            launchSingleTop = true
            restoreState = true
        }
    }

    Scaffold(
        snackbarHost = {
            SnackbarHost(hostState = snackbarHostState) { data ->
                Snackbar(
                    snackbarData = data,
                    containerColor = MaterialTheme.colorScheme.inverseSurface,
                    contentColor = MaterialTheme.colorScheme.inverseOnSurface,
                    actionColor = MaterialTheme.colorScheme.inversePrimary
                )
            }
        },
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
                            onClick = { navigateTo(screen.route) }
                        )
                    }
                    // Overflow "More" item for secondary destinations
                    NavigationBarItem(
                        icon = {
                            Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.nav_more_label))
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
                                            navigateTo(screen.route)
                                        }
                                    )
                                }
                            }
                        },
                        label = { Text(stringResource(R.string.nav_more_label), style = MaterialTheme.typography.labelSmall) },
                        selected = secondaryNavItems.any { it.route == currentRoute },
                        alwaysShowLabel = false,
                        onClick = { showOverflowMenu = !showOverflowMenu }
                    )
                }
            }
        },
        floatingActionButton = {
            if (showNav && currentRoute == Screen.Dashboard.route) {
                FloatingActionButton(
                    onClick = { navController.navigate(Screen.Personalization.route) },
                    containerColor = MaterialTheme.colorScheme.tertiaryContainer,
                    contentColor = MaterialTheme.colorScheme.onTertiaryContainer
                ) {
                    Icon(Icons.Default.Settings, contentDescription = stringResource(R.string.cd_settings_fab))
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
                        val nextRoute = if (hasCompletedOnboarding == true)
                            Screen.Dashboard.route
                        else
                            Screen.Tutorial.route
                        navController.navigate(nextRoute) {
                            popUpTo(Screen.Splash.route) { inclusive = true }
                            launchSingleTop = true
                        }
                    }
                )
            }
            composable(Screen.Tutorial.route) {
                TutorialScreen(
                    onFinished = {
                        navController.navigate(Screen.Dashboard.route) {
                            popUpTo(Screen.Tutorial.route) { inclusive = true }
                            launchSingleTop = true
                        }
                    }
                )
            }
            composable(Screen.Dashboard.route) {
                DashboardScreen(onNavigateToConnection = { navigateToConnection() })
            }
            composable(Screen.Connection.route) {
                ConnectionScreen(
                    viewModel = connectionViewModel,
                    onNavigateToQrScanner = {
                        navController.navigate(Screen.QrScanner.route)
                    }
                )
            }
            composable(Screen.QrScanner.route) {
                QrScannerScreen(
                    onScanned = { host, port, key ->
                        connectionViewModel.applyQrResultAndConnect(host, port, key)
                        // Navigate to Dashboard after successful QR scan and auto-connect
                        navController.navigate(Screen.Dashboard.route) {
                            popUpTo(Screen.Dashboard.route) { inclusive = true }
                            launchSingleTop = true
                        }
                    },
                    onBack = { navController.popBackStack() }
                )
            }
            composable(Screen.RemoteControl.route) {
                RemoteControlScreen(onNavigateToConnection = { navigateToConnection() })
            }
            composable(Screen.RemoteMouse.route) {
                RemoteMouseScreen(onNavigateToConnection = { navigateToConnection() })
            }
            composable(Screen.AppLauncher.route) {
                AppLauncherScreen(onNavigateToConnection = { navigateToConnection() })
            }
            composable(Screen.TaskManager.route) {
                TaskManagerScreen(onNavigateToConnection = { navigateToConnection() })
            }
            composable(Screen.RemoteDesktop.route) {
                RemoteDesktopScreen()
            }
            composable(Screen.Personalization.route) {
                PersonalizationScreen()
            }
            composable(Screen.Settings.route) {
                SettingsScreen(
                    onReplayTutorial = {
                        navController.navigate(Screen.Tutorial.route) {
                            launchSingleTop = true
                        }
                    }
                )
            }
            composable(Screen.Faq.route) {
                FaqScreen()
            }
        }
    }
}
