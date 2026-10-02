package com.clindsay94.remex.ui.navigation

import android.view.HapticFeedbackConstants
import androidx.activity.compose.PredictiveBackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
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
import androidx.compose.material3.adaptive.currentWindowAdaptiveInfoV2
import androidx.compose.material3.adaptive.navigationsuite.ExperimentalMaterial3AdaptiveNavigationSuiteApi
import androidx.compose.material3.adaptive.navigationsuite.NavigationSuiteScaffoldDefaults
import androidx.compose.material3.adaptive.navigationsuite.NavigationSuiteType
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.material3.SheetValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.toRoute
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.data.SplashStyles
import com.clindsay94.remex.ui.splash.LiveHandshakeController
import com.clindsay94.remex.ui.splash.LiveHandshakeSplash
import com.clindsay94.remex.ui.splash.rememberLiveHandshakeLensModifier
import com.clindsay94.remex.ui.screens.AboutScreen
import com.clindsay94.remex.ui.routines.RoutineEditorChrome
import com.clindsay94.remex.ui.routines.RoutineOpenRequests
import com.clindsay94.remex.ui.routines.RoutinesScreen
import com.clindsay94.remex.ui.PairingRouteArgs
import com.clindsay94.remex.ui.PairingRouteResult
import com.clindsay94.remex.ui.screens.AppLauncherScreen
import com.clindsay94.remex.ui.screens.FileTransferScreen
import com.clindsay94.remex.ui.screens.ConnectionScreen
import com.clindsay94.remex.ui.screens.ConnectionStatusChip
import com.clindsay94.remex.ui.screens.ConnectionViewModel
import com.clindsay94.remex.ui.screens.ControlTabScreen
import com.clindsay94.remex.ui.screens.DashboardScreen
import com.clindsay94.remex.ui.screens.DesktopTabScreen
import com.clindsay94.remex.ui.screens.FaqScreen
import com.clindsay94.remex.ui.screens.HomeScreen
import com.clindsay94.remex.ui.screens.QrScannerScreen
import com.clindsay94.remex.ui.screens.RemoteDesktopScreen
import com.clindsay94.remex.ui.screens.SettingsScreen
import com.clindsay94.remex.ui.screens.ShareDiagnosticsScreen
import com.clindsay94.remex.ui.screens.SplashScreen
import com.clindsay94.remex.ui.screens.TutorialScreen
import com.clindsay94.remex.ui.theme.RemExTheme
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/**
 * The route owner for the four primary pager tabs (Home, Desktop, Apps, Control) — typed like every other destination
 * (RemEx-mt43). Private because it is an implementation detail of this shell: the tabs inside it
 * are pager pages, not navigation destinations.
 */
@kotlinx.serialization.Serializable
private data object PrimaryNav

// Destinations that suppress the navigation chrome (full-screen / flow screens). Class references
// rather than instances because the check runs against the back stack's NavDestination via
// hasRoute, which matches on the route class.
private val noNavChrome =
        setOf(
                Screen.Splash::class,
                Screen.Tutorial::class,
                Screen.RemoteDesktop::class,
                Screen.QrScanner::class,
        )

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppNavigation() {
        val context = LocalContext.current
        val settingsManager = remember { SettingsManager(context) }
        val connectionViewModel: ConnectionViewModel = viewModel()

        val hasCompletedOnboarding by
                settingsManager.hasCompletedOnboardingFlow.collectAsStateWithLifecycle(initialValue = null)
        // Nullable until DataStore's first emission: the Splash route decides which splash plays
        // from this value exactly once, so it must be the stored style, never the default that
        // stands in for it while loading (RemEx-8g6n0).
        val personalization by
                settingsManager.personalizationPreferencesFlow.collectAsStateWithLifecycle(initialValue = null)
        val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
        // The More "New" badge shows until Routines is opened once (routines spec 1.2, R-UX-05).
        // Initial true so a cold start never flashes a badge the user already dismissed.
        val routinesOpened by settingsManager.routinesOpenedFlow.collectAsStateWithLifecycle(initialValue = true)

        // Live Handshake (RemEx-8g6n0) is an overlay ABOVE the app rather than a route: the
        // dashboard composes underneath and the portal opens into it, refracted by the lens on
        // the content layer below.
        val liveHandshake = remember { LiveHandshakeController(context.applicationContext) }
        DisposableEffect(liveHandshake) { onDispose { liveHandshake.finish() } }
        val lensModifier = rememberLiveHandshakeLensModifier(liveHandshake.lens)

        Box(modifier = Modifier.fillMaxSize()) {
                // The lens layer exists only while the splash does: no permanent layer over the
                // whole app (and so none over the remote-desktop SurfaceView).
                Box(
                        modifier = Modifier.fillMaxSize()
                                .then(if (liveHandshake.session != null) lensModifier else Modifier)
                ) {
                        AppNavigationContent(
                                hasCompletedOnboarding = if (personalization == null) null else hasCompletedOnboarding,
                                isConnected = isConnected,
                                showRoutinesBadge = !routinesOpened,
                                splashStyle = (personalization ?: SettingsManager.PersonalizationPreferences()).splashStyle,
                                liveHandshake = liveHandshake,
                                onQrScanned = { host, port, pin ->
                                        connectionViewModel.applyQrResultAndConnect(host, port, pin)
                                },
                                dashboardScreenContent = { onNav, isVisible ->
                                        DashboardScreen(onNavigateToConnection = onNav, isVisible = isVisible)
                                },
                                homeScreenContent = { onNav, onOpenDestination ->
                                        HomeScreen(
                                                onNavigateToConnection = onNav,
                                                onOpenDestination = onOpenDestination,
                                                connectionViewModel = connectionViewModel,
                                        )
                                },
                                desktopScreenContent = { mode, onModeChange, onStartStream, onNav ->
                                        DesktopTabScreen(
                                                mode = mode,
                                                onModeChange = onModeChange,
                                                onStartStream = onStartStream,
                                                onNavigateToConnection = onNav,
                                        )
                                },
                                appLauncherScreenContent = { onNav ->
                                        AppLauncherScreen(onNavigateToConnection = onNav)
                                },
                                controlScreenContent = { segment, onSegmentChange, isVisible, onNav ->
                                        ControlTabScreen(
                                                segment = segment,
                                                onSegmentChange = onSegmentChange,
                                                isVisible = isVisible,
                                                onNavigateToConnection = onNav,
                                        )
                                },
                                connectionScreenContent = { onQr ->
                                        ConnectionScreen(
                                                viewModel = connectionViewModel,
                                                onNavigateToQrScanner = onQr
                                        )
                                },
                        )
                }
                liveHandshake.session?.let { session ->
                        LiveHandshakeSplash(
                                signals = session.signals,
                                lens = liveHandshake.lens,
                                continueFromSystemSplash = true,
                                onFinished = { liveHandshake.finish() },
                        )
                }
        }
}

@OptIn(
        ExperimentalMaterial3Api::class,
        ExperimentalMaterial3AdaptiveNavigationSuiteApi::class,
        ExperimentalMaterial3ExpressiveApi::class,
)
@Composable
private fun AppNavigationContent(
        hasCompletedOnboarding: Boolean?,
        isConnected: Boolean,
        splashStyle: String,
        liveHandshake: LiveHandshakeController? = null,
        onQrScanned: (String, Int, String) -> Unit,
        dashboardScreenContent: @Composable (onNavigateToConnection: () -> Unit, isVisible: Boolean) -> Unit,
        homeScreenContent: HomeScreenContent,
        desktopScreenContent: DesktopScreenContent,
        appLauncherScreenContent: @Composable (onNavigateToConnection: () -> Unit) -> Unit,
        controlScreenContent: ControlScreenContent,
        connectionScreenContent: @Composable (onNavigateToQrScanner: () -> Unit) -> Unit,
        showRoutinesBadge: Boolean = false,
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

        val navController = rememberNavController()
        val currentBackStackEntry by navController.currentBackStackEntryAsState()
        val currentDestination = currentBackStackEntry?.destination
        // The two identity questions the whole shell asks, answered once. hasRoute matches the
        // destination's route class, which is what replaced string comparison (RemEx-mt43).
        val isAtPrimary = currentDestination?.hasRoute<PrimaryNav>() == true
        fun isOn(screen: Screen) = currentDestination?.hasRoute(screen::class) == true

        // Live Handshake's "ready": the destination past the splash has composed its first frame
        // under the overlay, so the portal has a real app to open into (RemEx-8g6n0).
        val liveSession = liveHandshake?.session
        if (liveSession != null && (isAtPrimary || isOn(Screen.Tutorial))) {
                LaunchedEffect(liveSession) {
                        withFrameNanos { }
                        liveSession.markReady()
                }
        }

        // Keep primary destinations under one route owner so connected screens do not
        // re-create route-scoped ViewModels during tab changes.
        val pagerState = rememberPagerState(pageCount = { navItems.size })
        var selectedPrimaryIndex by rememberSaveable { mutableIntStateOf(0) }
        // The Desktop tab's mode and the Control tab's segment live here, above the pager, so a
        // page the pager disposes comes back as the user left it (RemEx-wqo7a.2).
        var desktopMode by rememberSaveable { mutableStateOf(DesktopMode.Stream) }
        var controlSegment by rememberSaveable { mutableStateOf(ControlSegment.Commands) }

        val view = LocalView.current
        val scope = rememberCoroutineScope()

        // ─── Adaptive layout ─────────────────────────────────────────────────────
        val adaptiveInfo = currentWindowAdaptiveInfoV2()
        // M3: NavigationBar on compact, NavigationRail on medium, NavigationDrawer on expanded
        val suiteType = NavigationSuiteScaffoldDefaults.calculateFromAdaptiveInfo(adaptiveInfo)
        // The compact Routines editor hides the NavigationBar so its floating toolbar owns the
        // bottom edge (routines spec 2.1). It is a pane inside Screen.Routines, not a route, so
        // RoutineEditorChrome says when it is up; the rail on wider layouts is unaffected.
        val routineEditorOwnsBottom =
                suiteType == NavigationSuiteType.NavigationBar &&
                        isOn(Screen.Routines) &&
                        RoutineEditorChrome.editorShowing
        val showNav =
                currentDestination != null &&
                        noNavChrome.none { currentDestination.hasRoute(it) } &&
                        !routineEditorOwnsBottom

        val layoutType = if (showNav) suiteType else NavigationSuiteType.None

        val isNavBarLayout = layoutType == NavigationSuiteType.NavigationBar

        // ─── State ───────────────────────────────────────────────────────────────
        var showExitDialog by rememberSaveable { mutableStateOf(false) }
        var showMoreSheet by remember { mutableStateOf(false) }
        val moreSheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))

        // Close more sheet on route change
        // Close the More sheet when the route changes underneath it (deep links, pairing
        // navigation) — but let the hide animation finish before unmounting (RemEx-rzzo).
        LaunchedEffect(currentDestination) {
                if (showMoreSheet) {
                        moreSheetState.hide()
                        showMoreSheet = false
                }
        }

        LaunchedEffect(isAtPrimary, selectedPrimaryIndex) {
                if (isAtPrimary && pagerState.currentPage != selectedPrimaryIndex) {
                        pagerState.animateScrollToPage(selectedPrimaryIndex)
                }
        }

        LaunchedEffect(isAtPrimary, pagerState.settledPage) {
                if (isAtPrimary) {
                        selectedPrimaryIndex = pagerState.settledPage
                }
        }

        // Back handler: show exit confirmation when at the primary root destination
        val isAtRoot = isAtPrimary
        // Animatable rather than a raw Float: mid-gesture the value snaps to the finger, but a
        // cancelled (or completed) gesture springs the shell home instead of jumping to identity
        // in one frame (RemEx-gblj). The graphicsLayer gates read value > 0f so the return spring
        // stays visible after the gesture itself has ended.
        val backProgress = remember { Animatable(0f) }
        val backReturnSpec = MaterialTheme.motionScheme.defaultSpatialSpec<Float>()

        PredictiveBackHandler(enabled = isAtRoot) { backEventFlow ->
                try {
                        backEventFlow.collect { event ->
                                backProgress.snapTo(event.progress)
                        }
                        showExitDialog = true
                        backProgress.animateTo(0f, backReturnSpec)
                } catch (e: CancellationException) {
                        // onBackCancelled() cancels THIS coroutine too (androidx source: it
                        // cancels both the event channel and the handler job), so the return
                        // spring must run on the composition scope that survives the gesture.
                        scope.launch { backProgress.animateTo(0f, backReturnSpec) }
                        throw e
                }
        }

        // Gated on STARTED so a pairing request that arrives while the app is backgrounded does
        // not drive navigation behind the user's back; it is handled when they come back.
        //
        // Gating ALONE would have made this worse rather than better. pairingRequired has
        // replay = 1, so re-subscribing on every foreground redelivers the last request — the app
        // would walk to the PIN screen every single time it resumed. consumePairingRequest() is
        // what makes the pair safe: handle it once, then drop it. The isConnected check below is now
        // a second line of defence rather than the only one.
        val lifecycleOwner = LocalLifecycleOwner.current
        LaunchedEffect(lifecycleOwner) {
                lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
                        RemexClientManager.pairingRequired.collect { (host, port) ->
                                // Pairing is only meaningful when not connected. Deliberately does
                                // NOT consume: isConnected is cleared only when the native layer
                                // NOTICES the link is gone, so it can be stale-true after the phone
                                // leaves the LAN — and connect() has no already-connected early
                                // return, so a genuine request can be emitted in that state.
                                // Destroying it here would strand the user on a Connect button that
                                // does nothing until the staleness resolves. Leaving it in the
                                // replay cache is what the pre-existing guard did, and it is right:
                                // consume only where we actually acted.
                                if (RemexClientManager.isConnected.value) return@collect

                                // THROUGH THE VALIDATION, NOT AROUND IT (RemEx-ph4nw, kept by
                                // RemEx-mt43). The typed PairingRoute proves the arguments are
                                // PRESENT - it cannot prove they are USABLE. A blank host still
                                // sends the user to a pairing screen that looks operational and
                                // cannot succeed, and an out-of-range port still offers a PIN to
                                // a machine nobody asked for. These values come off
                                // pairingRequired, which the native layer emits, so they are not
                                // ours to assume well-formed - parse refuses them where the caller
                                // can still do something about it.
                                val parsed = PairingRouteArgs.parse(host, port.toString())
                                if (parsed !is PairingRouteResult.Valid) {
                                        // DELIBERATELY NOT CONSUMED. Consuming a request we then
                                        // refuse to act on would strand the user with nothing on
                                        // screen and nothing to retry; leaving it in the replay cache
                                        // is what the guard above does for the same reason. The log
                                        // line is the only place this becomes visible, so it names
                                        // both values.
                                        android.util.Log.w(
                                                "AppNavigation",
                                                "Refusing to open pairing: the host and port are not a usable route " +
                                                        "(host='$host', port=$port)."
                                        )
                                        return@collect
                                }

                                RemexClientManager.consumePairingRequest()
                                navController.navigate(PairingRoute(parsed.host, parsed.port)) {
                                        launchSingleTop = true
                                }
                        }
                }
        }

        // ─── Navigation helpers ───────────────────────────────────────────────────
        fun navigateTo(screen: Screen) {
                navController.navigate(screen) {
                        popUpTo(PrimaryNav) { saveState = true }
                        launchSingleTop = true
                        restoreState = true
                }
        }

        fun navigateToPrimary(index: Int) {
                selectedPrimaryIndex = index.coerceIn(0, navItems.lastIndex)
                if (!isAtPrimary) {
                        navController.navigate(PrimaryNav) {
                                popUpTo(PrimaryNav) { saveState = true }
                                launchSingleTop = true
                                restoreState = true
                        }
                }
        }

        fun navigateToConnection() {
                navController.navigate(Screen.Connection) {
                        popUpTo(PrimaryNav) { saveState = true }
                        launchSingleTop = true
                        restoreState = true
                }
        }

        fun onNavItemClick(screen: NavDestination) {
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                // Object identity replaces the old route-string lookup; the pager index is the
                // position in navItems, whose order is load-bearing (see NavRoutes.kt). navItems is
                // List<PrimaryDestination> (RemEx-740mr) but `screen` here is the wider
                // NavDestination (moreItems calls this too), so indexOfFirst rather than indexOf —
                // indexOf's parameter type is PrimaryDestination and would not accept `screen`.
                val primaryIndex = navItems.indexOfFirst { it == screen }
                if (primaryIndex >= 0) {
                        navigateToPrimary(primaryIndex)
                } else {
                        navigateTo(screen)
                }
        }

        // A routine notification's Open / "See what happened" (RemEx-pp0rt.6): go to Routines,
        // which consumes the request and shows the run. Never over the splash or the tutorial,
        // which navigate onward themselves; the key on currentDestination retries once they have.
        val pendingRoutineOpen by RoutineOpenRequests.pending.collectAsStateWithLifecycle()
        LaunchedEffect(pendingRoutineOpen, currentDestination) {
                if (pendingRoutineOpen == null || currentDestination == null) return@LaunchedEffect
                if (isOn(Screen.Splash) || isOn(Screen.Tutorial) || isOn(Screen.Routines)) return@LaunchedEffect
                navigateTo(Screen.Routines)
        }

        // ─── Adaptive layout shell ────────────────────────────────────────────────
        if (isNavBarLayout || !showNav) {
                // ── Compact (phone) or full-screen routes: Scaffold-style with bottom
                // NavigationBar ──
                Column(
                        modifier = Modifier
                                .fillMaxSize()
                                .graphicsLayer {
                                        if (backProgress.value > 0f) {
                                                scaleX = 1.0f - (backProgress.value * 0.08f)
                                                scaleY = 1.0f - (backProgress.value * 0.08f)
                                                translationX = backProgress.value * 48f * view.context.resources.displayMetrics.density
                                                alpha = 1.0f - (backProgress.value * 0.2f)
                                        }
                                }
                ) {
                        Box(modifier = Modifier.weight(1f)) {
                                RemexNavHost(
                                        navController = navController,
                                        hasCompletedOnboarding = hasCompletedOnboarding,
                                        splashStyle = splashStyle,
                                        onQrScanned = onQrScanned,
                                        dashboardScreenContent = dashboardScreenContent,
                                        homeScreenContent = homeScreenContent,
                                        desktopScreenContent = desktopScreenContent,
                                        appLauncherScreenContent = appLauncherScreenContent,
                                        controlScreenContent = controlScreenContent,
                                        connectionScreenContent = connectionScreenContent,
                                        desktopMode = desktopMode,
                                        onDesktopModeChange = { desktopMode = it },
                                        controlSegment = controlSegment,
                                        onControlSegmentChange = { controlSegment = it },
                                        onOpenDestination = { onNavItemClick(it) },
                                        onNavigateToConnection = { navigateToConnection() },
                                        onSelectPrimaryPage = { selectedPrimaryIndex = it },
                                        // Always the hoisted pager, even while a no-chrome route
                                        // (the stream) covers it: nulling it mid-exit swapped the
                                        // fading Desktop tab for Home and flashed it.
                                        pagerState = pagerState,
                                        onLiveHandshakeStart = liveHandshake?.let { it::start },
                                        modifier = Modifier.fillMaxSize(),
                                )
                        }

                        // M3: NavigationBar slides in from bottom when nav routes are active
                        AnimatedVisibility(
                                visible = showNav,
                                enter =
                                        slideInVertically(
                                                animationSpec =
                                                        MaterialTheme.motionScheme.defaultSpatialSpec(),
                                        ) { it } +
                                                fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
                                exit =
                                        slideOutVertically(
                                                animationSpec =
                                                        MaterialTheme.motionScheme.fastSpatialSpec(),
                                        ) { it } +
                                                fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                        ) {
                                NavigationBar {
                                        // The four primary tabs: Home, Desktop, Apps, Control
                                        navItems.forEachIndexed { index, screen ->
                                                val isSelected =
                                                        isAtPrimary && selectedPrimaryIndex == index
                                                NavigationBarItem(
                                                        selected = isSelected,
                                                        onClick = { onNavItemClick(screen) },
                                                        icon = {
                                                                // A single BadgedBox keeps the icon identity
                                                                // stable; the badge scales+fades in and out
                                                                // (RemEx-pgzk). The TalkBack stateDescription
                                                                // applies only while the badge shows (8d9k).
                                                                val showDisconnectedBadge =
                                                                        screen == Screen.Home && !isConnected
                                                                val disconnectedLabel =
                                                                        stringResource(
                                                                                R.string.status_disconnected
                                                                        )
                                                                BadgedBox(
                                                                        badge = {
                                                                                androidx.compose.animation.AnimatedVisibility(
                                                                                        visible = showDisconnectedBadge,
                                                                                        enter = scaleIn(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastSpatialSpec()
                                                                                        ) + fadeIn(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastEffectsSpec()
                                                                                        ),
                                                                                        exit = scaleOut(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastSpatialSpec()
                                                                                        ) + fadeOut(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastEffectsSpec()
                                                                                        ),
                                                                                ) {
                                                                                        Badge()
                                                                                }
                                                                        },
                                                                        modifier =
                                                                                Modifier.semantics {
                                                                                        if (showDisconnectedBadge) {
                                                                                                stateDescription =
                                                                                                        disconnectedLabel
                                                                                        }
                                                                                },
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
                                                        },
                                                        label = {
                                                                Text(
                                                                        stringResource(
                                                                                screen.titleRes
                                                                        )
                                                                )
                                                        },
                                                        // One-word labels, always shown
                                                        // (refresh spec, Navigation).
                                                        alwaysShowLabel = true,
                                                        )
                                        }

                                        // "More" item — opens the overflow bottom sheet
                                        val moreSelected = moreItems.any { isOn(it) }
                                        NavigationBarItem(
                                                selected = moreSelected,
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.KEYBOARD_TAP
                                                        )
                                                        showMoreSheet = true
                                                },
                                                icon = {
                                                        // "New" until Routines is opened once, with
                                                        // the disconnected badge's motion (spec M14).
                                                        NewBadgedIcon(
                                                                show = showRoutinesBadge,
                                                                imageVector = Icons.Default.MoreHoriz,
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
                                                alwaysShowLabel = true,
                                        )
                                }
                        }
                }
        } else {
                // ── Medium / Expanded (tablet, foldable): NavigationRail ──
                Row(
                        modifier = Modifier
                                .fillMaxSize()
                                .graphicsLayer {
                                        if (backProgress.value > 0f) {
                                                scaleX = 1.0f - (backProgress.value * 0.08f)
                                                scaleY = 1.0f - (backProgress.value * 0.08f)
                                                translationX = backProgress.value * 48f * view.context.resources.displayMetrics.density
                                                alpha = 1.0f - (backProgress.value * 0.2f)
                                        }
                                }
                ) {
                        // M3: NavigationRail slides in from start
                        AnimatedVisibility(
                                visible = showNav,
                                enter =
                                        slideInHorizontally(
                                                animationSpec =
                                                        MaterialTheme.motionScheme.defaultSpatialSpec(),
                                        ) { -it } +
                                                fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
                                exit =
                                        slideOutHorizontally(
                                                animationSpec =
                                                        MaterialTheme.motionScheme.fastSpatialSpec(),
                                        ) { -it } +
                                                fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                        ) {
                                NavigationRail(
                                        containerColor =
                                                MaterialTheme.colorScheme.surfaceContainerLow,
                                ) {
                                        Spacer(modifier = Modifier.height(16.dp))

                                        // Primary items — top of rail
                                        navItems.forEachIndexed { index, screen ->
                                                NavigationRailItem(
                                                        selected =
                                                                isAtPrimary &&
                                                                        selectedPrimaryIndex ==
                                                                                index,
                                                        onClick = { onNavItemClick(screen) },
                                                        icon = {
                                                                // A single BadgedBox keeps the icon identity
                                                                // stable; the badge scales+fades in and out
                                                                // (RemEx-pgzk). The TalkBack stateDescription
                                                                // applies only while the badge shows (8d9k).
                                                                val showDisconnectedBadge =
                                                                        screen == Screen.Home && !isConnected
                                                                val disconnectedLabel =
                                                                        stringResource(
                                                                                R.string.status_disconnected
                                                                        )
                                                                BadgedBox(
                                                                        badge = {
                                                                                androidx.compose.animation.AnimatedVisibility(
                                                                                        visible = showDisconnectedBadge,
                                                                                        enter = scaleIn(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastSpatialSpec()
                                                                                        ) + fadeIn(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastEffectsSpec()
                                                                                        ),
                                                                                        exit = scaleOut(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastSpatialSpec()
                                                                                        ) + fadeOut(
                                                                                                MaterialTheme.motionScheme
                                                                                                        .fastEffectsSpec()
                                                                                        ),
                                                                                ) {
                                                                                        Badge()
                                                                                }
                                                                        },
                                                                        modifier =
                                                                                Modifier.semantics {
                                                                                        if (showDisconnectedBadge) {
                                                                                                stateDescription =
                                                                                                        disconnectedLabel
                                                                                        }
                                                                                },
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
                                                        },
                                                        label = {
                                                                Text(
                                                                        stringResource(
                                                                                screen.titleRes
                                                                        )
                                                                )
                                                        },
                                                        alwaysShowLabel = true,
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
                                                        selected = isOn(screen),
                                                        onClick = { onNavItemClick(screen) },
                                                        icon = {
                                                                NewBadgedIcon(
                                                                        show = showRoutinesBadge && screen == Screen.Routines,
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
                                                        alwaysShowLabel = true,
                                                )
                                        }

                                        Spacer(modifier = Modifier.height(16.dp))
                                }
                        }

                        Box(modifier = Modifier.weight(1f)) {
                                RemexNavHost(
                                        navController = navController,
                                        hasCompletedOnboarding = hasCompletedOnboarding,
                                        splashStyle = splashStyle,
                                        onQrScanned = onQrScanned,
                                        dashboardScreenContent = dashboardScreenContent,
                                        homeScreenContent = homeScreenContent,
                                        desktopScreenContent = desktopScreenContent,
                                        appLauncherScreenContent = appLauncherScreenContent,
                                        controlScreenContent = controlScreenContent,
                                        connectionScreenContent = connectionScreenContent,
                                        desktopMode = desktopMode,
                                        onDesktopModeChange = { desktopMode = it },
                                        controlSegment = controlSegment,
                                        onControlSegmentChange = { controlSegment = it },
                                        onOpenDestination = { onNavItemClick(it) },
                                        onNavigateToConnection = { navigateToConnection() },
                                        onSelectPrimaryPage = { selectedPrimaryIndex = it },
                                        // Always the hoisted pager, even while a no-chrome route
                                        // (the stream) covers it: nulling it mid-exit swapped the
                                        // fading Desktop tab for Home and flashed it.
                                        pagerState = pagerState,
                                        onLiveHandshakeStart = liveHandshake?.let { it::start },
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
                                val newLabel = stringResource(R.string.routines_badge_new)
                                val showNew = showRoutinesBadge && screen == Screen.Routines
                                NavigationDrawerItem(
                                        label = { Text(stringResource(screen.titleRes)) },
                                        icon = { Icon(screen.icon, contentDescription = null) },
                                        badge =
                                                if (showNew) {
                                                        { Badge { Text(newLabel) } }
                                                } else {
                                                        null
                                                },
                                        selected = isOn(screen),
                                        onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                )
                                                // Navigate only after the hide animation
                                                // completes so the sheet slides out instead of
                                                // being destroyed mid-animation (RemEx-rzzo).
                                                scope
                                                        .launch { moreSheetState.hide() }
                                                        .invokeOnCompletion { cause ->
                                                                if (!moreSheetState.isVisible)
                                                                        showMoreSheet = false
                                                                // Only navigate when the hide ran
                                                                // to completion — a preempted hide
                                                                // (double-tap race) must not fire
                                                                // a stale navigation.
                                                                if (cause == null)
                                                                        navigateTo(screen)
                                                        }
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

@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun RemexNavHost(
        navController: androidx.navigation.NavHostController,
        hasCompletedOnboarding: Boolean,
        splashStyle: String,
        onQrScanned: (String, Int, String) -> Unit,
        dashboardScreenContent: @Composable ((() -> Unit), Boolean) -> Unit,
        homeScreenContent: HomeScreenContent,
        desktopScreenContent: DesktopScreenContent,
        appLauncherScreenContent: @Composable (() -> Unit) -> Unit,
        controlScreenContent: ControlScreenContent,
        connectionScreenContent: @Composable (() -> Unit) -> Unit,
        desktopMode: DesktopMode,
        onDesktopModeChange: (DesktopMode) -> Unit,
        controlSegment: ControlSegment,
        onControlSegmentChange: (ControlSegment) -> Unit,
        onOpenDestination: (NavDestination) -> Unit,
        onNavigateToConnection: () -> Unit,
        onSelectPrimaryPage: (Int) -> Unit,
        pagerState: androidx.compose.foundation.pager.PagerState? = null,
        onLiveHandshakeStart: (() -> Unit)? = null,
        modifier: Modifier = Modifier,
) {
        // NavHost's transition lambdas are NOT @Composable, so the motionScheme specs must be
        // captured here in composable scope and closed over. Enters use the default (emphasized)
        // tier, exits the fast tier — preserving the former slow-in / quick-out relationship.
        val enterScaleSpec = MaterialTheme.motionScheme.defaultSpatialSpec<Float>()
        val exitScaleSpec = MaterialTheme.motionScheme.fastSpatialSpec<Float>()
        val enterSlideSpec = MaterialTheme.motionScheme.defaultSpatialSpec<IntOffset>()
        val exitSlideSpec = MaterialTheme.motionScheme.fastSpatialSpec<IntOffset>()
        val enterFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
        val exitFadeSpec = MaterialTheme.motionScheme.fastEffectsSpec<Float>()
        NavHost(
                navController = navController,
                // Always start at splash, and it plays on every fresh open of the app, including
                // a relaunch while Android still has the process cached (Connor, 2026-10-02; this
                // reverses the per-process skip from RemEx-4j8ls P1-9, which read as "the splash
                // sometimes doesn't play"). A config-change recreation never replays it: the
                // restored back stack is already past Splash, so this composable isn't reached.
                startDestination = Screen.Splash,
                modifier = modifier,
                // M3 Expressive: container-transform-style enter (grow + fade in)
                enterTransition = {
                        scaleIn(
                                initialScale = 0.94f,
                                animationSpec = enterScaleSpec,
                        ) + fadeIn(enterFadeSpec)
                },
                exitTransition = {
                        scaleOut(
                                targetScale = 1.06f,
                                animationSpec = exitScaleSpec,
                        ) + fadeOut(exitFadeSpec)
                },
                popEnterTransition = {
                        scaleIn(
                                initialScale = 1.06f,
                                animationSpec = enterScaleSpec,
                        ) + fadeIn(enterFadeSpec)
                },
                popExitTransition = {
                        scaleOut(
                                targetScale = 0.94f,
                                animationSpec = exitScaleSpec,
                        ) + fadeOut(exitFadeSpec)
                },
        ) {
                composable<Screen.Splash>(
                        enterTransition = { fadeIn(enterFadeSpec) },
                        exitTransition = { fadeOut(exitFadeSpec) },
                ) {
                        fun goPastSplash() {
                                onSelectPrimaryPage(0)
                                navController.navigate(
                                        if (hasCompletedOnboarding) PrimaryNav
                                        else Screen.Tutorial as Any
                                ) {
                                        popUpTo(Screen.Splash) { inclusive = true }
                                        launchSingleTop = true
                                }
                        }
                        if (splashStyle == SplashStyles.LiveHandshake && onLiveHandshakeStart != null) {
                                // Live Handshake plays as an overlay above the app (RemEx-8g6n0):
                                // move on at once so the destination composes underneath and is
                                // what the portal opens into.
                                LaunchedEffect(Unit) {
                                        onLiveHandshakeStart()
                                        goPastSplash()
                                }
                        } else {
                                // A repeated onFinished is harmless: goPastSplash navigates with
                                // launchSingleTop and pops Splash inclusively.
                                SplashScreen(
                                        splashStyle = splashStyle,
                                        onFinished = { goPastSplash() },
                                )
                        }
                }

                composable<Screen.Tutorial>(
                        enterTransition = { fadeIn(enterFadeSpec) },
                        exitTransition = { fadeOut(exitFadeSpec) },
                ) {
                        TutorialScreen(
                                onFinished = {
                                        onSelectPrimaryPage(0)
                                        navController.navigate(PrimaryNav) {
                                                popUpTo(Screen.Tutorial) { inclusive = true }
                                                launchSingleTop = true
                                        }
                                },
                        )
                }

                composable<PrimaryNav> {
                        // "Start streaming" on the Desktop tab: the full-screen stream route on top
                        // of the tabs, so Back comes straight back to Desktop (RemEx-wqo7a.2).
                        val onStartStream: () -> Unit = {
                                navController.navigate(Screen.RemoteDesktop) { launchSingleTop = true }
                        }
                        if (pagerState != null) {
                                PrimaryDestinationsPager(
                                        pagerState = pagerState,
                                        homeScreenContent = homeScreenContent,
                                        desktopScreenContent = desktopScreenContent,
                                        appLauncherScreenContent = appLauncherScreenContent,
                                        controlScreenContent = controlScreenContent,
                                        desktopMode = desktopMode,
                                        onDesktopModeChange = onDesktopModeChange,
                                        controlSegment = controlSegment,
                                        onControlSegmentChange = onControlSegmentChange,
                                        onOpenDestination = onOpenDestination,
                                        onStartStream = onStartStream,
                                        onNavigateToConnection = onNavigateToConnection,
                                        modifier = Modifier.fillMaxSize(),
                                )
                        } else {
                                // No pager here (single-pane fallback) - Home is the only content
                                // on screen.
                                homeScreenContent({ onNavigateToConnection() }, onOpenDestination)
                        }
                }

                // The full Sensors canvas: its own route now that Home is the first tab. Always
                // visible while it is the current route, so it always parses telemetry here.
                composable<Screen.Dashboard> {
                        dashboardScreenContent({ onNavigateToConnection() }, true)
                }

                composable<Screen.Connection> {
                        connectionScreenContent { navController.navigate(Screen.QrScanner) }
                }

                composable<Screen.QrScanner>(
                        // QR scanner enters from bottom — modal feel
                        enterTransition = {
                                slideInVertically(enterSlideSpec) {
                                        it
                                } + fadeIn(enterFadeSpec)
                        },
                        exitTransition = {
                                slideOutVertically(exitSlideSpec) {
                                        it
                                } + fadeOut(exitFadeSpec)
                        },
                ) {
                        QrScannerScreen(
                                onScanned = { host, port, pin ->
                                        onQrScanned(host, port, pin)
                                        onSelectPrimaryPage(0)
                                        navController.navigate(PrimaryNav) {
                                                popUpTo(PrimaryNav) { inclusive = true }
                                                launchSingleTop = true
                                        }
                                },
                                onBack = { navController.popBackStack() },
                        )
                }

                composable<PairingRoute>(
                        // No navArgument block and no `?: ""` / `?: 5005` reads: the typed route
                        // carries its arguments, so the silent fallbacks RemEx-667p refused cannot
                        // be reintroduced by a missing one (RemEx-mt43).
                        enterTransition = {
                                slideInVertically(enterSlideSpec) { it } + fadeIn(enterFadeSpec)
                        },
                        exitTransition = {
                                slideOutVertically(exitSlideSpec) { it } + fadeOut(exitFadeSpec)
                        }
                ) { backStackEntry ->
                        val pairing = backStackEntry.toRoute<PairingRoute>()
                        com.clindsay94.remex.ui.screens.PairingScreen(
                                host = pairing.host,
                                port = pairing.port,
                                onPairSuccess = {
                                        navController.popBackStack()
                                        // Attempt auto connect again after successful pairing
                                        RemexClientManager.toggleConnection()
                                },
                                onCancel = {
                                        navController.popBackStack()
                                }
                        )
                }

                composable<Screen.RemoteDesktop>(
                        // Full-screen immersive — pure crossfade, no spatial motion
                        enterTransition = { fadeIn(enterFadeSpec) },
                        exitTransition = { fadeOut(exitFadeSpec) },
                        popEnterTransition = { fadeIn(enterFadeSpec) },
                        popExitTransition = { fadeOut(exitFadeSpec) },
                ) {
                        // Opened only by the Desktop tab's "Start streaming", so it starts the
                        // stream itself rather than asking for Start a second time (RemEx-wqo7a.2).
                        RemoteDesktopScreen(startStreamingOnOpen = true)
                }

                composable<Screen.Settings> {
                        SettingsScreen(
                                onReplayTutorial = {
                                        navController.navigate(Screen.Tutorial) {
                                                launchSingleTop = true
                                        }
                                },
                                onNavigateToAbout = {
                                        navController.navigate(Screen.About) {
                                                launchSingleTop = true
                                        }
                                },
                                onNavigateToQrScanner = {
                                        navController.navigate(Screen.QrScanner) {
                                                launchSingleTop = true
                                        }
                                },
                                onNavigateToShareDiagnostics = {
                                        navController.navigate(Screen.ShareDiagnostics) {
                                                launchSingleTop = true
                                        }
                                },
                        )
                }

                composable<Screen.Faq> { FaqScreen() }

                composable<Screen.ShareDiagnostics> { ShareDiagnosticsScreen() }

                composable<Screen.About> { AboutScreen() }

                composable<Screen.FileTransfer> {
                    FileTransferScreen(onNavigateToConnection = { onNavigateToConnection() })
                }

                // Routines (RemEx-pp0rt.6): list, gallery, editor, history and run detail are panes
                // of one list-detail scaffold inside this destination (routines spec 2.1).
                composable<Screen.Routines> {
                        RoutinesScreen(onNavigateToConnection = { onNavigateToConnection() })
                }
        }
}

/**
 * An icon with the "New" dot (routines spec 1.3, M14): the same scale + fade as the disconnected
 * badge, and TalkBack hears "New" as the state while it shows (R-UX-05).
 */
@Composable
private fun NewBadgedIcon(show: Boolean, imageVector: androidx.compose.ui.graphics.vector.ImageVector, contentDescription: String) {
        val newLabel = stringResource(R.string.routines_badge_new)
        BadgedBox(
                badge = {
                        androidx.compose.animation.AnimatedVisibility(
                                visible = show,
                                enter = scaleIn(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                                exit = scaleOut(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                        ) {
                                Badge()
                        }
                },
                modifier = Modifier.semantics { if (show) stateDescription = newLabel },
        ) {
                Icon(imageVector = imageVector, contentDescription = contentDescription)
        }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PrimaryDestinationsPager(
        pagerState: androidx.compose.foundation.pager.PagerState,
        homeScreenContent: HomeScreenContent,
        desktopScreenContent: DesktopScreenContent,
        appLauncherScreenContent: @Composable (() -> Unit) -> Unit,
        controlScreenContent: ControlScreenContent,
        desktopMode: DesktopMode,
        onDesktopModeChange: (DesktopMode) -> Unit,
        controlSegment: ControlSegment,
        onControlSegmentChange: (ControlSegment) -> Unit,
        onOpenDestination: (NavDestination) -> Unit,
        onStartStream: () -> Unit,
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier,
) {
        HorizontalPager(
                state = pagerState,
                modifier = modifier,
                beyondViewportPageCount = 0,
                // No tab swipe while the Desktop tab is in Trackpad mode: every drag there is meant
                // for the PC's pointer (RemEx-wqo7a.2). Keyed on the SETTLED page, so a swipe that
                // is on its way into Desktop is never cut off halfway; the tabs still change from
                // the navigation bar, which scrolls the pager programmatically.
                userScrollEnabled =
                        !pagerTrackpadOwnsSwipes(
                                settledDestination = navItems[pagerState.settledPage],
                                desktopMode = desktopMode,
                        ),
        ) { page ->
                // Expression-bodied local fun, not a 'when' statement: a 'when' whose result is
                // discarded (the old shape here) is *not* required to be exhaustive by the Kotlin
                // compiler even over a sealed type — that is exactly how a fifth navItem used to
                // compile and crash on first swipe (RemEx-740mr). Making the 'when' the expression
                // body of a function turns a missing branch into a hard compile error instead: add a
                // PrimaryDestination without wiring a page here and this function fails to compile.
                // No 'else' branch — that would silently re-admit the runtime fallback this replaces.
                @Composable
                fun renderPage(destination: PrimaryDestination): Unit =
                        when (destination) {
                                Screen.Home ->
                                        homeScreenContent({ onNavigateToConnection() }, onOpenDestination)
                                Screen.Desktop ->
                                        desktopScreenContent(
                                                desktopMode,
                                                onDesktopModeChange,
                                                onStartStream,
                                                { onNavigateToConnection() },
                                        )
                                Screen.AppLauncher ->
                                        appLauncherScreenContent { onNavigateToConnection() }
                                Screen.Control ->
                                        controlScreenContent(
                                                controlSegment,
                                                onControlSegmentChange,
                                                page == pagerState.currentPage &&
                                                        !pagerState.isScrollInProgress,
                                                { onNavigateToConnection() },
                                        )
                        }
                renderPage(navItems[page])
        }
}

/** Home tab: (onNavigateToConnection, onOpenDestination). Home opens Sensors, Desktop, Files and Routines. */
private typealias HomeScreenContent = @Composable (() -> Unit, (NavDestination) -> Unit) -> Unit

/** Desktop tab: (mode, onModeChange, onStartStream, onNavigateToConnection). */
private typealias DesktopScreenContent =
        @Composable (DesktopMode, (DesktopMode) -> Unit, () -> Unit, () -> Unit) -> Unit

/** Control tab: (segment, onSegmentChange, isVisible, onNavigateToConnection). */
private typealias ControlScreenContent =
        @Composable (ControlSegment, (ControlSegment) -> Unit, Boolean, () -> Unit) -> Unit

/**
 * Whether the trackpad, not the pager, owns horizontal drags (RemEx-wqo7a.2): only while the
 * pager has settled on the Desktop tab and that tab is in Trackpad mode. Stream mode has no
 * gestures of its own (the stream is a separate full-screen route), so it keeps tab swipes.
 */
internal fun pagerTrackpadOwnsSwipes(
        settledDestination: PrimaryDestination,
        desktopMode: DesktopMode,
): Boolean = settledDestination == Screen.Desktop && desktopMode == DesktopMode.Trackpad

// ─── Previews ─────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3AdaptiveNavigationSuiteApi::class)
@Preview(showBackground = true)
@Composable
private fun AppNavigationPreview() {
        RemExTheme {
                AppNavigationContent(
                        hasCompletedOnboarding = true,
                        isConnected = true,
                        splashStyle = SplashStyles.Default,
                        onQrScanned = { _, _, _ -> },
                        dashboardScreenContent = { _, _ -> Box(Modifier.fillMaxSize()) },
                        homeScreenContent = { _, _ -> Box(Modifier.fillMaxSize()) },
                        desktopScreenContent = { _, _, _, _ -> Box(Modifier.fillMaxSize()) },
                        appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                        controlScreenContent = { _, _, _, _ -> Box(Modifier.fillMaxSize()) },
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
                        isConnected = false,
                        splashStyle = SplashStyles.Default,
                        onQrScanned = { _, _, _ -> },
                        dashboardScreenContent = { _, _ -> Box(Modifier.fillMaxSize()) },
                        homeScreenContent = { _, _ -> Box(Modifier.fillMaxSize()) },
                        desktopScreenContent = { _, _, _, _ -> Box(Modifier.fillMaxSize()) },
                        appLauncherScreenContent = { Box(Modifier.fillMaxSize()) },
                        controlScreenContent = { _, _, _, _ -> Box(Modifier.fillMaxSize()) },
                        connectionScreenContent = { Box(Modifier.fillMaxSize()) },
                )
        }
}
