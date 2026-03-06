using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Home "NOC-style" landing page.
/// Shows connection status, pinned sensor summaries, and navigation buttons.
/// </summary>
public partial class HomeViewModel : ObservableObject
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

        PinnedSensors.Clear();
        foreach (var card in canvas.Cards
                     .Where(c => c.CardType == "Sensor" && c.IsPinnedToHome && c.Sensor != null))
        {
            PinnedSensors.Add(card.Sensor!);
        }
    }

    // ═══════════════ Navigation ═══════════════

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
}
