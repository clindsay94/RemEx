package com.clindsay94.remex.ui.navigation

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
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
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.DpOffset
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.screens.AppLauncherScreen
import com.clindsay94.remex.ui.screens.ConnectionScreen
import com.clindsay94.remex.ui.screens.ConnectionViewModel
import com.clindsay94.remex.ui.screens.DashboardScreen
import com.clindsay94.remex.ui.screens.FaqScreen
import com.clindsay94.remex.ui.screens.FloatingMouseIsland
import com.clindsay94.remex.ui.screens.PersonalizationScreen
import com.clindsay94.remex.ui.screens.QrScannerScreen
import com.clindsay94.remex.ui.screens.RemoteControlScreen
import com.clindsay94.remex.ui.screens.RemoteControlViewModel
import com.clindsay94.remex.ui.screens.RemoteDesktopScreen
import com.clindsay94.remex.ui.screens.RemoteMouseScreen
import com.clindsay94.remex.ui.screens.SettingsScreen
import com.clindsay94.remex.ui.screens.SplashScreen
import com.clindsay94.remex.ui.screens.TaskManagerScreen
import com.clindsay94.remex.ui.screens.TutorialScreen
import kotlinx.coroutines.launch

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
fun AppNavigation() {
    val context = LocalContext.current
    val haptic = LocalHapticFeedback.current
    val settingsManager = remember { SettingsManager(context) }
    // Activity-scoped so ConnectionScreen and QrScannerScreen share the same instance
    val connectionViewModel: ConnectionViewModel = viewModel()

    val hasCompletedOnboarding by
            settingsManager.hasCompletedOnboardingFlow.collectAsState(initial = null)
    val isConnected by RemexClientManager.isConnected.collectAsState()

    // While DataStore hasn't loaded yet, show a plain background to avoid a
    // white flash before the correct start destination is chosen.
    if (hasCompletedOnboarding == null) {
        Box(modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background))
        return
    }

    val navController = rememberNavController()
    val navBackStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = navBackStackEntry?.destination?.route

    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()

    val showNav =
            currentRoute != Screen.Splash.route &&
                    currentRoute != Screen.Tutorial.route &&
                    currentRoute != Screen.RemoteDesktop.route &&
                    currentRoute != Screen.QrScanner.route
    var showOverflowMenu by remember { mutableStateOf(false) }
    var showMouseOverlay by remember { mutableStateOf(false) }
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

    val mouseViewModel: RemoteControlViewModel = viewModel()
    val mouseShapePreset by mouseViewModel.remoteMouseCardShapePreset.collectAsState()
    val mouseCornerRadius by mouseViewModel.cardCornerRadius.collectAsState()
    val savedFabPositionX by mouseViewModel.fabPositionX.collectAsState()
    val savedFabPositionY by mouseViewModel.fabPositionY.collectAsState()
    val savedMouseFabX by mouseViewModel.mouseFabX.collectAsState()
    val savedMouseFabY by mouseViewModel.mouseFabY.collectAsState()

    var mouseFabOffsetX by
            remember(savedMouseFabX) {
                mutableStateOf(
                        if (savedMouseFabX.isNaN()) Float.NaN else savedMouseFabX
                )
            }
    var mouseFabOffsetY by
            remember(savedMouseFabY) {
                mutableStateOf(
                        if (savedMouseFabY.isNaN()) Float.NaN else savedMouseFabY
                )
            }

    // Hide mouse overlay when navigating away from main nav screens
    val showMouseFab =
            showNav &&
                    currentRoute != Screen.RemoteMouse.route &&
                    currentRoute != Screen.Settings.route &&
                    currentRoute != Screen.Personalization.route &&
                    currentRoute != Screen.Faq.route &&
                    currentRoute != Screen.Connection.route

    LaunchedEffect(currentRoute, showMouseFab) {
        if (!showMouseFab) {
            showMouseOverlay = false
        }
    }

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
                val result =
                        snackbarHostState.showSnackbar(
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
                // Empty - navigation is now a floating overlay
            },
            floatingActionButton = {
                if (showMouseFab) {
                    val density = androidx.compose.ui.platform.LocalDensity.current
                    Box(modifier = Modifier.fillMaxSize()) {
                        FloatingActionButton(
                                onClick = { showMouseOverlay = !showMouseOverlay },
                                containerColor = MaterialTheme.colorScheme.tertiaryContainer,
                                contentColor = MaterialTheme.colorScheme.onTertiaryContainer,
                                modifier =
                                        Modifier.then(
                                                        if (!mouseFabOffsetX.isNaN() &&
                                                                        !mouseFabOffsetY.isNaN()
                                                        ) {
                                                            Modifier.offset {
                                                                IntOffset(
                                                                        mouseFabOffsetX.toInt(),
                                                                        mouseFabOffsetY.toInt()
                                                                )
                                                            }
                                                        } else {
                                                            Modifier.align(Alignment.BottomEnd)
                                                                    .padding(
                                                                            bottom = 80.dp,
                                                                            end = 16.dp
                                                                    )
                                                        }
                                                )
                                                .pointerInput(Unit) {
                                                    detectDragGesturesAfterLongPress(
                                                            onDragStart = {
                                                                if (mouseFabOffsetX.isNaN() ||
                                                                                mouseFabOffsetY
                                                                                        .isNaN()
                                                                ) {
                                                                    // Initialize offset if it's
                                                                    // the first time dragging
                                                                    // This is a bit tricky with
                                                                    // align, so we'll estimate
                                                                    // or just start from 0
                                                                    // Better to initialize with
                                                                    // current position if
                                                                    // possible
                                                                }
                                                                scope.launch {
                                                                    haptic.performHapticFeedback(
                                                                            HapticFeedbackType
                                                                                    .LongPress
                                                                    )
                                                                }
                                                            },
                                                            onDrag = { change, dragAmount ->
                                                                change.consume()
                                                                if (mouseFabOffsetX.isNaN()) {
                                                                    // If we don't have a saved
                                                                    // position, we start from a
                                                                    // reasonable default
                                                                    // approximately where
                                                                    // Alignment.BottomEnd with
                                                                    // padding is.
                                                                    mouseFabOffsetX =
                                                                            with(density) {
                                                                                -16.dp.toPx()
                                                                            }
                                                                    mouseFabOffsetY =
                                                                            with(density) {
                                                                                -80.dp.toPx()
                                                                            }
                                                                }
                                                                mouseFabOffsetX += dragAmount.x
                                                                mouseFabOffsetY += dragAmount.y
                                                            },
                                                            onDragEnd = {
                                                                mouseViewModel.saveMouseFabPosition(
                                                                        mouseFabOffsetX,
                                                                        mouseFabOffsetY
                                                                )
                                                            }
                                                    )
                                                }
                        ) {
                            Icon(
                                    Icons.Default.Mouse,
                                    contentDescription =
                                            stringResource(R.string.screen_remote_mouse_title)
                            )
                        }
                    }
                }
            }
    ) { innerPadding ->
        Box(
                modifier =
                        Modifier.fillMaxSize()
                                .padding(if (showNav) innerPadding else PaddingValues(0.dp))
                                .pointerInput(showNav) {
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
                    startDestination = Screen.Splash.route,
                    modifier = Modifier.fillMaxSize()
            ) {
                composable(Screen.Splash.route) {
                    SplashScreen(
                            onFinished = {
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
                composable(Screen.RemoteDesktop.route) { RemoteDesktopScreen() }
                composable(Screen.Personalization.route) { PersonalizationScreen() }
                composable(Screen.Settings.route) {
                    SettingsScreen(
                            onReplayTutorial = {
                                navController.navigate(Screen.Tutorial.route) {
                                    launchSingleTop = true
                                }
                            }
                    )
                }
                composable(Screen.Faq.route) { FaqScreen() }
            }

            if (showMouseOverlay) {
                // Default position: bottom-right with some padding
                val defaultOffsetX = remember {
                    with(context.resources.displayMetrics) { widthPixels - 200f }
                }
                val defaultOffsetY = remember {
                    with(context.resources.displayMetrics) { heightPixels - 500f }
                }

                // Use saved position if available, otherwise use default
                var fabOffsetX by
                        remember(savedFabPositionX) {
                            mutableStateOf(
                                    if (savedFabPositionX.isNaN()) defaultOffsetX
                                    else savedFabPositionX
                            )
                        }
                var fabOffsetY by
                        remember(savedFabPositionY) {
                            mutableStateOf(
                                    if (savedFabPositionY.isNaN()) defaultOffsetY
                                    else savedFabPositionY
                            )
                        }

                Box(
                        modifier =
                                Modifier.fillMaxSize().clickable(
                                                indication = null,
                                                interactionSource =
                                                        remember { MutableInteractionSource() }
                                        ) { showMouseOverlay = false }
                ) {
                    Box(
                            modifier =
                                    Modifier.offset {
                                        IntOffset(fabOffsetX.toInt(), fabOffsetY.toInt())
                                    }
                    ) {
                        FloatingMouseIsland(
                                viewModel = mouseViewModel,
                                shapePreset = mouseShapePreset,
                                cornerRadius = mouseCornerRadius,
                                onDismiss = { showMouseOverlay = false },
                                onDragStart = {
                                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                },
                                onDrag = { dragAmount ->
                                    fabOffsetX =
                                            (fabOffsetX + dragAmount.x).coerceIn(
                                                    0f,
                                                    context.resources.displayMetrics.widthPixels -
                                                            320f
                                            )
                                    fabOffsetY =
                                            (fabOffsetY + dragAmount.y).coerceIn(
                                                    0f,
                                                    context.resources.displayMetrics.heightPixels -
                                                            400f
                                            )
                                },
                                onDragEnd = {
                                    mouseViewModel.saveFloatingMouseIslandPosition(
                                            fabOffsetX,
                                            fabOffsetY
                                    )
                                }
                        )
                    }
                }
            }
            // ═══ FLOATING PILL NAVIGATION BAR (OneUI 8.5 style) ═══
            if (showNav) {
                AnimatedVisibility(
                        visible = navBarVisible,
                        enter = fadeIn(tween(200)) + slideInVertically(tween(200)) { it / 2 },
                        exit = fadeOut(tween(200)) + slideOutVertically(tween(200)) { it / 2 },
                        modifier = Modifier.align(Alignment.BottomCenter)
                ) {
                    androidx.compose.foundation.layout.Row(
                            modifier =
                                    Modifier.padding(bottom = 24.dp)
                                            .background(
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
                        navItems.forEach { screen ->
                            val isSelected = currentRoute == screen.route
                            val screenTitle = stringResource(screen.titleRes)
                            val containerColor by
                                    animateDpAsState(
                                            targetValue = if (isSelected) 12.dp else 0.dp,
                                            label = "pill_elevation"
                                    )

                            androidx.compose.material3.FilledTonalIconButton(
                                    onClick = {
                                        navigateTo(screen.route)
                                        showNavBarWithTimer()
                                    },
                                    modifier = Modifier.size(48.dp),
                                    colors =
                                            androidx.compose.material3.IconButtonDefaults
                                                    .filledTonalIconButtonColors(
                                                            containerColor =
                                                                    if (isSelected)
                                                                            MaterialTheme
                                                                                    .colorScheme
                                                                                    .primaryContainer
                                                                    else
                                                                            MaterialTheme
                                                                                    .colorScheme
                                                                                    .surfaceContainerHighest
                                                    )
                            ) {
                                Icon(
                                        imageVector = screen.icon,
                                        contentDescription = screenTitle,
                                        tint =
                                                if (isSelected)
                                                        MaterialTheme.colorScheme.onPrimaryContainer
                                                else MaterialTheme.colorScheme.onSurfaceVariant,
                                        modifier = Modifier.size(24.dp)
                                )
                            }
                        }

                        // Overflow button wrapped in Box for proper menu anchor positioning
                        Box {
                            androidx.compose.material3.FilledTonalIconButton(
                                    onClick = {
                                        showOverflowMenu = !showOverflowMenu
                                        showNavBarWithTimer()
                                    },
                                    modifier = Modifier.size(48.dp),
                                    colors =
                                            androidx.compose.material3.IconButtonDefaults
                                                    .filledTonalIconButtonColors(
                                                            containerColor =
                                                                    if (secondaryNavItems.any {
                                                                                it.route ==
                                                                                        currentRoute
                                                                            } ||
                                                                                    currentRoute ==
                                                                                            Screen.Personalization
                                                                                                    .route
                                                                    )
                                                                            MaterialTheme
                                                                                    .colorScheme
                                                                                    .primaryContainer
                                                                    else
                                                                            MaterialTheme
                                                                                    .colorScheme
                                                                                    .surfaceContainerHighest
                                                    )
                            ) {
                                Icon(
                                        Icons.Default.MoreVert,
                                        contentDescription =
                                                stringResource(R.string.nav_more_label),
                                        modifier = Modifier.size(24.dp)
                                )
                            }

                            DropdownMenu(
                                    expanded = showOverflowMenu,
                                    onDismissRequest = { showOverflowMenu = false },
                                    offset = DpOffset(0.dp, 8.dp)
                            ) {
                                secondaryNavItems.forEach { screen ->
                                    DropdownMenuItem(
                                            text = { Text(stringResource(screen.titleRes)) },
                                            leadingIcon = {
                                                Icon(screen.icon, contentDescription = null)
                                            },
                                            onClick = {
                                                showOverflowMenu = false
                                                navigateTo(screen.route)
                                                showNavBarWithTimer()
                                            }
                                    )
                                }
                                DropdownMenuItem(
                                        text = {
                                            Text(stringResource(Screen.Personalization.titleRes))
                                        },
                                        leadingIcon = {
                                            Icon(
                                                    Screen.Personalization.icon,
                                                    contentDescription = null
                                            )
                                        },
                                        onClick = {
                                            showOverflowMenu = false
                                            navigateTo(Screen.Personalization.route)
                                            showNavBarWithTimer()
                                        }
                                )
                            }
                        }
                    }
                }
            }
        } // end outer Box
    }
}
