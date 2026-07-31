using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Messages;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Home "NOC-style" landing page.
/// Shows connection status, pinned sensor summaries, and navigation buttons.
/// </summary>
public partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly System.ComponentModel.PropertyChangedEventHandler _onConnectionChanged;
    private readonly System.ComponentModel.PropertyChangedEventHandler _onShellChanged;
    private readonly Action<TelemetryPayload> _onTelemetry;
    private readonly NotifyCollectionChangedEventHandler _onActivityChanged;

    /// <summary>Shared connection ViewModel — drives the status hero card.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>Exposes ShellViewModel so AXAML can bind to shell-level preferences.</summary>
    public ShellViewModel Shell => _shell;

    /// <summary>
    /// True when the connection status dot should display its pulse animation:
    /// only when actually connected and the user hasn't opted in to reduced motion.
    /// </summary>
    public bool ShowConnectionPulse => Connection.IsConnected && !_shell.IsReducedMotion;

    /// <summary>Pinned sensor summaries displayed in the UniformGrid.</summary>
    public ObservableCollection<SensorViewModel> PinnedSensors { get; } = new();

    // ─── Always-on stats strip (sourced live from the host telemetry stream) ───

    /// <summary>CPU load as a display string (e.g. "37%"), or "—" when no telemetry is flowing.</summary>
    [ObservableProperty]
    private string _cpuText = "—";

    /// <summary>Memory load as a display string (e.g. "58%"), or "—" when disconnected.</summary>
    [ObservableProperty]
    private string _ramText = "—";

    /// <summary>Host uptime display string (e.g. "3d 5h 12m"), or "—" when disconnected.</summary>
    [ObservableProperty]
    private string _uptimeText = "—";

    /// <summary>CPU load 0–100, for the mini progress bar.</summary>
    [ObservableProperty]
    private double _cpuPercent;

    /// <summary>Memory load 0–100, for the mini progress bar.</summary>
    [ObservableProperty]
    private double _ramPercent;

    // ─── Recent activity feed (shared, cross-container store) ───

    /// <summary>Newest-first feed of recent file transfers, app launches, and commands.</summary>
    public ObservableCollection<ActivityEntry> RecentActivity => ActivityService.Instance.Recent;

    /// <summary>True when there is at least one activity entry (drives the empty-state).</summary>
    public bool HasRecentActivity => ActivityService.Instance.Recent.Count > 0;

    public HomeViewModel(ConnectionViewModel connection, ShellViewModel shell)
    {
        Connection = connection;
        _shell = shell;

        // Re-compute ShowConnectionPulse whenever either dependency changes; blank the stats
        // strip the instant the link drops so it never shows stale numbers.
        _onConnectionChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
            {
                OnPropertyChanged(nameof(ShowConnectionPulse));
                if (!Connection.IsConnected)
                    UpdateStats(null);
            }
        };
        _onShellChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsReducedMotion))
                OnPropertyChanged(nameof(ShowConnectionPulse));
        };
        Connection.PropertyChanged += _onConnectionChanged;
        _shell.PropertyChanged += _onShellChanged;

        // Drive the always-on stats strip from the existing ~1 Hz telemetry stream (already
        // marshalled to the UI thread by ConnectionViewModel — no extra timer needed).
        _onTelemetry = payload => UpdateStats(payload);
        Connection.TelemetryReceived += _onTelemetry;
        UpdateStats(Connection.Telemetry);

        // Keep the recent-activity empty-state flag live as events are recorded.
        _onActivityChanged = (_, _) => OnPropertyChanged(nameof(HasRecentActivity));
        ActivityService.Instance.Recent.CollectionChanged += _onActivityChanged;
    }

    /// <summary>Projects a telemetry payload into the CPU / memory / uptime strip. Null blanks it.</summary>
    private void UpdateStats(TelemetryPayload? payload)
    {
        if (payload is null)
        {
            CpuText = "—";
            RamText = "—";
            UptimeText = "—";
            CpuPercent = 0;
            RamPercent = 0;
            return;
        }

        // CPU: any "%" reading in the CPU category (HWiNFO relabels the literal name), else the
        // WindowsPerf/Linux "Total CPU Usage" fallback. Both are 0–100.
        var cpu = payload.Sensors.FirstOrDefault(s =>
                      string.Equals(s.Category, "CPU", StringComparison.OrdinalIgnoreCase) && s.Unit == "%")
                  ?? payload.Sensors.FirstOrDefault(s => s.Name == "Total CPU Usage");

        // RAM: the 0–100 "Physical Memory Load" reading — avoids the MB-vs-GB cross-platform
        // mismatch in the "used/available" sensors.
        var ram = payload.Sensors.FirstOrDefault(s => s.Name == "Physical Memory Load")
                  ?? payload.Sensors.FirstOrDefault(s =>
                      string.Equals(s.Category, "Memory", StringComparison.OrdinalIgnoreCase) && s.Unit == "%");

        CpuPercent = cpu?.Value ?? 0;
        RamPercent = ram?.Value ?? 0;
        CpuText = cpu is null ? "—" : $"{cpu.Value:F0}%";
        RamText = ram is null ? "—" : $"{ram.Value:F0}%";
        UptimeText = string.IsNullOrWhiteSpace(payload.UptimeText) ? "—" : payload.UptimeText;
    }

    /// <summary>Clears the recent-activity feed.</summary>
    [RelayCommand]
    private void ClearActivity() => ActivityService.Instance.Clear();

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

    [RelayCommand]
    private void NavigateToFileTransfer() => _shell.NavigateToFileTransfer();

    [RelayCommand]
    private void NavigateToDiagnosticLogs() => _shell.NavigateToDiagnosticLogs();

    [RelayCommand]
    private void NavigateToAbout() => _shell.NavigateToAbout();

    // ═══════════════ External links ═══════════════

    [RelayCommand]
    private void OpenGitHub() => OpenUrl("https://github.com/clindsay94/remex");

    [RelayCommand]
    private void OpenPlayStore() => OpenUrl("https://play.google.com/store/apps/details?id=com.clindsay94.remex");

    [RelayCommand]
    private void OpenHwInfo() => OpenUrl("https://www.hwinfo.com/download/");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Ignore if no browser / handler is available.
        }
    }

    public void Dispose()
    {
        Connection.PropertyChanged -= _onConnectionChanged;
        _shell.PropertyChanged -= _onShellChanged;
        Connection.TelemetryReceived -= _onTelemetry;
        ActivityService.Instance.Recent.CollectionChanged -= _onActivityChanged;
    }
}
