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

    public ShellViewModel(DashboardLayoutService layoutService)
    {
        _layoutService = layoutService;
        Connection = new ConnectionViewModel();

        // Default to Home on startup.
        NavigateToHome();
    }

    // ═══════════════ Navigation Commands ═══════════════

    [RelayCommand]
    public void NavigateToHome()
    {
        _homeViewModel ??= new HomeViewModel(Connection, this);
        CurrentView = _homeViewModel;
    }

    [RelayCommand]
    public void NavigateToCanvas()
    {
        if (_canvasViewModel is null)
        {
            _canvasViewModel = new CanvasDashboardViewModel(Connection, _layoutService, this);
            _ = _canvasViewModel.InitializeAsync();
        }
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
        CurrentView = _settingsViewModel;
    }

    /// <summary>
    /// Provides access to the canvas VM for cross-view coordination
    /// (e.g. Home reading pinned sensors from the canvas data).
    /// </summary>
    public CanvasDashboardViewModel? CanvasViewModel => _canvasViewModel;
}
