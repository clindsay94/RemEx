using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;
using Remex.Desktop.Services.Security;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Desktop;

public partial class App : Application
{
    private TrayFlyoutWindow? _flyout;
    /// <summary>The two tray-menu items whose text or enablement follows phone presence.</summary>
    private NativeMenuItem? _statusHeaderItem;
    private NativeMenuItem? _remoteDesktopItem;
    public static IServiceProvider Services { get; private set; } = null!;
    public static bool IsShuttingDown { get; set; }

    public static int? OverrideHostPort { get; set; }
    public static Action<IServiceCollection>? RegisterPlatformServices { get; set; }
    public static Func<Task>? StopEmbeddedHostAsync { get; set; }
    public static string? EmbeddedHostInstanceId { get; set; }

    /// <summary>
    /// Service provider of the in-process embedded host (desktop only). Used by the
    /// Avalonia UI to subscribe to host-side events (e.g. pairing PIN display).
    /// </summary>
    public static IServiceProvider? EmbeddedHostServices { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var collection = new ServiceCollection();

        collection.AddSingleton<ILauncherStorageService, LauncherStorageService>();
        collection.AddSingleton<IIconExtractionService, IconExtractionService>();
        collection.AddSingleton<DashboardLayoutService>();
        collection.AddSingleton<RemexSavefileService>(sp =>
        {
            // Host-side dashboard storage resolves the same ProgramData path regardless of
            // whether it happens to be registered in this container.
            var hostProfileStorage = sp.GetService<Remex.Core.Services.IDashboardProfileStorageService>()
                ?? new Remex.Core.Services.DashboardProfileStorageService();

            return new RemexSavefileService(
                sp.GetRequiredService<DashboardLayoutService>(),
                sp.GetRequiredService<ILauncherStorageService>(),
                new FileTransferRootSettingsService(),
                hostProfileStorage);
        });
        collection.AddSingleton<ThemeService>();
        collection.AddSingleton<HardwareThemeService>();
        collection.AddSingleton<UpdateCheckService>();
        collection.AddSingleton<IMdnsDiscoveryService, MdnsDiscoveryService>();
        collection.AddSingleton<PinnedCertStore>();
        collection.AddSingleton<IPairingPinQueryService, IpcPairingPinQueryService>();

        collection.AddSingleton<ConnectionViewModel>();
        collection.AddTransient<AppLauncherViewModel>();
        collection.AddTransient<AddProgramViewModel>();
        collection.AddTransient<TaskManagerViewModel>();
        collection.AddSingleton<HomeViewModel>();
        collection.AddSingleton<ShellViewModel>();
        // Singleton: the tray flyout window is created once and reused, and its tile list is
        // rebuilt in place as phone presence changes rather than per show.
        collection.AddSingleton<TrayFlyoutViewModel>();
        collection.AddTransient<DiagnosticLogsViewModel>();

        RegisterPlatformServices?.Invoke(collection);

        var configBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        CommandModeContext.ConfigureServices(collection, configBuilder.Build());

        Services = collection.BuildServiceProvider();
        CommandModeContext.StartListener(Services);

        // Apply the saved theme before the window opens, so SolarFlare (or any non-default) users do
        // not see a dark-glass flash.
        //
        // "SYNCHRONOUSLY" IS WHAT THIS SAID AND IT WAS NOT TRUE. ThemeService.ApplyCustomization
        // posts its whole body to the dispatcher, and MainWindow.Show happens later from the
        // fire-and-forget InitializeAppAsync. The ordering that actually prevents the flash is
        // priority, not synchrony: a DispatcherPriority.Default job runs ahead of Render, so the
        // palette lands before the first frame. Worth stating accurately because the flash is now
        // one shared DARK fallback under every preset (RemEx-07jij), so lowering that priority would
        // regress a light preset with nothing to point at the cause.
        ApplyThemeBeforeWindowShown();

        // Install the notification surfaces BEFORE app init, because app init is one of the things
        // that can fail and needs to announce it. The in-app toast host is not set here — ShellView
        // installs it once there is a window to draw the toast over.
        //
        // The probe re-reads the lifetime on every call rather than capturing a window: MainWindow
        // is created lazily and can be replaced, and a captured reference would go on reporting the
        // visibility of a window that is no longer the one on screen.
        NotificationService.Instance.TrayBalloon = new TrayBalloonSink();
        NotificationService.Instance.WindowVisibleProbe = () =>
            ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime
            && lifetime.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

        BuildTrayMenu();
        WireTrayTooltipToPhonePresence();

        _ = InitializeAppAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyThemeBeforeWindowShown()
    {
        try
        {
            // THE SERVICE'S OWN PATH, NOT A SECOND HAND-BUILT ONE (RemEx-mzbn). This read happens
            // before the window is shown, so it predates the service — but building the path here
            // meant it did not honour RemexDataPaths' test redirect, and a file with two resolvers
            // where only one is redirected is what RemEx-ln0k existed to remove.
            // Fully qualified: `Services` binds to App.Services, the IServiceProvider, not the namespace.
            var filePath = Remex.Desktop.Services.DashboardLayoutService.DefaultFilePath;

            // READ AND MIGRATED THROUGH THE SERVICE'S OWN PATH, not a hand-built deserialize
            // (RemEx-dbkzy). This method used to build its own JsonSerializerOptions and apply the
            // RAW record — so for a profile written before the seed engine it painted the theme the
            // migration exists to replace. A 2.4 Cyber-NOC user's window opened violet and turned
            // cyan once LoadAsync landed, or opened cyan, depending on how this file read raced the
            // window: the whole point of this method is which palette the window OPENS on, which is
            // the phrase the bead's acceptance uses.
            //
            // The same argument as RemEx-mzbn one line up, one level deeper: a second reader of a
            // file is a second opinion about what that file means, and the two drift.
            var profile = Remex.Desktop.Services.DashboardLayoutService.ReadAndMigrate(filePath, out _);

            if (profile?.Customization == null) return;

            var themeService = Services.GetRequiredService<ThemeService>();
            
            // Apply the full customization object immediately.
            // This ensures colors, corner radii, and base theme are set BEFORE the window is shown.
            themeService.ApplyCustomization(profile.Customization);
        }
        catch (Exception ex)
        {
            // Falls back to the default dark theme already declared in App.axaml, which is the right
            // behaviour - but the user sees a theme they did not choose, with nothing to explain it.
            // "my theme keeps resetting" is unanswerable without this line.
            InMemoryLogSink.Append(LogLevel.Warning, "App",
                "Could not apply the saved theme customization; falling back to the default dark theme", ex);
        }
    }

    private async Task InitializeAppAsync()
    {
        try
        {
            var layoutService = Services.GetRequiredService<DashboardLayoutService>();
            var savefileService = Services.GetRequiredService<RemexSavefileService>();
            var profile = await layoutService.LoadAsync();

            // Auto-check for a newer GitHub release on startup unless the user opted out. Fire-and-forget
            // so a slow/absent network never delays the window; the About page reads the cached result.
            if (profile == null || profile.CheckForUpdatesAutomatically)
            {
                _ = Services.GetService<UpdateCheckService>()?.CheckAsync();
            }

            // Silent rolling auto-snapshot: restart the debounce timer every time the dashboard
            // profile is written to disk (canvas moves, settings changes, theme swaps, etc.).
            layoutService.ProfileSaved += () => savefileService.NotifyStateChanged();

            // First-run restore: dashboard_layout.json was missing on load (fresh install or a
            // wiped %LocalAppData%\Remex) but a rolling auto-snapshot exists from a previous
            // install. Offer to restore from it once the main window exists.
            var profileFileWasMissing = layoutService.ProfileFileMissingOnLoad;

            if (profile != null && !string.IsNullOrWhiteSpace(profile.Language))
            {
                try
                {
                    var culture = new System.Globalization.CultureInfo(profile.Language);
                    Remex.Desktop.Localization.Strings.Culture = culture;
                    System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                }
                catch (System.Globalization.CultureNotFoundException ex)
                {
                    // Ignoring it and using the default culture is correct, but the user picked a
                    // language and the app came up in another one. That is a support case nobody can
                    // answer from a log that never mentions it - and the bad code is in the message,
                    // which is the one fact needed to fix it.
                    InMemoryLogSink.Append(LogLevel.Warning, "App",
                        $"Settings hold an unrecognised language code '{profile.Language}'; using the default culture", ex);
                }
            }

            var viewModel = Services.GetRequiredService<ShellViewModel>();
            viewModel.NavigateToHome();

            // --view <Name> (RemEx-8q7de): opens a specific view at startup for the palette sweep
            // script, which launches the host once per cell x view rather than sending keystrokes
            // through a running one (SendKeys navigation is banned — no InvokePattern on the nav
            // list items for UI Automation to click instead). An unrecognised name just stays on
            // Home with a log line; it is not a reason to fail startup.
            var launchArgs = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args;
            var requestedView = StartupViewArgument.ExtractRequestedViewName(launchArgs);
            if (requestedView != null && !StartupViewArgument.TryApply(launchArgs, viewModel))
            {
                InMemoryLogSink.Append(LogLevel.Warning, "App",
                    $"--view '{requestedView}' is not a recognised view name; staying on Home", null);
            }

            if (OverrideHostPort.HasValue)
            {
                viewModel.Connection.HostAddress = $"wss://localhost:{OverrideHostPort.Value}{Remex.Core.RemexConstants.WebSocketPath}";
            }
            else if (profile != null && !string.IsNullOrWhiteSpace(profile.HostAddress))
            {
                viewModel.Connection.HostAddress = profile.HostAddress;
            }

            // Wire the embedded host's pairing service so the desktop UI can display
            // the PIN that the user's phone is asking for.
            if (EmbeddedHostServices?.GetService(typeof(Remex.Core.Services.Security.IPairingService))
                is Remex.Core.Services.Security.IPairingService pairingService)
            {
                viewModel.Connection.AttachEmbeddedPairingService(pairingService);
            }

            // Wire the embedded host's file-trust service so this (serving) PC raises a consent dialog when
            // a paired device asks for full-device browse or an incoming file push (plan §2). Mirrors the
            // pairing-service event pattern above.
            if (EmbeddedHostServices?.GetService(typeof(Remex.Core.Services.FileTransfer.IFileTrustService))
                is Remex.Core.Services.FileTransfer.IFileTrustService fileTrustService)
            {
                fileTrustService.ConsentRequested += prompt =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => ShowFileConsentDialogAsync(fileTrustService, prompt)
                            .FireAndForget("show the file-consent dialog"));
            }

            if (OperatingSystem.IsWindows() && !CommandModeContext.IsServerMode)
            {
                var pairingPinQueryService = Services.GetService<IPairingPinQueryService>(); // optional service
                if (pairingPinQueryService != null)
                {
                    viewModel.Connection.AttachStandalonePairingPinQueryService(pairingPinQueryService);
                    await viewModel.Connection.RefreshStandalonePairingPinAsync();
                    viewModel.Connection.StartStandalonePairingPinPolling();
                }
            }

            _ = viewModel.Connection.AutoConnectAsync();

            // P8-G: keep tray icon tooltip in sync with live sensor readings
            viewModel.Connection.TelemetryReceived += telemetry =>
            {
                viewModel.UpdateTrayStatus(telemetry);
                UpdateTrayTooltip(viewModel.TrayStatusSummary);
            };

            // Update tooltip once on connect/disconnect state change
            viewModel.Connection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
                {
                    viewModel.UpdateTrayStatus(viewModel.Connection.Telemetry);
                    UpdateTrayTooltip(viewModel.TrayStatusSummary);
                }
            };

            // Rebuilding is the simplest correct response to a language switch - every header is
            // read from LocalizationService at build time, so there is nothing to re-address.
            LocalizationService.Instance.PropertyChanged += (s, e) => BuildTrayMenu();

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    if (desktop.MainWindow == null)
                    {
                        desktop.MainWindow = new MainWindow { DataContext = viewModel };
                    }
                    
                    bool startMinimized = desktop.Args != null && Array.Exists(desktop.Args, arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
                    if (!startMinimized)
                    {
                        desktop.MainWindow.Show();
                    }
                }
                else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
                {
                    singleViewPlatform.MainView = new ShellView { DataContext = viewModel };
                }
            });

            // First-run restore prompt (desktop lifetimes only): dashboard_layout.json was
            // missing on load — not merely corrupt — and a rolling auto-snapshot from a previous
            // install exists. Offer to restore everything from it, no restart required.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime restoreDesktop
                && profileFileWasMissing
                && savefileService.TryGetLatestSnapshotPath() is { } snapshotPath
                && restoreDesktop.MainWindow is { } mainWindow)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!mainWindow.IsVisible)
                    {
                        mainWindow.Show();
                    }

                    var shouldRestore = await MaterialDialogs.RestoreAsync(mainWindow, snapshotPath);
                    if (shouldRestore)
                    {
                        await using var stream = File.OpenRead(snapshotPath);
                        await savefileService.ImportAsync(stream);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            // ROUTED TO THE IN-APP LOG, NOT JUST Debug.WriteLine (RemEx-43ha). Debug output exists
            // only in a debugger, so on a user's machine this failure left NO trace anywhere - and
            // it is the whole of app initialization, so it is the single most valuable line a
            // support case could have. The in-app sink is what the diagnostics export reads, which
            // is how it reaches someone who can act on it.
            InMemoryLogSink.Append(LogLevel.Error, "App", "Failed to initialize app", ex);
            System.Diagnostics.Debug.WriteLine($"Failed to initialize app: {ex.Message}");

            // AND SHOW IT. The log line above is what a support case reads; this is what the user
            // in front of the machine gets. Without it the app simply comes up wrong and the only
            // person who could say so has no idea anything failed (RemEx-5wc2).
            NotificationService.Instance.Notify(
                NotificationImportance.Problem,
                LocalizationService.Instance["Notification_StartupFailed_Title"],
                LocalizationService.Instance["Notification_StartupFailed_Message"]);
        }
    }

    /// <summary>
    /// Shows the file-sharing consent dialog for a pending prompt and relays the user's decision back to the
    /// host's <see cref="Remex.Core.Services.FileTransfer.IFileTrustService"/>. Dismissing the window (or a
    /// missing decision) resolves as a clean deny; the service's own 60-second timeout is the backstop.
    /// </summary>
    /// <remarks>
    /// <c>async Task</c> rather than <c>async void</c>: an exception escaping an async void
    /// method is raised on the synchronization context and takes the process down, rather than being
    /// observable by the caller. The body is already try/catch-wrapped so this is a
    /// belt-and-braces change, but the shape is what the convention asks for (RemEx-ajk3).
    /// </remarks>
    private async Task ShowFileConsentDialogAsync(
        Remex.Core.Services.FileTransfer.IFileTrustService service,
        Remex.Core.Services.FileTransfer.FileConsentPrompt prompt)
    {
        try
        {
            var vm = new FileConsentDialogViewModel(prompt.Request);

            Remex.Core.Services.FileTransfer.FileConsentDecision decision;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is { } owner)
            {
                // SURFACE THE OWNER FIRST, OR THERE IS NO DIALOG (RemEx-6bfyt). Avalonia's ShowDialog
                // throws "Cannot show window with non-visible parent", and not-visible is RemEx's
                // NORMAL state rather than an edge case: the logon task starts it '--minimized'
                // (scripts/autostart-remex.ps1) so MainWindow is constructed and never shown, and
                // hide-to-tray returns it there. The throw lands in the catch below, which denies
                // fail-closed with no reason code - byte-identical to the user tapping Deny. So the
                // prompt nobody could see becomes a refusal nobody can explain.
                //
                // This was latent while the desktop route only served phones too old to render the
                // prompt. Routing full browse here by kind made it the ONLY path for that grant, so
                // the guard stops being optional.
                //
                // THROUGH THE HELPER, NOT A LOCAL Show()/Activate() PAIR (RemEx-b3bi). All three of
                // its steps are load-bearing and none implies another - in particular a MINIMIZED
                // window reports IsVisible true, so an `if (!owner.IsVisible) Show()` guard skips it
                // and Activate alone leaves it in the taskbar. That is a reachable state (open from
                // the tray, then minimize) and it lands in exactly the same silent-deny as the one
                // above. This was written as a local two-step first; the helper is the whole reason
                // that class of bug has one place to be fixed.
                BringMainWindowToFront();

                decision = await MaterialDialogs.FileConsentAsync(owner, vm);
            }
            else
            {
                decision = await MaterialDialogs.FileConsentAsync(owner: null, vm);
            }

            service.ResolveConsent(prompt.Request.ConsentId, decision.Granted, decision.Remember);
        }
        catch (Exception ex)
        {
            // Never let a UI failure hang the host's pending consent — deny cleanly instead.
            //
            // AND SAY SO IN THE LOG. Denying is correct and fail-closed, but it is indistinguishable
            // from the user having chosen to deny, so without this line a transfer is refused and
            // nobody can tell why. That is the shape of report this export exists to answer.
            InMemoryLogSink.Append(LogLevel.Error, "App",
                "File consent dialog failed; denying the request fail-closed", ex);
            System.Diagnostics.Debug.WriteLine($"File consent dialog failed: {ex.Message}");
            service.ResolveConsent(prompt.Request.ConsentId, granted: false, remember: false);

            // Denying is correct, but from the phone's end it is indistinguishable from the user
            // having said no — and from this end it is indistinguishable from nothing happening at
            // all. Announce it so the person at the PC knows a request arrived and was refused for
            // a reason they did not choose.
            NotificationService.Instance.Notify(
                NotificationImportance.Problem,
                LocalizationService.Instance["Notification_ConsentFailed_Title"],
                LocalizationService.Instance["Notification_ConsentFailed_Message"]);
        }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        // Interacting with the tray icon deactivates the flyout, and a transient flyout hides on
        // deactivation - so without this, clicking the icon that owns the window is indistinguishable
        // from clicking away from it. The flag is cleared again in ShowAtTray, so arming it for a
        // deactivation that never arrives cannot swallow the next genuine click-away.
        //
        // NOTE this covers the LEFT click only. Avalonia raises Clicked for the left button; a
        // right-click opens the native menu without routing through here, so the "right-click the
        // tray icon and the flyout survives" behaviour is unverified and has no hook to hang on -
        // TrayIcon exposes no menu-opening event. See Task 7's verification list.
        _flyout?.SuppressNextDeactivate();

        ToggleLiveGlance();
    }

    private void ToggleLiveGlance()
    {
        if (_flyout == null)
        {
            var vm = Services.GetRequiredService<TrayFlyoutViewModel>();
            _flyout = new TrayFlyoutWindow { DataContext = vm };

            // The flyout is a visible Window, so it can host a modal dialog. Wired here rather than
            // in the view model because a view model owns no UI (RemEx-07jx). ConfirmationDialogHost
            // returns false when the owner has no visible window, so a destructive action declines
            // rather than proceeding unconfirmed if that ever stops being true.
            vm.OnConfirmationRequested = ConfirmationDialogHost.For(_flyout);
        }

        // The flyout refreshes itself in ShowAtTray now - HomeViewModel is no longer its data
        // context, so refreshing that here would have refreshed something nothing was binding to.

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
        }
        else
        {
            _flyout.ShowAtTray();
        }
    }

    /// <summary>
    /// Keeps the tray tooltip saying what the shell's dot says (RemEx-3s4v).
    /// </summary>
    /// <remarks>
    /// THE TRAY IS THE STATUS SURFACE FOR MOST OF THIS APP'S LIFE, because it closes to the tray by
    /// default — so a tooltip that read "RemEx Desktop - Remote Execution" whether or not a phone was
    /// attached was answering the one question it is well placed to answer with a constant.
    /// Subscribed rather than bound: TrayIcon is a NativeMenu-adjacent object declared in App.axaml,
    /// not a control in a visual tree, so there is no data context to bind through.
    /// </remarks>
    private void WireTrayTooltipToPhonePresence()
    {
        if (TrayIcon.GetIcons(this) is not { Count: > 0 } icons) return;

        var icon = icons[0];
        var product = LocalizationService.Instance["Tray_TooltipDefault"];

        void Apply() => icon.ToolTipText =
            TrayTooltip.Compose(product, PhonePresenceMonitor.Instance.PresenceText);

        // One 3-second poll drives both surfaces. The menu's status header is the same sentence as
        // the tooltip, so refreshing them from separate subscriptions would only create a window in
        // which they disagree.
        PhonePresenceMonitor.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PhonePresenceMonitor.PresenceText)) Apply();
            RefreshTrayMenu();
        };

        Apply();
    }

    private void OnShowMainWindow(object? sender, EventArgs e) => BringMainWindowToFront();

    /// <summary>
    /// Creates the main window if it is gone, then shows, activates and un-minimizes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE COPY, BECAUSE THREE AGREED UNTIL THEY DID NOT (RemEx-b3bi). The tray menu, the tray
    /// flyout's "open" button and a pressed balloon each carried their own version of this. They
    /// matched, which is exactly what makes the drift invisible: the failure mode is one entry point
    /// quietly not restoring a MINIMIZED window while the other two do, and nobody notices until they
    /// happen to use that entry point with the window in that state.
    /// </para>
    /// <para>
    /// ALL THREE STEPS ARE LOAD-BEARING and none is implied by another. Show brings back a window
    /// hidden by close-to-tray, Activate raises and focuses it above whatever the user was doing, and
    /// WindowState resolves Minimized — a minimized window is already "shown", so Show alone leaves
    /// it in the taskbar and the click appears to do nothing.
    /// </para>
    /// </remarks>
    public static void BringMainWindowToFront()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.MainWindow ??= new MainWindow { DataContext = Services.GetRequiredService<ShellViewModel>() };
        desktop.MainWindow.Show();
        desktop.MainWindow.Activate();
        desktop.MainWindow.WindowState = WindowState.Normal;
    }

    private void OnExitApp(object? sender, EventArgs e) => ShutdownApplication();

    private bool _shutdownInitiated;

    /// <summary>
    /// Tears the entire application down — the single source of truth for "Exit",
    /// used by the tray menu, the in-app Exit button, and the close-to-exit window
    /// behavior. Stops the embedded host gracefully (time-boxed), releases the
    /// service mutex, then force-terminates the process via <see cref="Environment.Exit"/>
    /// so that no lingering foreground thread (Kestrel, the mDNS responder, the
    /// network listener, or hardware-monitor interop) can keep the process alive.
    /// </summary>
    public void ShutdownApplication()
    {
        if (_shutdownInitiated) return;
        _shutdownInitiated = true;
        IsShuttingDown = true;

        // Hide the UI immediately so exit feels instant while the host stops.
        try { _flyout?.Close(); } catch { /* best-effort */ }
        _flyout = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { desktop.MainWindow?.Hide(); } catch { /* best-effort */ }
        }

        if (Services?.GetService<ShellViewModel>() is IDisposable disposableVm) // optional service
        {
            try { disposableVm.Dispose(); } catch { /* best-effort */ }
        }

        // Graceful, time-boxed host shutdown on a background thread, then a hard exit.
        _ = Task.Run(async () =>
        {
            try
            {
                if (StopEmbeddedHostAsync is { } stopHost)
                    await stopHost().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* best-effort — we force-exit regardless */ }

            try { CommandModeContext.Cleanup(); } catch { /* best-effort */ }

            Environment.Exit(0);
        });
    }

    /// <summary>
    /// Convenience entry point for callers that only hold a reference to
    /// <see cref="Application.Current"/> (e.g. windows and view-models).
    /// </summary>
    public static void RequestApplicationShutdown()
    {
        if (Current is App app)
            app.ShutdownApplication();
    }

    public static Action? RequestPlatformWidgetUpdate { get; set; }
    private void TriggerPlatformWidgetUpdate() => RequestPlatformWidgetUpdate?.Invoke();

    // ═══════════════ P8-G: Tray Tooltip ═══════════════

    private void UpdateTrayTooltip(string text)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons?.FirstOrDefault() is { } icon)
            icon.ToolTipText = text;
    }

    // ═══════════════ Tray context menu ═══════════════

    /// <summary>
    /// Builds the tray context menu in code.
    /// </summary>
    /// <remarks>
    /// IN CODE RATHER THAN IN App.axaml BECAUSE THE ITEMS ARE NOT STATIC. The declared menu could
    /// only be updated by index — <c>menu.Items[2] as NativeMenuItem</c> — which silently did
    /// nothing the moment anyone inserted an item, and could not enable or disable anything at all.
    /// Holding the two live items in fields removes both problems.
    /// <para>
    /// The theme toggle that used to live here is gone rather than ported. It only ever swapped
    /// BaseDarkGlass and SolarFlare, which is two of the four themes, and Settings already offers
    /// all four.
    /// </para>
    /// </remarks>
    private void BuildTrayMenu()
    {
        var strings = LocalizationService.Instance;

        _statusHeaderItem = new NativeMenuItem { IsEnabled = false };
        _remoteDesktopItem = new NativeMenuItem { Header = strings["Palette_RemoteDesktop"] };
        _remoteDesktopItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToRemoteDesktop();
        };

        var lockItem = new NativeMenuItem { Header = strings["Palette_LockPc"] };

        // NOT `async (_, _) => await ...`. An exception escaping an async void handler is raised on
        // the synchronization context and takes the process down (RemEx-ajk3, and the note on
        // ShowFileConsentDialogAsync above). LockAsync reaches string.Format over a localized format
        // string, which is the failure PhonePresenceMonitor guards its own first Refresh against: a
        // translation that gains a placeholder throws FormatException in one language only.
        lockItem.Click += (_, _) =>
            Services.GetRequiredService<ShellViewModel>().Connection.LockAsync()
                .FireAndForget("lock the PC from the tray menu");

        var transfersItem = new NativeMenuItem { Header = strings["Tray_Menu_OpenTransfers"] };
        transfersItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToFileTransfer();
        };

        var pairItem = new NativeMenuItem { Header = strings["Tray_Menu_PairDevice"] };
        pairItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToSettings();
        };

        var showItem = new NativeMenuItem { Header = strings["Tray_ShowMainWindow"] };
        showItem.Click += OnShowMainWindow;

        var settingsItem = new NativeMenuItem { Header = strings["Palette_Settings"] };
        settingsItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToSettings();
        };

        var exitItem = new NativeMenuItem { Header = strings["Tray_Exit"] };
        exitItem.Click += OnExitApp;

        var menu = new NativeMenu();
        menu.Items.Add(_statusHeaderItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(lockItem);
        menu.Items.Add(_remoteDesktopItem);
        menu.Items.Add(transfersItem);
        menu.Items.Add(pairItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        if (TrayIcon.GetIcons(this)?.FirstOrDefault() is { } icon)
            icon.Menu = menu;

        RefreshTrayMenu();
    }

    /// <summary>Republishes the state-dependent parts: the status header and Remote's enablement.</summary>
    /// <remarks>
    /// Reuses <see cref="TrayTileRules.IsRemoteDesktopEnabled"/> rather than restating
    /// <c>IsPhoneAttached</c> — one rule, two surfaces, no chance of them disagreeing. That is the
    /// mistake the flyout's presence dot made before RemEx-7zzw.
    /// </remarks>
    private void RefreshTrayMenu()
    {
        var presence = PhonePresenceMonitor.Instance;

        if (_statusHeaderItem is not null)
            _statusHeaderItem.Header = presence.PresenceText;

        if (_remoteDesktopItem is not null)
            _remoteDesktopItem.IsEnabled = TrayTileRules.IsRemoteDesktopEnabled(presence.IsPhoneAttached);
    }
}
