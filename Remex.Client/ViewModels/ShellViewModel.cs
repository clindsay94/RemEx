using System;
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

    /// <summary>Shared connection logic — injected into child VMs that need it.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>
    /// The currently active child ViewModel, bound to a TransitioningContentControl.
    /// </summary>
    [ObservableProperty]
    private ObservableObject? _currentView;

    // ═══════════════ Child VMs (lazy-created, cached) ═══════════════

    private HomeViewModel? _homeViewModel;
    private CanvasDashboardViewModel? _canvasViewModel;
    private SettingsViewModel? _settingsViewModel;
    private RemoteViewModel? _remoteViewModel;
    private AppLauncherViewModel? _appLauncherViewModel;
    private CustomizationViewModel? _customizationViewModel;
    private RemoteDesktopViewModel? _remoteDesktopViewModel;

    public ShellViewModel(DashboardLayoutService layoutService, ConnectionViewModel connectionViewModel)
    {
        _layoutService = layoutService;
        Connection = connectionViewModel;

        // Initialize background/shared VMs
        _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this);
        _ = _canvasViewModel.InitializeAsync();

        // Default to Home on startup.
        NavigateToHome();
    }

    // ═══════════════ Navigation Commands ═══════════════

    [RelayCommand]
    public void NavigateToHome()
    {
        _homeViewModel ??= new HomeViewModel(Connection, this);
        _homeViewModel.RefreshPinnedSensors();
        CurrentView = _homeViewModel;
    }

    [RelayCommand]
    public void NavigateToCanvas()
    {
        CurrentView = _canvasViewModel;
    }

    [RelayCommand]
    public void NavigateToSettings()
    {
        if (_settingsViewModel is null)
        {
            _settingsViewModel = new SettingsViewModel(_layoutService, Connection, this);
            _ = _settingsViewModel.InitializeAsync();
        }

        // Refresh the sensor pin list with current canvas data.
        _settingsViewModel.RefreshSensors();
        CurrentView = _settingsViewModel;
    }

    [RelayCommand]
    public void NavigateToRemote()
    {
        _remoteViewModel ??= new RemoteViewModel(
            Connection, this,
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Remex.Core.Services.Network.IWakeOnLanService>(App.Services),
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Remex.Core.Services.Command.ISystemCommandService>(App.Services),
            _layoutService);
        CurrentView = _remoteViewModel;
    }

    [RelayCommand]
    public void NavigateToAppLauncher()
    {
        if (_appLauncherViewModel is null)
        {
            _appLauncherViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AppLauncherViewModel>(App.Services);
        }
        CurrentView = _appLauncherViewModel;
    }

    [RelayCommand]
    public void NavigateToCustomization()
    {
        _customizationViewModel ??= new CustomizationViewModel(this);
        CurrentView = _customizationViewModel;
    }

    [RelayCommand]
    public void NavigateToRemoteDesktop()
    {
        _remoteDesktopViewModel ??= new RemoteDesktopViewModel(Connection, this);
        CurrentView = _remoteDesktopViewModel;
    }

    /// <summary>
    /// Provides access to the canvas VM for cross-view coordination
    /// (e.g. Home reading pinned sensors from the canvas data).
    /// </summary>
    public CanvasDashboardViewModel? CanvasViewModel => _canvasViewModel;
}
