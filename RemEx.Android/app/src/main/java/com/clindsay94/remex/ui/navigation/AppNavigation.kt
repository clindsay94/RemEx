package com.clindsay94.remex.ui.navigation

import android.view.HapticFeedbackConstants
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.NavigationDrawerItemDefaults
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.adaptive.currentWindowAdaptiveInfo
import androidx.compose.material3.adaptive.navigationsuite.ExperimentalMaterial3AdaptiveNavigationSuiteApi
import androidx.compose.material3.adaptive.navigationsuite.NavigationSuiteScaffoldDefaults
import androidx.compose.material3.adaptive.navigationsuite.NavigationSuiteType
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
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

// M3 Expressive motion easing curves
private val EmphasizedDecelerate = CubicBezierEasing(0.05f, 0.7f, 0.1f, 1f)
private val EmphasizedAccelerate = CubicBezierEasing(0.3f, 0f, 0.8f, 0.15f)

// Routes that suppress the navigation chrome (full-screen / flow screens)
private val noNavRoutes =
        setOf(
                Screen.Splash.route,
                Screen.Tutorial.route,
                Screen.RemoteDesktop.route,
                Screen.QrScanner.route,
        )

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation(splashShown: Boolean, onMarkSplashShown: () -> Unit) {
        val context = LocalContext.current
        val settingsManager = remember { SettingsManager(context) }
        val connectionViewModel: ConnectionViewModel = viewModel()

        val hasCompletedOnboarding by
                settingsManager.hasCompletedOnboardingFlow.collectAsState(initial = null)
        val isConnected by RemexClientManager.isConnected.collectAsState()

        AppNavigationContent(
                hasCompletedOnboarding = hasCompletedOnboarding,
                splashShown = splashShown,
                isConnected = isConnected,
                onMarkSplashShown = onMarkSplashShown,
                onQrScanned = { host, port, key ->
                        connectionViewModel.applyQrResultAndConnect(host, port, key)
                },
                dashboardScreenContent = { onNav ->
                        DashboardScreen(onNavigateToConnection = onNav)
                },
                remoteControlScreenContent = { onNav ->
                        RemoteControlScreen(onNavigateToConnection = onNav)
                },
                remoteMouseScreenContent = { onNav ->
                        RemoteMouseScreen(onNavigateToConnection = onNav)
                },
                appLauncherScreenContent = { onNav ->
                        AppLauncherScreen(onNavigateToConnection = onNav)
                },
                taskManagerScreenContent = { onNav ->
                        TaskManagerScreen(onNavigateToConnection = onNav)
                },
                connectionScreenContent = { onQr ->
                        ConnectionScreen(
                                viewModel = connectionViewModel,
                                onNavigateToQrScanner = onQr
                        )
                },
        )
}

@OptIn(
        ExperimentalMaterial3Api::class,
        ExperimentalMaterial3ExpressiveApi::class,
        ExperimentalMaterial3AdaptiveNavigationSuiteApi::class,
)
@Composable
private fun AppNavigationContent(
        hasCompletedOnboarding: Boolean?,
        splashShown: Boolean,
        isConnected: Boolean,
        onMarkSplashShown: () -> Unit,
        onQrScanned: (String, Int, String) -> Unit,
        dashboardScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        remoteControlScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        remoteMouseScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        appLauncherScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        taskManagerScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        connectionScreenContent: @Composable (onNavigateToQrScanner: () -> Unit) -> Unit,
) {
        // Hold a blank surface while DataStore loads to avoid a white flash
        if (hasCompletedOnboarding == null) {
                Box(
                        modifier =
                                Modifier.fillMaxSize()
                                        .background(MaterialTheme.colorScheme.background)
                )
                return
        }

        // Always start at splash screen - it will immediately navigate if already shown
        val startDestination = Screen.Splash.route

        val navController = rememberNavController()
        val currentBackStackEntry by navController.currentBackStackEntryAsState()
        val currentRoute = currentBackStackEntry?.destination?.route

        // Pager state for horizontal swipe navigation between primary screens
        val pagerState = rememberPagerState(pageCount = { navItems.size })
        val currentPageIndex =
                navItems.indexOfFirst { it.route == currentRoute }.takeIf { it >= 0 } ?: 0

        val view = LocalView.current
        val scope = rememberCoroutineScope()

        // ─── Adaptive layout ─────────────────────────────────────────────────────
        val adaptiveInfo = currentWindowAdaptiveInfo()
        val showNav = currentRoute != null && !noNavRoutes.contains(currentRoute)

        // M3: NavigationBar on compact, NavigationRail on medium, NavigationDrawer on expanded
        val layoutType =
                if (showNav) {
                        NavigationSuiteScaffoldDefaults.calculateFromAdaptiveInfo(adaptiveInfo)
                } else {
                        NavigationSuiteType.None
                }

        val isNavBarLayout = layoutType == NavigationSuiteType.NavigationBar

        // ─── State ───────────────────────────────────────────────────────────────
        var showExitDialog by rememberSaveable { mutableStateOf(false) }
        var showMoreSheet by remember { mutableStateOf(false) }
        val moreSheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

        // Close more sheet on route change
        LaunchedEffect(currentRoute) { showMoreSheet = false }

        // Back handler: show exit confirmation when at a root destination
        val isAtRoot = navItems.any { it.route == currentRoute }
        BackHandler(enabled = isAtRoot) { showExitDialog = true }

        // ─── Navigation helpers ───────────────────────────────────────────────────
        fun navigateTo(route: String) {
                navController.navigate(route) {
                        popUpTo(navController.graph.startDestinationId) { saveState = true }
                        launchSingleTop = true
                        restoreState = true
                }
        }

        // Only sync pager when we're on a primary nav screen that uses the pager
        val isOnPrimaryNavScreen = navItems.any { it.route == currentRoute }

        // Sync pager state with navigation
        LaunchedEffect(currentRoute) {
                if (isOnPrimaryNavScreen) {
                        val index = navItems.indexOfFirst { it.route == currentRoute }
                        if (index >= 0 && pagerState.currentPage != index) {
                                pagerState.animateScrollToPage(index)
                        }
                }
        }

        // Navigate when user swipes the pager
        LaunchedEffect(pagerState.currentPage, pagerState.isScrollInProgress) {
                if (isOnPrimaryNavScreen && !pagerState.isScrollInProgress) {
                        val targetRoute = navItems.getOrNull(pagerState.currentPage)?.route
                        if (targetRoute != null && targetRoute != currentRoute) {
                                navigateTo(targetRoute)
                        }
                }
        }

        fun navigateToConnection() {
                navController.navigate(Screen.Connection.route) {
                        popUpTo(navController.graph.startDestinationId) { saveState = true }
                        launchSingleTop = true
                        restoreState = true
                }
        }

        fun onNavItemClick(route: String) {
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                navigateTo(route)
        }

        // ─── Adaptive layout shell ────────────────────────────────────────────────
        if (isNavBarLayout || !showNav) {
                // ── Compact (phone) or full-screen routes: Scaffold-style with bottom
                // NavigationBar ──
                Column(modifier = Modifier.fillMaxSize()) {
                        Box(modifier = Modifier.weight(1f)) {
                                RemexNavHost(
                                        navController = navController,
                                        startDestination = startDestination,
                                        hasCompletedOnboarding = hasCompletedOnboarding,
                                        onMarkSplashShown = onMarkSplashShown,
                                        onQrScanned = onQrScanned,
                                        dashboardScreenContent = dashboardScreenContent,
                                        remoteControlScreenContent = remoteControlScreenContent,
                                        remoteMouseScreenContent = remoteMouseScreenContent,
                                        appLauncherScreenContent = appLauncherScreenContent,
                                        taskManagerScreenContent = taskManagerScreenContent,
                                        connectionScreenContent = connectionScreenContent,
                                        onNavigateToConnection = { navigateToConnection() },
                                        pagerState = if (showNav) pagerState else null,
                                        modifier = Modifier.fillMaxSize(),
                                )
                        }

                        // M3: NavigationBar slides in from bottom when nav routes are active
                        AnimatedVisibility(
                                visible = showNav,
                                enter =
                                        slideInVertically(
                                                animationSpec =
                                                        tween(350, easing = EmphasizedDecelerate),
                                        ) { it } +
                                                fadeIn(tween(350, easing = EmphasizedDecelerate)),
                                exit =
                                        slideOutVertically(
                                                animationSpec =
                                                        tween(200, easing = EmphasizedAccelerate),
                                        ) { it } +
                                                fadeOut(tween(200, easing = EmphasizedAccelerate)),
                        ) {
                                NavigationBar(modifier = Modifier.navigationBarsPadding()) {
                                        // Primary 4 nav items
                                        navItems.forEach { screen ->
                                                val isSelected = currentRoute == screen.route
                                                NavigationBarItem(
                                                        selected = isSelected,
                                                        onClick = { onNavItemClick(screen.route) },
                                                        icon = {
                                                                // M3: Badge the Dashboard icon when
                                                                // disconnected
                                                                if (screen == Screen.Dashboard &&
                                                                                !isConnected
                                                                ) {
                                                                        BadgedBox(
                                                                                badge = { Badge() }
                                                                        ) {
                                                                                Icon(
                                                                                        imageVector =
                                                                                                screen.icon,
                                                                                        contentDescription =
                                                                                                stringResource(
                                                                                                        screen.titleRes
                                                                                                ),
                                                                                )
                                                                        }
                                                                } else {
                                                                        Icon(
                                                                                imageVector =
                                                                                        screen.icon,
                                                                                contentDescription =
                                                                                        stringResource(
                                                                                                screen.titleRes
                                                                                        ),
                                                                        )
                                                                }
                                                        },
                                                        label = {
                                                                Text(
                                                                        stringResource(
                                                                                screen.titleRes
                                                                        )
                                                                )
                                                        },
                                                        alwaysShowLabel =
                                                                false, // M3: label only on selected
                                                        // item
                                                        )
                                        }

                                        // "More" item — opens the overflow bottom sheet
                                        val moreSelected =
                                                moreItems.any { it.route == currentRoute }
                                        NavigationBarItem(
                                                selected = moreSelected,
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.KEYBOARD_TAP
                                                        )
                                                        showMoreSheet = true
                                                },
                                                icon = {
                                                        Icon(
                                                                imageVector =
                                                                        Icons.Default.MoreHoriz,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string
                                                                                        .nav_more_label
                                                                        ),
                                                        )
                                                },
                                                label = {
                                                        Text(
                                                                stringResource(
                                                                        R.string.nav_more_label
                                                                )
                                                        )
                                                },
                                                alwaysShowLabel = false,
                                        )
                                }
                        }
                }
        } else {
                // ── Medium / Expanded (tablet, foldable): NavigationRail ──
                Row(modifier = Modifier.fillMaxSize()) {
                        // M3: NavigationRail slides in from start
                        AnimatedVisibility(
                                visible = showNav,
                                enter =
                                        slideInHorizontally(
                                                animationSpec =
                                                        tween(350, easing = EmphasizedDecelerate),
                                        ) { -it } +
                                                fadeIn(tween(350, easing = EmphasizedDecelerate)),
                                exit =
                                        slideOutHorizontally(
                                                animationSpec =
                                                        tween(200, easing = EmphasizedAccelerate),
                                        ) { -it } +
                                                fadeOut(tween(200, easing = EmphasizedAccelerate)),
                        ) {
                                NavigationRail(
                                        containerColor =
                                                MaterialTheme.colorScheme.surfaceContainerLow,
                                ) {
                                        Spacer(modifier = Modifier.height(16.dp))

                                        // Primary items — top of rail
                                        navItems.forEach { screen ->
                                                NavigationRailItem(
                                                        selected = currentRoute == screen.route,
                                                        onClick = { onNavItemClick(screen.route) },
                                                        icon = {
                                                                if (screen == Screen.Dashboard &&
                                                                                !isConnected
                                                                ) {
                                                                        BadgedBox(
                                                                                badge = { Badge() }
                                                                        ) {
                                                                                Icon(
                                                                                        imageVector =
                                                                                                screen.icon,
                                                                                        contentDescription =
                                                                                                stringResource(
                                                                                                        screen.titleRes
                                                                                                ),
                                                                                )
                                                                        }
                                                                } else {
                                                                        Icon(
                                                                                imageVector =
                                                                                        screen.icon,
                                                                                contentDescription =
                                                                                        stringResource(
                                                                                                screen.titleRes
                                                                                        ),
                                                                        )
                                                                }
                                                        },
                                                        label = {
                                                                Text(
                                                                        stringResource(
                                                                                screen.titleRes
                                                                        )
                                                                )
                                                        },
                                                        alwaysShowLabel = false,
                                                )
                                        }

                                        Spacer(modifier = Modifier.weight(1f))

                                        // M3: Divider separates primary from overflow destinations
                                        HorizontalDivider(
                                                modifier =
                                                        Modifier.width(40.dp)
                                                                .padding(vertical = 8.dp),
                                                color = MaterialTheme.colorScheme.outlineVariant,
                                        )

                                        // Overflow items — bottom of rail
                                        moreItems.forEach { screen ->
                                                NavigationRailItem(
                                                        selected = currentRoute == screen.route,
                                                        onClick = { onNavItemClick(screen.route) },
                                                        icon = {
                                                                Icon(
                                                                        imageVector = screen.icon,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        screen.titleRes
                                                                                ),
                                                                )
                                                        },
                                                        label = {
                                                                Text(
                                                                        stringResource(
                                                                                screen.titleRes
                                                                        )
                                                                )
                                                        },
                                                        alwaysShowLabel = false,
                                                )
                                        }

                                        Spacer(modifier = Modifier.height(16.dp))
                                }
                        }

                        Box(modifier = Modifier.weight(1f)) {
                                RemexNavHost(
                                        navController = navController,
                                        startDestination = startDestination,
                                        hasCompletedOnboarding = hasCompletedOnboarding,
                                        onMarkSplashShown = onMarkSplashShown,
                                        onQrScanned = onQrScanned,
                                        dashboardScreenContent = dashboardScreenContent,
                                        remoteControlScreenContent = remoteControlScreenContent,
                                        remoteMouseScreenContent = remoteMouseScreenContent,
                                        appLauncherScreenContent = appLauncherScreenContent,
                                        taskManagerScreenContent = taskManagerScreenContent,
                                        connectionScreenContent = connectionScreenContent,
                                        onNavigateToConnection = { navigateToConnection() },
                                        pagerState = if (showNav) pagerState else null,
                                        modifier = Modifier.fillMaxSize(),
                                )

                                if (showNav) {
                                        ConnectionStatusChip(
                                                isConnected = isConnected,
                                                modifier =
                                                        Modifier.align(Alignment.TopCenter)
                                                                .statusBarsPadding()
                                                                .padding(top = 4.dp),
                                        )
                                }
                        }
                }
        }

        // ─── More — ModalBottomSheet (compact/phone only) ────────────────────────
        if (showMoreSheet && isNavBarLayout) {
                ModalBottomSheet(
                        onDismissRequest = { showMoreSheet = false },
                        sheetState = moreSheetState,
                        containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
                ) {
                        Text(
                                text = stringResource(R.string.nav_more_label),
                                style = MaterialTheme.typography.titleSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(start = 28.dp, bottom = 4.dp),
                        )
                        moreItems.forEach { screen ->
                                NavigationDrawerItem(
                                        label = { Text(stringResource(screen.titleRes)) },
                                        icon = { Icon(screen.icon, contentDescription = null) },
                                        selected = currentRoute == screen.route,
                                        onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                )
                                                scope
                                                        .launch { moreSheetState.hide() }
                                                        .invokeOnCompletion {
                                                                if (!moreSheetState.isVisible)
                                                                        showMoreSheet = false
                                                        }
                                                navigateTo(screen.route)
                                        },
                                        modifier =
                                                Modifier.padding(
                                                        NavigationDrawerItemDefaults.ItemPadding
                                                ),
                                )
                        }
                        Spacer(modifier = Modifier.navigationBarsPadding())
                        Spacer(modifier = Modifier.height(8.dp))
                }
        }

        // ─── Exit confirmation dialog ─────────────────────────────────────────────
        if (showExitDialog) {
                val exitContext = LocalContext.current
                AlertDialog(
                        onDismissRequest = { showExitDialog = false },
                        title = { Text(stringResource(R.string.exit_dialog_title)) },
                        text = { Text(stringResource(R.string.exit_dialog_message)) },
                        confirmButton = {
                                Button(
                                        onClick = {
                                                showExitDialog = false
                                                (exitContext as? android.app.Activity)?.finish()
                                        }
                                ) { Text(stringResource(R.string.button_exit)) }
                        },
                        dismissButton = {
                                TextButton(onClick = { showExitDialog = false }) {
                                        Text(stringResource(R.string.button_cancel))
                                }
                        },
                )
        }
}

// ─── NavHost ─────────────────────────────────────────────────────────────────

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun RemexNavHost(
        navController: androidx.navigation.NavHostController,
        startDestination: String,
        hasCompletedOnboarding: Boolean,
        onMarkSplashShown: () -> Unit,
        onQrScanned: (String, Int, String) -> Unit,
        dashboardScreenContent: @Composable (() -> Unit) -> Unit,
        remoteControlScreenContent: @Composable (() -> Unit) -> Unit,
        remoteMouseScreenContent: @Composable (() -> Unit) -> Unit,
        appLauncherScreenContent: @Composable (() -> Unit) -> Unit,
        taskManagerScreenContent: @Composable (() -> Unit) -> Unit,
        connectionScreenContent: @Composable (() -> Unit) -> Unit,
        onNavigateToConnection: () -> Unit,
        pagerState: androidx.compose.foundation.pager.PagerState? = null,
        modifier: Modifier = Modifier,
) {
        NavHost(
                navController = navController,
                startDestination = startDestination,
                modifier = modifier,
                // M3 Expressive: container-transform-style enter (grow + fade in)
                enterTransition = {
                        scaleIn(
                                initialScale = 0.94f,
                                animationSpec = tween(350, easing = EmphasizedDecelerate),
                        ) + fadeIn(tween(350, easing = EmphasizedDecelerate))
                },
                exitTransition = {
                        scaleOut(
                                targetScale = 1.06f,
                                animationSpec = tween(200, easing = EmphasizedAccelerate),
                        ) + fadeOut(tween(200, easing = EmphasizedAccelerate))
                },
                popEnterTransition = {
                        scaleIn(
                                initialScale = 1.06f,
                                animationSpec = tween(350, easing = EmphasizedDecelerate),
                        ) + fadeIn(tween(350, easing = EmphasizedDecelerate))
                },
                popExitTransition = {
                        scaleOut(
                                targetScale = 0.94f,
                                animationSpec = tween(200, easing = EmphasizedAccelerate),
                        ) + fadeOut(tween(200, easing = EmphasizedAccelerate))
                },
        ) {
                composable(
                        Screen.Splash.route,
                        enterTransition = { fadeIn(tween(500)) },
                        exitTransition = { fadeOut(tween(500)) },
                ) {
                        SplashScreen(
                                onFinished = {
                                        onMarkSplashShown()
                                        navController.navigate(
                                                if (hasCompletedOnboarding) Screen.Dashboard.route
                                                else Screen.Tutorial.route
                                        ) {
                                                popUpTo(Screen.Splash.route) { inclusive = true }
                                                launchSingleTop = true
                                        }
                                },
                        )
                }

                composable(
                        Screen.Tutorial.route,
                        enterTransition = { fadeIn(tween(400)) },
                        exitTransition = { fadeOut(tween(400)) },
                ) {
                        TutorialScreen(
                                onFinished = {
                                        navController.navigate(Screen.Dashboard.route) {
                                                popUpTo(Screen.Tutorial.route) { inclusive = true }
                                                launchSingleTop = true
                                        }
                                },
                        )
                }

                // Helper composable for primary nav screens with pager support
                val primaryNavContent: @Composable () -> Unit = {
                        if (pagerState != null) {
                                // Wrap primary nav screens in HorizontalPager for swipe navigation
                                HorizontalPager(
                                        state = pagerState,
                                        modifier = Modifier.fillMaxSize(),
                                        userScrollEnabled = true
                                ) { page ->
                                        when (navItems[page].route) {
                                                Screen.Dashboard.route ->
                                                        dashboardScreenContent {
                                                                onNavigateToConnection()
                                                        }
                                                Screen.RemoteControl.route ->
                                                        remoteControlScreenContent {
                                                                onNavigateToConnection()
                                                        }
                                                Screen.AppLauncher.route ->
                                                        appLauncherScreenContent {
                                                                onNavigateToConnection()
                                                        }
                                                Screen.TaskManager.route ->
                                                        taskManagerScreenContent {
                                                                onNavigateToConnection()
                                                        }
                                        }
                                }
                        }
                }

                composable(Screen.Dashboard.route) {
                        if (pagerState != null) {
                                primaryNavContent()
                        } else {
                                dashboardScreenContent { onNavigateToConnection() }
                        }
                }

                composable(Screen.Connection.route) {
                        connectionScreenContent { navController.navigate(Screen.QrScanner.route) }
                }

                composable(
                        Screen.QrScanner.route,
                        // QR scanner enters from bottom — modal feel
                        enterTransition = {
                                slideInVertically(tween(350, easing = EmphasizedDecelerate)) {
                                        it
                                } + fadeIn(tween(350, easing = EmphasizedDecelerate))
                        },
                        exitTransition = {
                                slideOutVertically(tween(250, easing = EmphasizedAccelerate)) {
                                        it
                                } + fadeOut(tween(250, easing = EmphasizedAccelerate))
                        },
                ) {
                        QrScannerScreen(
                                onScanned = { host, port, key ->
                                        onQrScanned(host, port, key)
                                        navController.navigate(Screen.Dashboard.route) {
                                                popUpTo(Screen.Dashboard.route) { inclusive = true }
                                                launchSingleTop = true
                                        }
                                },
                                onBack = { navController.popBackStack() },
                        )
                }

                composable(Screen.RemoteControl.route) {
                        if (pagerState != null) {
                                primaryNavContent()
                        } else {
                                remoteControlScreenContent { onNavigateToConnection() }
                        }
                }

                composable(Screen.RemoteMouse.route) {
                        remoteMouseScreenContent { onNavigateToConnection() }
                }

                composable(Screen.AppLauncher.route) {
                        if (pagerState != null) {
                                primaryNavContent()
                        } else {
                                appLauncherScreenContent { onNavigateToConnection() }
                        }
                }

                composable(Screen.TaskManager.route) {
                        if (pagerState != null) {
                                primaryNavContent()
                        } else {
                                taskManagerScreenContent { onNavigateToConnection() }
                        }
                }

                composable(
                        Screen.RemoteDesktop.route,
                        // Full-screen immersive — pure crossfade, no spatial motion
                        enterTransition = { fadeIn(tween(300)) },
                        exitTransition = { fadeOut(tween(300)) },
                        popEnterTransition = { fadeIn(tween(300)) },
                        popExitTransition = { fadeOut(tween(300)) },
                ) { RemoteDesktopScreen() }

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
                                },
                        )
                }

                composable(Screen.Faq.route) { FaqScreen() }

                composable(Screen.About.route) { AboutScreen() }
        }
}

// ─── Previews ─────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3AdaptiveNavigationSuiteApi::class)
@Preview(showBackground = true)
@Composable
private fun AppNavigationPreview() {
        RemExTheme {
                AppNavigationContent(
                        hasCompletedOnboarding = true,
                        splashShown = true,
                        isConnected = true,
                        onMarkSplashShown = {},
                        onQrScanned = { _, _, _ -> },
                        dashboardScreenContent = { Box(Modifier.fillMaxSize()) },
                        remoteControlScreenContent = { Box(Modifier.fillMaxSize()) },
                        remoteMouseScreenContent = { Box(Modifier.fillMaxSize()) },
                        appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                        taskManagerScreenContent = { Box(Modifier.fillMaxSize()) },
                        connectionScreenContent = { Box(Modifier.fillMaxSize()) },
                )
        }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3AdaptiveNavigationSuiteApi::class)
@Preview(showBackground = true)
@Composable
private fun AppNavigationDisconnectedPreview() {
        RemExTheme {
                AppNavigationContent(
                        hasCompletedOnboarding = true,
                        splashShown = true,
                        isConnected = false,
                        onMarkSplashShown = {},
                        onQrScanned = { _, _, _ -> },
                        dashboardScreenContent = { Box(Modifier.fillMaxSize()) },
                        remoteControlScreenContent = { Box(Modifier.fillMaxSize()) },
                        remoteMouseScreenContent = { Box(Modifier.fillMaxSize()) },
                        appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                        taskManagerScreenContent = { Box(Modifier.fillMaxSize()) },
                        connectionScreenContent = { Box(Modifier.fillMaxSize()) },
                )
        }
}
