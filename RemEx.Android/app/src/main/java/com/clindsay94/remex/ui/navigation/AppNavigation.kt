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
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.UnfoldMore
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButtonDefaults
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
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
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
import com.clindsay94.remex.ui.screens.AboutScreen
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
import com.clindsay94.remex.ui.theme.RemExTheme
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
fun AppNavigation(splashShown: Boolean, onMarkSplashShown: () -> Unit) {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    // Activity-scoped so ConnectionScreen and QrScannerScreen share the same instance
    val connectionViewModel: ConnectionViewModel = viewModel()

    val hasCompletedOnboarding by
            settingsManager.hasCompletedOnboardingFlow.collectAsState(initial = null)
    val isConnected by RemexClientManager.isConnected.collectAsState()

    val mouseViewModel: RemoteControlViewModel = viewModel()
    val mouseShapePreset by mouseViewModel.remoteMouseCardShapePreset.collectAsState()
    val mouseCornerRadius by mouseViewModel.cardCornerRadius.collectAsState()
    val savedFabPositionX by mouseViewModel.fabPositionX.collectAsState()
    val savedFabPositionY by mouseViewModel.fabPositionY.collectAsState()
    val savedMouseFabX by mouseViewModel.mouseFabX.collectAsState()
    val savedMouseFabY by mouseViewModel.mouseFabY.collectAsState()

    AppNavigationContent(
            hasCompletedOnboarding = hasCompletedOnboarding,
            splashShown = splashShown,
            isConnected = isConnected,
            mouseShapePreset = mouseShapePreset,
            mouseCornerRadius = mouseCornerRadius,
            savedFabPositionX = savedFabPositionX,
            savedFabPositionY = savedFabPositionY,
            savedMouseFabX = savedMouseFabX,
            savedMouseFabY = savedMouseFabY,
            onMarkSplashShown = { onMarkSplashShown() },
            onSaveMouseFabPosition = { x, y -> mouseViewModel.saveMouseFabPosition(x, y) },
            onSaveFloatingMouseIslandPosition = { x, y ->
                mouseViewModel.saveFloatingMouseIslandPosition(x, y)
            },
            onQrScanned = { host, port, key ->
                connectionViewModel.applyQrResultAndConnect(host, port, key)
            },
            dashboardScreenContent = { onNavigateToConnection ->
                DashboardScreen(onNavigateToConnection = onNavigateToConnection)
            },
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
            },
            floatingMouseIslandContent = {
                    shapePreset,
                    cornerRadius,
                    onDismiss,
                    onDragStart,
                    onDrag,
                    onDragEnd ->
                FloatingMouseIsland(
                        viewModel = mouseViewModel,
                        shapePreset = shapePreset,
                        cornerRadius = cornerRadius,
                        onDismiss = onDismiss,
                        onDragStart = onDragStart,
                        onDrag = onDrag,
                        onDragEnd = onDragEnd
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
        mouseShapePreset: Float,
        mouseCornerRadius: Int,
        savedFabPositionX: Float,
        savedFabPositionY: Float,
        savedMouseFabX: Float,
        savedMouseFabY: Float,
        onMarkSplashShown: () -> Unit,
        onSaveMouseFabPosition: (Float, Float) -> Unit,
        onSaveFloatingMouseIslandPosition: (Float, Float) -> Unit,
        onQrScanned: (String, Int, String) -> Unit,
        dashboardScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        remoteControlScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        remoteMouseScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        appLauncherScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        taskManagerScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        connectionScreenContent: @Composable (onNavigateToQrScanner: () -> Unit) -> Unit,
        floatingMouseIslandContent:
                @Composable
                (
                        shapePreset: Float,
                        cornerRadius: Int,
                        onDismiss: () -> Unit,
                        onDragStart: (() -> Unit)?,
                        onDrag: ((androidx.compose.ui.geometry.Offset) -> Unit)?,
                        onDragEnd: (() -> Unit)?) -> Unit
) {
    val context = LocalContext.current
    val view = LocalView.current

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

    var mouseFabOffsetX by
            remember(savedMouseFabX) {
                mutableStateOf(if (savedMouseFabX.isNaN()) Float.NaN else savedMouseFabX)
            }
    var mouseFabOffsetY by
            remember(savedMouseFabY) {
                mutableStateOf(if (savedMouseFabY.isNaN()) Float.NaN else savedMouseFabY)
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
                    dashboardScreenContent { navigateToConnection() }
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

            if (showMouseOverlay) {
                // Default position: bottom-center with some padding to clear FAB
                val density = LocalDensity.current
                val defaultOffsetX = remember {
                    val with =
                            with(receiver = context.resources.displayMetrics) {
                                widthPixels / 2f - with(density) { 150.dp.toPx() }
                            }
                    with
                }
                val defaultOffsetY = remember {
                    with(context.resources.displayMetrics) {
                        heightPixels - with(density) { 440.dp.toPx() }
                    }
                }

                // Use saved position if available, otherwise use default
                var fabOffsetX by remember {
                    mutableStateOf(
                            if (savedFabPositionX.isNaN()) defaultOffsetX else savedFabPositionX
                    )
                }
                var fabOffsetY by remember {
                    mutableStateOf(
                            if (savedFabPositionY.isNaN()) defaultOffsetY else savedFabPositionY
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
                        floatingMouseIslandContent(
                                mouseShapePreset,
                                mouseCornerRadius,
                                { showMouseOverlay = false },
                                {
                                    view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                },
                                { dragAmount ->
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
                                { onSaveFloatingMouseIslandPosition(fabOffsetX, fabOffsetY) }
                        )
                    }
                }
            }
            // ═══ FLOATING PILL NAVIGATION BAR (OneUI 8.5 style) + MOUSE FAB ═══
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
                            // Navigation indicator state
                            val selectedIndex = navItems.indexOfFirst { it.route == currentRoute }

                            navItems.forEachIndexed { index, screen ->
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

                            // Overflow button wrapped in Box for proper menu anchor positioning
                            Box {
                                val isOverflowSelected =
                                        secondaryNavItems.any { it.route == currentRoute } ||
                                                currentRoute == Screen.Personalization.route

                                val overflowContainerColor by
                                        animateColorAsState(
                                                targetValue =
                                                        if (isOverflowSelected)
                                                                MaterialTheme.colorScheme
                                                                        .primaryContainer
                                                        else
                                                                MaterialTheme.colorScheme
                                                                        .surfaceContainerHighest,
                                                animationSpec =
                                                        MaterialTheme.motionScheme
                                                                .defaultEffectsSpec(),
                                                label = "overflow_color"
                                        )

                                FilledTonalIconButton(
                                        onClick = {
                                            view.performHapticFeedback(
                                                    HapticFeedbackConstants.KEYBOARD_TAP
                                            )
                                            showOverflowMenu = !showOverflowMenu
                                            showNavBarWithTimer()
                                        },
                                        modifier = Modifier.size(48.dp),
                                        colors =
                                                IconButtonDefaults.filledTonalIconButtonColors(
                                                        containerColor = overflowContainerColor
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
                                                    view.performHapticFeedback(
                                                            HapticFeedbackConstants.KEYBOARD_TAP
                                                    )
                                                    showOverflowMenu = false
                                                    navigateTo(screen.route)
                                                    showNavBarWithTimer()
                                                }
                                        )
                                    }
                                    DropdownMenuItem(
                                            text = {
                                                Text(
                                                        stringResource(
                                                                Screen.Personalization.titleRes
                                                        )
                                                )
                                            },
                                            leadingIcon = {
                                                Icon(
                                                        Screen.Personalization.icon,
                                                        contentDescription = null
                                                )
                                            },
                                            onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                )
                                                showOverflowMenu = false
                                                navigateTo(Screen.Personalization.route)
                                                showNavBarWithTimer()
                                            }
                                    )
                                }
                            }
                        }

                        // ── Mouse FAB (anchored to the right of toolbar, never overlapping) ──
                        if (showMouseFab) {
                            FloatingActionButton(
                                    onClick = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.KEYBOARD_TAP
                                        )
                                        showMouseOverlay = !showMouseOverlay
                                    },
                                    containerColor = MaterialTheme.colorScheme.tertiaryContainer,
                                    contentColor = MaterialTheme.colorScheme.onTertiaryContainer
                            ) {
                                Icon(
                                        Icons.Default.UnfoldMore,
                                        contentDescription =
                                                stringResource(R.string.screen_remote_mouse_title)
                                )
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
                mouseShapePreset = 0f,
                mouseCornerRadius = 12,
                savedFabPositionX = Float.NaN,
                savedFabPositionY = Float.NaN,
                savedMouseFabX = Float.NaN,
                savedMouseFabY = Float.NaN,
                onMarkSplashShown = {},
                onSaveMouseFabPosition = { _, _ -> },
                onSaveFloatingMouseIslandPosition = { _, _ -> },
                onQrScanned = { _, _, _ -> },
                dashboardScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteControlScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteMouseScreenContent = { Box(Modifier.fillMaxSize()) },
                appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                taskManagerScreenContent = { Box(Modifier.fillMaxSize()) },
                connectionScreenContent = { Box(Modifier.fillMaxSize()) },
                floatingMouseIslandContent = { _, _, _, _, _, _ -> Box(Modifier.size(300.dp)) }
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
                mouseShapePreset = 0f,
                mouseCornerRadius = 12,
                savedFabPositionX = Float.NaN,
                savedFabPositionY = Float.NaN,
                savedMouseFabX = Float.NaN,
                savedMouseFabY = Float.NaN,
                onMarkSplashShown = {},
                onSaveMouseFabPosition = { _, _ -> },
                onSaveFloatingMouseIslandPosition = { _, _ -> },
                onQrScanned = { _, _, _ -> },
                dashboardScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteControlScreenContent = { Box(Modifier.fillMaxSize()) },
                remoteMouseScreenContent = { Box(Modifier.fillMaxSize()) },
                appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                taskManagerScreenContent = { Box(Modifier.fillMaxSize()) },
                connectionScreenContent = { Box(Modifier.fillMaxSize()) },
                floatingMouseIslandContent = { _, _, _, _, _, _ -> Box(Modifier.size(300.dp)) }
        )
    }
}
