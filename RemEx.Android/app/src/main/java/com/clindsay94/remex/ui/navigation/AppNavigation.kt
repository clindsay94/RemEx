package com.clindsay94.remex.ui.navigation

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.screens.AboutScreen
import com.clindsay94.remex.ui.screens.AppLauncherScreen
import com.clindsay94.remex.ui.screens.ConnectionScreen
import com.clindsay94.remex.ui.screens.ConnectionStatusChip
import com.clindsay94.remex.ui.screens.ConnectionViewModel
import com.clindsay94.remex.ui.screens.DashboardScreen
import com.clindsay94.remex.ui.screens.PersonalizationViewModel
import com.clindsay94.remex.ui.screens.FaqScreen
import com.clindsay94.remex.ui.screens.PersonalizationScreen
import com.clindsay94.remex.ui.screens.QrScannerScreen
import com.clindsay94.remex.ui.screens.RemoteControlScreen
import com.clindsay94.remex.ui.screens.RemoteDesktopScreen
import com.clindsay94.remex.ui.screens.RemoteMouseScreen
import com.clindsay94.remex.ui.screens.SettingsScreen
import com.clindsay94.remex.ui.screens.SplashScreen
import com.clindsay94.remex.ui.screens.TaskManagerScreen
import com.clindsay94.remex.ui.screens.TutorialScreen
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.launch
import org.json.JSONArray

// Routes that require an active PC connection to be useful
private val connectionRequiredRoutes =
        setOf(
                Screen.AppLauncher.route,
                Screen.RemoteDesktop.route,
                Screen.TaskManager.route,
                Screen.RemoteControl.route,
                Screen.RemoteMouse.route,
        )

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation(splashShown: Boolean, onMarkSplashShown: () -> Unit) {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    // Activity-scoped so ConnectionScreen and QrScannerScreen share the same instance
    val connectionViewModel: ConnectionViewModel = viewModel()

    val hasCompletedOnboarding by
            settingsManager.hasCompletedOnboardingFlow.collectAsState(initial = null)
    val isConnected by RemexClientManager.isConnected.collectAsState()

    val personalizationViewModel: PersonalizationViewModel = viewModel()
    val personalization by personalizationViewModel.personalization.collectAsState()

    AppNavigationContent(
            hasCompletedOnboarding = hasCompletedOnboarding,
            splashShown = splashShown,
            isConnected = isConnected,
            navPrimaryItemsJson = personalization?.navPrimaryItemsJson ?: "[\"dashboard\", \"remote_desktop\", \"task_manager\", \"remote_control\", \"app_launcher\"]",
            onMarkSplashShown = { onMarkSplashShown() },
            onQrScanned = { host, port, key ->
                connectionViewModel.applyQrResultAndConnect(host, port, key)
            },
            dashboardScreenContent = { DashboardScreen() },
            remoteControlScreenContent = { onNavigateToConnection ->
                RemoteControlScreen(onNavigateToConnection = onNavigateToConnection)
            },
            remoteMouseScreenContent = { onNavigateToConnection ->
                RemoteMouseScreen(onNavigateToConnection = onNavigateToConnection)
            },
            appLauncherScreenContent = { onNavigateToConnection ->
                AppLauncherScreen(onNavigateToConnection = onNavigateToConnection)
            },
            taskManagerScreenContent = { onNavigateToConnection ->
                TaskManagerScreen(onNavigateToConnection = onNavigateToConnection)
            },
            connectionScreenContent = { onNavigateToQrScanner ->
                ConnectionScreen(
                        viewModel = connectionViewModel,
                        onNavigateToQrScanner = onNavigateToQrScanner
                )
            }
    )
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun AppNavigationContent(
        hasCompletedOnboarding: Boolean?,
        splashShown: Boolean,
        isConnected: Boolean,
        navPrimaryItemsJson: String,
        onMarkSplashShown: () -> Unit,
        onQrScanned: (String, Int, String) -> Unit,
        dashboardScreenContent: @Composable () -> Unit,
        remoteControlScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        remoteMouseScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        appLauncherScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        taskManagerScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        connectionScreenContent: @Composable (onNavigateToQrScanner: () -> Unit) -> Unit
) {
    val context = LocalContext.current
    val view = LocalView.current

    val allScreens = listOf(
        Screen.Dashboard, Screen.RemoteDesktop, Screen.TaskManager,
        Screen.RemoteControl, Screen.RemoteMouse, Screen.AppLauncher,
        Screen.Settings, Screen.Faq, Screen.About
    )

    val currentNavItems = remember(navPrimaryItemsJson) {
        try {
            val jsonArray = JSONArray(navPrimaryItemsJson)
            val routes = (0 until jsonArray.length()).map { jsonArray.getString(it) }
            routes.mapNotNull { route -> allScreens.find { it.route == route } }
        } catch (e: Exception) {
            listOf(Screen.Dashboard, Screen.RemoteDesktop, Screen.TaskManager, Screen.RemoteControl)
        }
    }

    val currentSecondaryNavItems = remember(currentNavItems) {
        allScreens.filter { screen -> !currentNavItems.contains(screen) }
    }

    // While DataStore hasn't loaded yet, show a plain background to avoid a
    // white flash before the correct start destination is chosen.
    if (hasCompletedOnboarding == null) {
        Box(modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background))
        return
    }

    val startDestination =
            if (splashShown) {
                if (hasCompletedOnboarding == true) Screen.Dashboard.route
                else Screen.Tutorial.route
            } else {
                Screen.Splash.route
            }

    val navController = rememberNavController()
    val navBackStackEntry = navController.currentBackStackEntryAsState().value
    val currentRoute = navBackStackEntry?.destination?.route

    val scope = rememberCoroutineScope()

    val showNav =
            currentRoute != Screen.Splash.route &&
                    currentRoute != Screen.Tutorial.route &&
                    currentRoute != Screen.RemoteDesktop.route &&
                    currentRoute != Screen.QrScanner.route
    var fabMenuExpanded by remember { mutableStateOf(false) }
    var navBarVisible by remember { mutableStateOf(true) }
    var navHideJob by remember { mutableStateOf<kotlinx.coroutines.Job?>(null) }

    // Auto-hide navigation bar after 3 seconds of inactivity
    fun showNavBarWithTimer() {
        navBarVisible = true
        navHideJob?.cancel()
        navHideJob =
                scope.launch {
                    kotlinx.coroutines.delay(3000L)
                    navBarVisible = false
                }
    }

    // Show nav bar on route change
    LaunchedEffect(currentRoute) {
        if (showNav) {
            showNavBarWithTimer()
        }
    }

    // Hide FAB on secondary nav screens (user is already viewing them)
    val showFab =
            showNav &&
                    currentRoute != Screen.RemoteMouse.route &&
                    currentRoute != Screen.Settings.route &&
                    currentRoute != Screen.Personalization.route &&
                    currentRoute != Screen.Faq.route &&
                    currentRoute != Screen.About.route &&
                    currentRoute != Screen.Connection.route

    LaunchedEffect(currentRoute) {
        if (!showFab) fabMenuExpanded = false
    }

    fun navigateToConnection() {
        navController.navigate(Screen.Connection.route) {
            popUpTo(navController.graph.startDestinationId) { saveState = true }
            launchSingleTop = true
            restoreState = true
        }
    }

    fun navigateTo(route: String) {
        navController.navigate(route) {
            popUpTo(navController.graph.startDestinationId) { saveState = true }
            launchSingleTop = true
            restoreState = true
        }
    }

    Scaffold(
            bottomBar = {
                // Empty - navigation is now a floating overlay
            }
    ) { innerPadding ->
        Box(
                modifier =
                        Modifier.fillMaxSize().padding(innerPadding).pointerInput(showNav) {
                            if (showNav) {
                                awaitEachGesture {
                                    awaitPointerEvent()
                                    // Show nav on any touch interaction
                                    showNavBarWithTimer()
                                }
                            }
                        }
        ) {
            NavHost(
                    navController = navController,
                    startDestination = startDestination,
                    modifier = Modifier.fillMaxSize()
            ) {
                composable(Screen.Splash.route) {
                    SplashScreen(
                            onFinished = {
                                onMarkSplashShown()
                                val nextRoute =
                                        if (hasCompletedOnboarding == true) Screen.Dashboard.route
                                        else Screen.Tutorial.route
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
                    dashboardScreenContent()
                }
                composable(Screen.Connection.route) {
                    connectionScreenContent { navController.navigate(Screen.QrScanner.route) }
                }
                composable(Screen.QrScanner.route) {
                    QrScannerScreen(
                            onScanned = { host, port, key ->
                                onQrScanned(host, port, key)
                                navController.navigate(Screen.Dashboard.route) {
                                    popUpTo(Screen.Dashboard.route) { inclusive = true }
                                    launchSingleTop = true
                                }
                            },
                            onBack = { navController.popBackStack() }
                    )
                }
                composable(Screen.RemoteControl.route) {
                    remoteControlScreenContent { navigateToConnection() }
                }
                composable(Screen.RemoteMouse.route) {
                    remoteMouseScreenContent { navigateToConnection() }
                }
                composable(Screen.AppLauncher.route) {
                    appLauncherScreenContent { navigateToConnection() }
                }
                composable(Screen.TaskManager.route) {
                    taskManagerScreenContent { navigateToConnection() }
                }
                composable(Screen.RemoteDesktop.route) { RemoteDesktopScreen() }
                composable(Screen.Personalization.route) { PersonalizationScreen() }
                composable(Screen.Settings.route) {
                    SettingsScreen(
                        onReplayTutorial = {
                            navController.navigate(Screen.Tutorial.route) {
                                launchSingleTop = true
                            }
                        },
                        onNavigateToAbout = {
                            navController.navigate(Screen.About.route) {
                                launchSingleTop = true
                            }
                        }
                    )
                }
                composable(Screen.Faq.route) { FaqScreen() }
                composable(Screen.About.route) { AboutScreen() }
            }

            // Persistent connection chip — floats in top-end corner on every app screen
            val showChip = currentRoute != null &&
                    currentRoute != Screen.Splash.route &&
                    currentRoute != Screen.Tutorial.route
            if (showChip) {
                ConnectionStatusChip(
                        isConnected = isConnected,
                        modifier = Modifier
                                .align(Alignment.TopEnd)
                                .statusBarsPadding()
                                .padding(end = 12.dp, top = 4.dp)
                )
            }

            // Scrim: dismisses FAB menu on outside tap; sits below nav bar
            if (fabMenuExpanded) {
                Box(
                        modifier = Modifier
                                .fillMaxSize()
                                .clickable(
                                        indication = null,
                                        interactionSource = remember { MutableInteractionSource() }
                                ) { fabMenuExpanded = false }
                )
            }

            // ═══ FLOATING PILL NAVIGATION BAR (OneUI 8.5 style) + FAB MENU ═══
            if (showNav) {
                AnimatedVisibility(
                        visible = navBarVisible,
                        enter =
                                fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()) +
                                        slideInVertically(
                                                MaterialTheme.motionScheme.defaultSpatialSpec()
                                        ) { it / 2 },
                        exit =
                                fadeOut(MaterialTheme.motionScheme.defaultEffectsSpec()) +
                                        slideOutVertically(
                                                MaterialTheme.motionScheme.defaultSpatialSpec()
                                        ) { it / 2 },
                        modifier = Modifier.align(Alignment.BottomCenter)
                ) {
                    Row(
                            modifier = Modifier.navigationBarsPadding().padding(bottom = 24.dp),
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                            verticalAlignment = Alignment.CenterVertically
                    ) {
                        // ── Navigation pill ──
                        Row(
                                modifier =
                                        Modifier.background(
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .surfaceContainerHigh.copy(
                                                                        alpha = 0.95f
                                                                ),
                                                        shape = MaterialTheme.shapes.extraLarge
                                                )
                                                .padding(horizontal = 12.dp, vertical = 8.dp),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                        ) {
                            currentNavItems.forEachIndexed { index, screen ->
                                val isSelected = currentRoute == screen.route
                                val screenTitle = stringResource(screen.titleRes)

                                // Animate indicator sliding to selected button
                                val indicatorWeight by
                                        animateFloatAsState(
                                                targetValue = if (isSelected) 1f else 0f,
                                                animationSpec =
                                                        MaterialTheme.motionScheme
                                                                .fastSpatialSpec(),
                                                label = "nav_indicator_$index"
                                        )

                                val containerColor by
                                        animateColorAsState(
                                                targetValue =
                                                        if (isSelected)
                                                                MaterialTheme.colorScheme
                                                                        .primaryContainer
                                                        else
                                                                MaterialTheme.colorScheme
                                                                        .surfaceContainerHighest,
                                                animationSpec =
                                                        MaterialTheme.motionScheme
                                                                .defaultEffectsSpec(),
                                                label = "nav_color_$index"
                                        )

                                val iconTint by
                                        animateColorAsState(
                                                targetValue =
                                                        if (isSelected)
                                                                MaterialTheme.colorScheme
                                                                        .onPrimaryContainer
                                                        else
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant,
                                                animationSpec =
                                                        MaterialTheme.motionScheme
                                                                .defaultEffectsSpec(),
                                                label = "nav_icon_tint_$index"
                                        )

                                val iconScale by
                                        animateFloatAsState(
                                                targetValue = if (isSelected) 1.12f else 1f,
                                                animationSpec =
                                                        MaterialTheme.motionScheme
                                                                .fastSpatialSpec(),
                                                label = "nav_icon_scale_$index"
                                        )

                                FilledTonalIconButton(
                                        onClick = {
                                            view.performHapticFeedback(
                                                    HapticFeedbackConstants.KEYBOARD_TAP
                                            )
                                            navigateTo(screen.route)
                                            showNavBarWithTimer()
                                        },
                                        modifier = Modifier.size(48.dp),
                                        colors =
                                                IconButtonDefaults.filledTonalIconButtonColors(
                                                        containerColor = containerColor
                                                )
                                ) {
                                    Icon(
                                            imageVector = screen.icon,
                                            contentDescription = screenTitle,
                                            tint = iconTint,
                                            modifier =
                                                    Modifier.size(24.dp).graphicsLayer {
                                                        scaleX = iconScale
                                                        scaleY = iconScale
                                                    }
                                    )
                                }
                            }

                        }

                        // ── FAB Menu (overflow navigation) ──
                        if (showFab) {
                            Box(contentAlignment = Alignment.BottomEnd) {
                                // Expanding secondary nav items above the FAB
                                AnimatedVisibility(
                                        visible = fabMenuExpanded,
                                        enter = fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()) +
                                                slideInVertically(MaterialTheme.motionScheme.defaultSpatialSpec()) { it },
                                        exit = fadeOut(MaterialTheme.motionScheme.defaultEffectsSpec()) +
                                                slideOutVertically(MaterialTheme.motionScheme.defaultSpatialSpec()) { it }
                                ) {
                                    Column(
                                            horizontalAlignment = Alignment.End,
                                            verticalArrangement = Arrangement.spacedBy(8.dp),
                                            modifier = Modifier.padding(bottom = 68.dp)
                                    ) {
                                        val fabMenuItems = currentSecondaryNavItems +
                                                listOf(Screen.Personalization)
                                        fabMenuItems.forEach { screen ->
                                            Row(
                                                    verticalAlignment = Alignment.CenterVertically,
                                                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                                            ) {
                                                Surface(
                                                        color = MaterialTheme.colorScheme.surfaceContainerHigh.copy(alpha = 0.95f),
                                                        shape = MaterialTheme.shapes.small
                                                ) {
                                                    Text(
                                                            text = stringResource(screen.titleRes),
                                                            style = MaterialTheme.typography.labelLarge,
                                                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp)
                                                    )
                                                }
                                                SmallFloatingActionButton(
                                                        onClick = {
                                                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                                            fabMenuExpanded = false
                                                            navigateTo(screen.route)
                                                            showNavBarWithTimer()
                                                        },
                                                        containerColor = MaterialTheme.colorScheme.secondaryContainer,
                                                        contentColor = MaterialTheme.colorScheme.onSecondaryContainer
                                                ) {
                                                    Icon(screen.icon, contentDescription = stringResource(screen.titleRes))
                                                }
                                            }
                                        }
                                    }
                                }

                                // Trigger FAB
                                FloatingActionButton(
                                        onClick = {
                                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                            fabMenuExpanded = !fabMenuExpanded
                                            showNavBarWithTimer()
                                        },
                                        containerColor = if (fabMenuExpanded)
                                            MaterialTheme.colorScheme.primaryContainer
                                        else
                                            MaterialTheme.colorScheme.tertiaryContainer,
                                        contentColor = if (fabMenuExpanded)
                                            MaterialTheme.colorScheme.onPrimaryContainer
                                        else
                                            MaterialTheme.colorScheme.onTertiaryContainer
                                ) {
                                    Icon(
                                            if (fabMenuExpanded) Icons.Default.Close else Icons.Default.MoreVert,
                                            contentDescription = stringResource(R.string.nav_more_label)
                                    )
                                }
                            }
                        }
                    }
                }
            }
        } // end outer Box
    }
}

@Preview(showBackground = true)
@Composable
private fun AppNavigationPreview() {
    RemExTheme {
        AppNavigationContent(
                hasCompletedOnboarding = true,
                splashShown = true,
                isConnected = true,
                navPrimaryItemsJson = "[\"dashboard\", \"remote_desktop\", \"task_manager\", \"remote_control\", \"app_launcher\"]",
                onMarkSplashShown = {},
                onQrScanned = { _, _, _ -> },
                dashboardScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteControlScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteMouseScreenContent = { Box(Modifier.fillMaxSize()) },
                appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                taskManagerScreenContent = { Box(Modifier.fillMaxSize()) },
                connectionScreenContent = { Box(Modifier.fillMaxSize()) }
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun AppNavigationDisconnectedPreview() {
    RemExTheme {
        AppNavigationContent(
                hasCompletedOnboarding = true,
                splashShown = true,
                isConnected = false,
                navPrimaryItemsJson = "[\"dashboard\", \"remote_desktop\", \"task_manager\", \"remote_control\", \"app_launcher\"]",
                onMarkSplashShown = {},
                onQrScanned = { _, _, _ -> },
                dashboardScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteControlScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteMouseScreenContent = { Box(Modifier.fillMaxSize()) },
                appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                taskManagerScreenContent = { Box(Modifier.fillMaxSize()) },
                connectionScreenContent = { Box(Modifier.fillMaxSize()) }
        )
    }
}
