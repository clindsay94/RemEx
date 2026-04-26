using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.Services;
using Remex.Core.Guards;

namespace Remex.Client.ViewModels;

/// <summary>
/// Top-level ViewModel that owns navigation between Home, Canvas, and Settings.
/// Shared resources (ConnectionViewModel, DashboardLayoutService) live here and
/// are injected into child ViewModels.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ThemeService _themeService;
    private readonly IImmersiveModeService? _immersiveMode;
    private readonly IServiceProvider _services;
    private readonly Action<Remex.Core.Models.CustomizationSettings> _onCustomizationApplied;
    private readonly PropertyChangedEventHandler _onConnectionChanged;
    private static readonly Random _rng = new();
    private bool _welcomeSplashStarted;

    /// <summary>Exposed for child VMs that need to read persisted settings (e.g. stream quality/FPS).</summary>
    public DashboardLayoutService LayoutService => _layoutService;

    /// <summary>Helper property for Android-specific UI logic.</summary>
    public bool IsAndroid => OperatingSystem.IsAndroid();

    /// <summary>Helper property for Desktop-specific UI logic.</summary>
    public bool IsDesktop => !OperatingSystem.IsAndroid();
    public double CompactPaneLength => OperatingSystem.IsAndroid() ? 0 : 64;
    public double OpenPaneLength => OperatingSystem.IsAndroid() ? 0 : 220;


    /// <summary>Shared connection logic — injected into child VMs that need it.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>
    /// The currently active child ViewModel, bound to a TransitioningContentControl.
    /// </summary>
    [ObservableProperty]
    private ObservableObject? _currentView;

    /// <summary>
    /// When true, the shell title bar / navigation chrome is hidden for immersive fullscreen.
    /// </summary>
    [ObservableProperty]
    private bool _isShellChromeHidden;

    /// <summary>Whether the side navigation drawer is expanded.</summary>
    [ObservableProperty]
    private bool _isDrawerOpen;

    /// <summary>Whether the settings overlay panel is open.</summary>
    [ObservableProperty]
    private bool _isSettingsPanelOpen;

    /// <summary>Index of the active navigation item (for highlight).</summary>
    [ObservableProperty]
    private int _activeNavIndex;

    /// <summary>Direction of the page slide transition. 1 = forward, -1 = backward.</summary>
    [ObservableProperty]
    private int _transitionDirection = 1;

    /// <summary>Random transition type index (0-3) for variety.</summary>
    [ObservableProperty]
    private int _transitionType;

    /// <summary>Controls the startup welcome splash overlay visibility.</summary>
    [ObservableProperty]
    private bool _showWelcomeSplash = true;

    /// <summary>Controls the first-run tutorial overlay visibility.</summary>
    [ObservableProperty]
    private bool _showTutorialOverlay;

    /// <summary>Current page index of the tutorial (0-based).</summary>
    [ObservableProperty]
    private int _tutorialPageIndex;

    /// <summary>Total number of tutorial pages.</summary>
    public int TutorialPageCount => 10;

    /// <summary>User preference to not show tutorial again.</summary>
    [ObservableProperty]
    private bool _dontShowTutorialAgain;

    /// <summary>
    /// When true, a dismissible banner is shown at the top of the content area informing
    /// the user that a host connection is required for the current feature.
    /// </summary>
    [ObservableProperty]
    private bool _showConnectionBanner;

    /// <summary>Message shown in the connection banner.</summary>
    [ObservableProperty]
    private string _connectionBannerMessage = string.Empty;

    /// <summary>
    /// When true, all infinite/decorative animations are suppressed for users
    /// who prefer reduced motion.  Persisted in the layout profile.
    /// </summary>
    [ObservableProperty]
    private bool _isReducedMotion;

    partial void OnIsReducedMotionChanged(bool value)
    {
        var current = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
        var updated = current with { IsReducedMotion = value };
        _layoutService.RequestSave(updated);
    }

    // ═══════════════ Child VMs (lazy-created, cached) ═══════════════

    private HomeViewModel? _homeViewModel;
    private CanvasDashboardViewModel? _canvasViewModel;
    private SettingsViewModel? _settingsViewModel;
    private RemoteViewModel? _remoteViewModel;
    private AppLauncherViewModel? _appLauncherViewModel;
    private CustomizationViewModel? _customizationViewModel;
    private RemoteDesktopViewModel? _remoteDesktopViewModel;
    private TaskManagerViewModel? _taskManagerViewModel;
    private AboutViewModel? _aboutViewModel;

    [ObservableProperty]
    private Remex.Core.Models.CustomizationSettings _customization = new();

    public ShellViewModel(DashboardLayoutService layoutService, ThemeService themeService, ConnectionViewModel connectionViewModel, IServiceProvider services, IImmersiveModeService? immersiveMode = null)
    {
        _layoutService = Guard.NotNull(layoutService);
        _themeService = Guard.NotNull(themeService);
        Connection = Guard.NotNull(connectionViewModel);
        _services = Guard.NotNull(services);
        _immersiveMode = immersiveMode; // Intentionally optional

        _onCustomizationApplied = settings => Customization = settings;
        _themeService.CustomizationApplied += _onCustomizationApplied;
        if (_layoutService.CurrentProfile?.Customization != null)
        {
            Customization = _layoutService.CurrentProfile.Customization;
        }

        // Load reduced-motion preference
        if (_layoutService.CurrentProfile is { } profile)
            _isReducedMotion = profile.IsReducedMotion;

        // Auto-hide the connection banner when the host connects
        _onConnectionChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsConnected) && Connection.IsConnected)
                ShowConnectionBanner = false;
        };
        Connection.PropertyChanged += _onConnectionChanged;

        // Initialize background/shared VMs
        _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this);
        _ = _canvasViewModel.InitializeAsync();
    }

    public void Dispose()
    {
        _themeService.CustomizationApplied -= _onCustomizationApplied;
        Connection.PropertyChanged -= _onConnectionChanged;

        // Dispose child ViewModels
        _homeViewModel?.Dispose();
        _canvasViewModel?.Dispose();
        _settingsViewModel?.Dispose();
        _remoteViewModel?.Dispose();
        _appLauncherViewModel?.Dispose();
        _customizationViewModel?.Dispose();
        _remoteDesktopViewModel?.Dispose();
        _taskManagerViewModel?.Dispose();
        _aboutViewModel?.Dispose();

        // Dispose shared connection ViewModel
        Connection.Dispose();
    }

    public void BeginWelcomeSplash()
    {
        if (_welcomeSplashStarted)
            return;

        _welcomeSplashStarted = true;
        _ = DismissWelcomeSplashAsync();
    }

    private async Task DismissWelcomeSplashAsync()
    {
        // Safety fallback — normally BootSequenceControl fires SequenceCompleted
        await Task.Delay(6000);
        Dispatcher.UIThread.Post(() =>
        {
            if (ShowWelcomeSplash)
                OnBootSequenceCompleted();
        });
    }

    public void OnBootSequenceCompleted()
    {
        ShowWelcomeSplash = false;
        // Show tutorial on first run after the splash fades
        if (!(_layoutService.CurrentProfile?.HasCompletedTutorial ?? false))
        {
            TutorialPageIndex = 0;
            ShowTutorialOverlay = true;
        }
    }

    [RelayCommand]
    public void TutorialNext()
    {
        int nextIndex = TutorialPageIndex + 1;

        // Skip OS-specific pages
        if (OperatingSystem.IsWindows())
        {
            if (nextIndex == 3) nextIndex = 4; // Skip Linux Service page
        }
        else if (OperatingSystem.IsLinux())
        {
            if (nextIndex == 2) nextIndex = 3; // Skip Windows Service page
            if (nextIndex == 4) nextIndex = 5; // Skip HWInfo page (Windows only)
        }

        if (nextIndex < TutorialPageCount)
            TutorialPageIndex = nextIndex;
    }

    [RelayCommand]
    public void TutorialPrevious()
    {
        int prevIndex = TutorialPageIndex - 1;

        // Skip OS-specific pages
        if (OperatingSystem.IsWindows())
        {
            if (prevIndex == 3) prevIndex = 2; // Skip Linux Service page
        }
        else if (OperatingSystem.IsLinux())
        {
            if (prevIndex == 4) prevIndex = 3; // Skip HWInfo page (Windows only)
            if (prevIndex == 2) prevIndex = 1; // Skip Windows Service page
        }

        if (prevIndex >= 0)
            TutorialPageIndex = prevIndex;
    }

    [RelayCommand]
    public void TutorialSkip() => CompleteTutorial();

    [RelayCommand]
    public void TutorialFinish() => CompleteTutorial();

    [RelayCommand]
    public void ReplayTutorial()
    {
        TutorialPageIndex = 0;
        ShowTutorialOverlay = true;
    }

    [RelayCommand]
    public void DismissConnectionBanner() => ShowConnectionBanner = false;

    /// <summary>
    /// Shows the connection banner if the host is not connected.
    /// Returns true if disconnected (caller may still navigate for preview).
    /// </summary>
    private void NotifyIfDisconnected(string featureName)
    {
        if (!Connection.IsConnected)
        {
            ConnectionBannerMessage = string.Format(LocalizationService.Instance["Status_FeatureRequiresConnection"], featureName);
            ShowConnectionBanner = true;
        }
    }

    private void CompleteTutorial()
    {
        ShowTutorialOverlay = false;
        // Persist that the user has completed the tutorial (or chosen not to see it again)
        var current = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
        var updated = current with { HasCompletedTutorial = true };
        _layoutService.RequestSave(updated);
        // Reset the checkbox for next time (if user manually replays tutorial)
        DontShowTutorialAgain = false;
    }

    private void SetTransitionAndNavigate(int targetIndex, ObservableObject viewModel)
    {
        TransitionDirection = targetIndex >= ActiveNavIndex ? 1 : -1;

        // Material 3 style:
        // On Android, we use a consistent, professional transition.
        // On Desktop, we can keep the variety.
        if (OperatingSystem.IsAndroid())
        {
            // We'll use CrossFade (index 2) as it's the closest to M3 FadeThrough
            // without complex shared-axis custom code.
            TransitionType = 2;
        }
        else
        {
            TransitionType = _rng.Next(4);
        }

        ActiveNavIndex = targetIndex;
        CurrentView = viewModel;
        // Auto-close drawer on mobile/narrow after navigation
        IsDrawerOpen = false;
    }

    // ═══════════════ Navigation Commands ═══════════════

    [RelayCommand]
    public void NavigateToHome()
    {
        _homeViewModel ??= _services.GetRequiredService<HomeViewModel>();
        _homeViewModel.RefreshPinnedSensors();
        SetTransitionAndNavigate(0, _homeViewModel);
    }

    [RelayCommand]
    public void NavigateToCanvas()
    {
        NotifyIfDisconnected("Sensor Workspace");
        SetTransitionAndNavigate(1, _canvasViewModel!);
    }

    [RelayCommand]
    public void NavigateToRemote()
    {
        NotifyIfDisconnected("Remote Control");
        _remoteViewModel ??= new RemoteViewModel(
            Connection, this,
            _services.GetRequiredService<Remex.Core.Services.Network.IWakeOnLanService>(),
            _layoutService);
        SetTransitionAndNavigate(2, _remoteViewModel);
    }

    [RelayCommand]
    public void NavigateToAppLauncher()
    {
        NotifyIfDisconnected("App Launcher");
        if (_appLauncherViewModel is null)
        {
            _appLauncherViewModel = _services.GetRequiredService<AppLauncherViewModel>();
        }
        SetTransitionAndNavigate(3, _appLauncherViewModel);
    }

    [RelayCommand]
    public void NavigateToTaskManager()
    {
        NotifyIfDisconnected("Task Manager");
        _taskManagerViewModel ??= new TaskManagerViewModel(Connection);
        SetTransitionAndNavigate(4, _taskManagerViewModel);
    }

    [RelayCommand]
    public void NavigateToRemoteDesktop()
    {
        _remoteDesktopViewModel ??= new RemoteDesktopViewModel(Connection, this, _immersiveMode);
        SetTransitionAndNavigate(5, _remoteDesktopViewModel);
    }

    [RelayCommand]
    public void NavigateToAbout()
    {
        _aboutViewModel ??= new AboutViewModel(Connection, this);
        SetTransitionAndNavigate(6, _aboutViewModel);
    }

    [RelayCommand]
    public void ToggleSettingsPanel()
    {
        IsSettingsPanelOpen = !IsSettingsPanelOpen;

        // Lazily create the settings/customization VMs
        if (IsSettingsPanelOpen)
        {
            EnsureSettingsVm();
            EnsureCustomizationVm();
        }
    }

    [RelayCommand]
    public void CloseSettingsPanel()
    {
        IsSettingsPanelOpen = false;
    }

    [RelayCommand]
    public void ToggleDrawer()
    {
        IsDrawerOpen = !IsDrawerOpen;
    }

    // ═══════════════ Legacy navigation kept for backward compat ═══════════════

    [RelayCommand]
    public void NavigateToSettings()
    {
        IsSettingsPanelOpen = false;
        EnsureSettingsVm();
        CurrentView = _settingsViewModel;
    }

    [RelayCommand]
    public void NavigateToCustomization()
    {
        IsSettingsPanelOpen = false;
        EnsureCustomizationVm();
        CurrentView = _customizationViewModel;
    }

    private void EnsureSettingsVm()
    {
        if (_settingsViewModel is null)
        {
            _settingsViewModel = new SettingsViewModel(_layoutService, Connection, this);
            _ = _settingsViewModel.InitializeAsync();
        }
        _settingsViewModel.RefreshSensors();
    }

    private void EnsureCustomizationVm()
    {
        _customizationViewModel ??= new CustomizationViewModel(this, _layoutService, _themeService);
    }

    /// <summary>
    /// Provides access to the canvas VM for cross-view coordination
    /// (e.g. Home reading pinned sensors from the canvas data).
    /// </summary>
    public CanvasDashboardViewModel? CanvasViewModel => _canvasViewModel;

    /// <summary>Exposed for the settings overlay to bind against.</summary>
    public SettingsViewModel? SettingsVm
    {
        get
        {
            EnsureSettingsVm();
            return _settingsViewModel;
        }
    }

    /// <summary>Exposed for the settings overlay to bind against.</summary>
    public CustomizationViewModel? CustomizationVm
    {
        get
        {
            EnsureCustomizationVm();
            return _customizationViewModel;
        }
    }
}
