using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;

namespace Remex.Client.ViewModels;

/// <summary>
/// Top-level ViewModel that owns navigation between Home, Canvas, and Settings.
/// Shared resources (ConnectionViewModel, DashboardLayoutService) live here and
/// are injected into child ViewModels.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ThemeService _themeService;
    private readonly IImmersiveModeService? _immersiveMode;
    private static readonly Random _rng = new();
    private bool _welcomeSplashStarted;

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

    // ═══════════════ Child VMs (lazy-created, cached) ═══════════════

    private HomeViewModel? _homeViewModel;
    private CanvasDashboardViewModel? _canvasViewModel;
    private SettingsViewModel? _settingsViewModel;
    private RemoteViewModel? _remoteViewModel;
    private AppLauncherViewModel? _appLauncherViewModel;
    private CustomizationViewModel? _customizationViewModel;
    private RemoteDesktopViewModel? _remoteDesktopViewModel;
    private TaskManagerViewModel? _taskManagerViewModel;

    [ObservableProperty]
    private Remex.Core.Models.CustomizationSettings _customization = new();

    public ShellViewModel(DashboardLayoutService layoutService, ThemeService themeService, ConnectionViewModel connectionViewModel, IImmersiveModeService? immersiveMode = null)
    {
        _layoutService = layoutService;
        _themeService = themeService;
        _immersiveMode = immersiveMode;
        Connection = connectionViewModel;

        _themeService.CustomizationApplied += settings => Customization = settings;
        if (_layoutService.CurrentProfile?.Customization != null)
        {
            Customization = _layoutService.CurrentProfile.Customization;
        }

        // Initialize background/shared VMs
        _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this);
        _ = _canvasViewModel.InitializeAsync();

        // Default to Home on startup.
        NavigateToHome();
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
        await Task.Delay(1800).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => ShowWelcomeSplash = false);
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
        _homeViewModel ??= new HomeViewModel(Connection, this);
        _homeViewModel.RefreshPinnedSensors();
        SetTransitionAndNavigate(0, _homeViewModel);
    }

    [RelayCommand]
    public void NavigateToCanvas()
    {
        SetTransitionAndNavigate(1, _canvasViewModel!);
    }

    [RelayCommand]
    public void NavigateToRemote()
    {
        _remoteViewModel ??= new RemoteViewModel(
            Connection, this,
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Remex.Core.Services.Network.IWakeOnLanService>(App.Services),
            _layoutService);
        SetTransitionAndNavigate(2, _remoteViewModel);
    }

    [RelayCommand]
    public void NavigateToAppLauncher()
    {
        if (_appLauncherViewModel is null)
        {
            _appLauncherViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AppLauncherViewModel>(App.Services);
        }
        SetTransitionAndNavigate(3, _appLauncherViewModel);
    }

    [RelayCommand]
    public void NavigateToTaskManager()
    {
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
        // Now opens the settings overlay instead of navigating
        EnsureSettingsVm();
        EnsureCustomizationVm();
        IsSettingsPanelOpen = true;
    }

    [RelayCommand]
    public void NavigateToCustomization()
    {
        // Now opens the settings overlay instead of navigating
        EnsureSettingsVm();
        EnsureCustomizationVm();
        IsSettingsPanelOpen = true;
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
