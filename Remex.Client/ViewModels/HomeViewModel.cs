using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Home "NOC-style" landing page.
/// Shows connection status, pinned sensor summaries, and navigation buttons.
/// </summary>
public partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;

    /// <summary>Shared connection ViewModel — drives the status hero card.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>Pinned sensor summaries displayed in the UniformGrid.</summary>
    public ObservableCollection<SensorViewModel> PinnedSensors { get; } = new();

    public HomeViewModel(ConnectionViewModel connection, ShellViewModel shell)
    {
        Connection = connection;
        _shell = shell;
    }

    /// <summary>
    /// Refreshes the pinned sensor list from the canvas VM's data.
    /// Called when navigating back to Home.
    /// </summary>
    public void RefreshPinnedSensors()
    {
        var canvas = _shell.CanvasViewModel;
        if (canvas is null) return;

        // Use the profile as the source of truth for which sensors are pinned
        var pinnedIds = _shell.LayoutService.CurrentProfile?.PinnedSensorIds ?? new List<string>();

        PinnedSensors.Clear();

        // Strategy: find a SensorViewModel for every ID in the pinned list.
        // We look in placed cards first, then in staged (discovered) templates.
        foreach (var id in pinnedIds)
        {
            var sensorVm = canvas.Cards
                .Where(c => c.CardType == "Sensor" && string.Equals(c.Sensor?.Name, id, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Sensor)
                .FirstOrDefault()
                ?? canvas.StagedCards
                .Where(c => c.CardType == "Sensor" && string.Equals(c.Sensor?.Name, id, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Sensor)
                .FirstOrDefault();

            if (sensorVm != null)
            {
                PinnedSensors.Add(sensorVm);
            }
        }
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateToHome() => _shell.NavigateToHome();

    [RelayCommand]
    private void NavigateToCanvas() => _shell.NavigateToCanvas();

    [RelayCommand]
    private void NavigateToSettings() => _shell.NavigateToSettings();

    [RelayCommand]
    private void NavigateToRemote() => _shell.NavigateToRemote();

    [RelayCommand]
    private void NavigateToAppLauncher() => _shell.NavigateToAppLauncher();

    [RelayCommand]
    private void NavigateToCustomization() => _shell.NavigateToCustomization();

    [RelayCommand]
    private void NavigateToRemoteDesktop() => _shell.NavigateToRemoteDesktop();

    [RelayCommand]
    private void NavigateToTaskManager() => _shell.NavigateToTaskManager();

    public void Dispose()
    {
        // No resources to dispose currently, but implementing IDisposable for consistency
        // in the ViewModel disposal hierarchy
    }
}
