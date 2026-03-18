using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// Manages snap-to-grid toggle, grid size, persisted host address,
/// and sensor pinning to the Home screen.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ConnectionViewModel _connection;
    private readonly ShellViewModel _shell;
    private DashboardProfile _profile = new();

    [ObservableProperty]
    private bool _isSnapToGridEnabled;

    [ObservableProperty]
    private int _gridSize = 50;

    [ObservableProperty]
    private string _hostAddress = "ws://localhost:5005/ws";

    /// <summary>Available sensors with checkboxes for pinning to Home.</summary>
    public ObservableCollection<SensorPinItem> AvailableSensors { get; } = new();

    public SettingsViewModel(
        DashboardLayoutService layoutService,
        ConnectionViewModel connection,
        ShellViewModel shell)
    {
        _layoutService = layoutService;
        _connection = connection;
        _shell = shell;
    }

    /// <summary>Loads current values from the persisted profile.</summary>
        public async Task InitializeAsync()
    {
        _profile = await _layoutService.LoadAsync().ConfigureAwait(false);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsSnapToGridEnabled = _profile.IsSnapToGridEnabled;
            GridSize = _profile.GridSize;
            HostAddress = _profile.HostAddress;
            RefreshSensors();
        });

        _ = RefreshServiceStatusAsync();
    }

    /// <summary>
    /// Rebuilds the available sensors list from the canvas VM's current cards.
    /// </summary>
    public void RefreshSensors()
    {
        AvailableSensors.Clear();

        var canvas = _shell.CanvasViewModel;
        if (canvas is null) return;

        var sensorCards = canvas.Cards
            .Where(c => c.CardType == "Sensor" && c.Sensor != null)
            .OrderBy(c => c.Sensor!.Name);

        foreach (var card in sensorCards)
        {
            var name = card.Sensor!.Name;
            var isPinned = _profile.PinnedSensorIds.Contains(name);
            var item = new SensorPinItem(name, isPinned);
            item.PinChanged += OnSensorPinChanged;
            AvailableSensors.Add(item);
        }
    }

    private void OnSensorPinChanged(object? sender, bool isPinned)
    {
        if (sender is not SensorPinItem item) return;

        // Update the canvas card's pinned state
        var canvas = _shell.CanvasViewModel;
        var card = canvas?.Cards.FirstOrDefault(c => c.Sensor?.Name == item.SensorName);
        if (card != null)
        {
            card.IsPinnedToHome = isPinned;
        }

        // Update profile
        if (isPinned && !_profile.PinnedSensorIds.Contains(item.SensorName))
            _profile.PinnedSensorIds.Add(item.SensorName);
        else if (!isPinned)
            _profile.PinnedSensorIds.Remove(item.SensorName);

        Save();
    }

    // ═══════════════ Change handlers ═══════════════

    partial void OnIsSnapToGridEnabledChanged(bool value) => Save();
    partial void OnGridSizeChanged(int value) => Save();

    partial void OnHostAddressChanged(string value)
    {
        // Push the value to the live ConnectionViewModel.
        _connection.HostAddress = value;
        Save();
    }

    [ObservableProperty]
    private string _savedStatus = string.Empty;

    [RelayCommand]
    private async Task SaveAsync()
    {
        Save();
        await _layoutService.FlushAsync();
        SavedStatus = "Settings Saved!";
        
        // Clear status after 3 seconds
        _ = Task.Delay(3000).ContinueWith(_ => SavedStatus = string.Empty);
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();

    // ═══════════════ Windows Service Management ═══════════════

    [ObservableProperty]
    private string _serviceStatusText = "Checking...";

    [ObservableProperty]
    private bool _isWindowsServiceSectionVisible;

    [ObservableProperty]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private bool _isServiceRunning;
    [ObservableProperty]
    private bool _canStartService;

    [ObservableProperty]
    private bool _canStopService;

    [ObservableProperty]
    private bool _canInstallService;

    [ObservableProperty]
    private bool _canUninstallService;

    [ObservableProperty]
    private bool _isPasswordInputVisible;

    [ObservableProperty]
    private string _servicePassword = string.Empty;

    [RelayCommand]
    private void ShowInstallPrompt()
    {
        if (!System.OperatingSystem.IsWindows()) return;
        CanInstallService = false;
        IsPasswordInputVisible = true;
        ServiceStatusText = $"Enter your Windows password for '{System.Environment.UserName}' to install the service.";
    }

    [RelayCommand]
    private void CancelInstall()
    {
        IsPasswordInputVisible = false;
        ServicePassword = string.Empty;
        _ = RefreshServiceStatusAsync();
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        if (!System.OperatingSystem.IsWindows()) return;
        if (string.IsNullOrEmpty(ServicePassword))
        {
            ServiceStatusText = "Password is required.";
            return;
        }

        var username = $".\\{System.Environment.UserName}";
        IsPasswordInputVisible = false;
        ServiceStatusText = "Publishing & installing…";

        // Pass credentials via environment variable to avoid command-line exposure.
        await RunServiceScriptAsync($"-Action Install -Username \"{username}\"", ServicePassword);
        ServicePassword = string.Empty;
        await RefreshServiceStatusAsync();
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (!System.OperatingSystem.IsWindows()) return;

        ServiceStatusText = "Uninstalling...";
        await RunServiceScriptAsync("-Action Uninstall");
        await RefreshServiceStatusAsync();
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        if (!System.OperatingSystem.IsWindows()) return;

        ServiceStatusText = "Starting...";
        await RunServiceScriptAsync("-Action Start");
        await RefreshServiceStatusAsync();
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        if (!System.OperatingSystem.IsWindows()) return;

        ServiceStatusText = "Stopping...";
        await RunServiceScriptAsync("-Action Stop");
        await RefreshServiceStatusAsync();
    }

    private async Task RefreshServiceStatusAsync()
    {
        if (!System.OperatingSystem.IsWindows())
        {
            IsWindowsServiceSectionVisible = false;
            return;
        }

        IsWindowsServiceSectionVisible = true;

        try
        {
            // Use sc.exe to check service status
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query RemexHost",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

                        if (output.Contains("does not exist") || proc.ExitCode != 0)
            {
                IsServiceInstalled = false;
                IsServiceRunning = false;
                ServiceStatusText = "Not Installed";
                CanInstallService = true;
                CanUninstallService = false;
                CanStartService = false;
                CanStopService = false;
            }
            else
            {
                IsServiceInstalled = true;
                CanInstallService = false;
                CanUninstallService = true;

                if (output.Contains("RUNNING"))
                {
                    IsServiceRunning = true;
                    ServiceStatusText = "Running";
                    CanStartService = false;
                    CanStopService = true;
                }
                else
                {
                    IsServiceRunning = false;
                    ServiceStatusText = "Stopped";
                    CanStartService = true;
                    CanStopService = false;
                }
            }
        }
        catch (System.Exception ex)
        {
            ServiceStatusText = $"Error: {ex.Message}";
        }
    }

    private async Task RunServiceScriptAsync(string args, string? password = null)
    {
        try
        {
            var basePath = System.AppDomain.CurrentDomain.BaseDirectory;
            var scriptPath = System.IO.Path.Combine(basePath, "scripts", "install-service.ps1");

            // Fallback for dev environment
            if (!System.IO.File.Exists(scriptPath))
            {
                var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, "..", "..", "..", "..", ".."));
                scriptPath = System.IO.Path.Combine(repoRoot, "scripts", "install-service.ps1");
            }

            if (!System.IO.File.Exists(scriptPath))
            {
                ServiceStatusText = "Error: install-service.ps1 not found.";
                return;
            }

            if (!string.IsNullOrEmpty(password))
            {
                args += $" -Password \"{password}\"";
            }

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\" {args}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });

            if (proc != null)
                await proc.WaitForExitAsync();

            if (proc?.ExitCode != 0)
            {
                ServiceStatusText = $"Error: Script exited with code {proc?.ExitCode}.";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ServiceStatusText = "Installation cancelled (UAC denied).";
        }
        catch (System.Exception ex)
        {
            ServiceStatusText = $"Error: {ex.Message}";
        }
    }

    // ═══════════════ Persistence ═══════════════

    private void Save()
    {
        var updated = _profile with
        {
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = HostAddress,
        };

        _profile = updated;
        _layoutService.RequestSave(updated);
    }
}

/// <summary>
/// Represents a sensor that can be pinned/unpinned to Home from Settings.
/// </summary>
public partial class SensorPinItem : ObservableObject
{
    public string SensorName { get; }

    [ObservableProperty]
    private bool _isPinned;

    public event System.EventHandler<bool>? PinChanged;

    public SensorPinItem(string sensorName, bool isPinned)
    {
        SensorName = sensorName;
        _isPinned = isPinned;
    }

    partial void OnIsPinnedChanged(bool value) => PinChanged?.Invoke(this, value);
}
