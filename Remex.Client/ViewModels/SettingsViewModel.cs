using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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
public partial class SettingsViewModel : ObservableObject, IDisposable
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

    [ObservableProperty]
    private string _accessKey = string.Empty;

    [ObservableProperty]
    private string _language = "en";

    public ObservableCollection<LanguageItem> AvailableLanguages { get; } = new()
    {
        new("English", "en"),
        new("Español", "es"),
        new("हिन्दी", "hi"),
        new("Bahasa Indonesia", "id"),
        new("Polski", "pl"),
        new("Português (BR)", "pt-BR"),
        new("Türkçe", "tr"),
        new("Українська", "uk")
    };

    [ObservableProperty]
    private string _hostRuntimeText = LocalizationService.Instance["Service_HostUnavailable"];

    [ObservableProperty]
    private string _hostCapabilityText = LocalizationService.Instance["Service_HostUnavailableHint"];

    /// <summary>Host JPEG compression quality (10–100) for the screen stream.</summary>
    [ObservableProperty]
    private int _streamQuality = 75;

    /// <summary>Host target frames-per-second for the screen stream.</summary>
    [ObservableProperty]
    private int _streamFps = 30;

    partial void OnStreamQualityChanged(int value) => Save();
    partial void OnStreamFpsChanged(int value) => Save();

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
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Refresh host info defaults if not connected
            if (_connection.HostCapabilities == null)
            {
                HostRuntimeText = LocalizationService.Instance["Service_HostUnavailable"];
                HostCapabilityText = LocalizationService.Instance["Service_HostUnavailableHint"];
            }
        });
    }

    /// <summary>Loads current values from the persisted profile.</summary>
    public async Task InitializeAsync()
    {
        _profile = await _layoutService.LoadAsync();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsSnapToGridEnabled = _profile.IsSnapToGridEnabled;
            GridSize = _profile.GridSize;
            HostAddress = _profile.HostAddress;
            AccessKey = _profile.AccessKey;
            HostPath = _profile.HostPath;
            Language = string.IsNullOrWhiteSpace(_profile.Language) ? "en" : _profile.Language;
            Services.LocalizationService.Instance.SetCulture(Language);
            StreamQuality = _profile.StreamQuality;
            StreamFps = _profile.StreamFps;
            UpdateHostCapabilitySummary();
            RefreshSensors();
        });

        _ = RefreshServiceStatusAsync();
    }

    /// <summary>
    /// Rebuilds the available sensors list from the canvas VM's current cards.
    /// </summary>
    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        foreach (var item in AvailableSensors)
            item.PinChanged -= OnSensorPinChanged;
    }

    public void RefreshSensors()
    {
        // Unsubscribe from old items before clearing
        foreach (var item in AvailableSensors)
            item.PinChanged -= OnSensorPinChanged;
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
            var source = card.Sensor.RawReading?.Source ?? "Unknown";
            var item = new SensorPinItem(name, isPinned, source);
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

    partial void OnIsSnapToGridEnabledChanged(bool value)
    {
        if (_shell.CanvasViewModel is { } canvas)
            canvas.IsSnapToGridEnabled = value;
        Save();
    }

    partial void OnGridSizeChanged(int value)
    {
        if (_shell.CanvasViewModel is { } canvas)
            canvas.GridSize = value;
        Save();
    }

    partial void OnHostAddressChanged(string value)
    {
        // Push the value to the live ConnectionViewModel.
        _connection.HostAddress = value;
        Save();
    }

    partial void OnAccessKeyChanged(string value)
    {
        _connection.AccessKey = value;
        Save();
    }

    partial void OnLanguageChanged(string value)
    {
        Services.LocalizationService.Instance.SetCulture(value);
        Save();
    }

    [ObservableProperty]
    private bool _isDiscovering;

    [RelayCommand]
    private async Task DiscoverHostAsync()
    {
        IsDiscovering = true;
        try
        {
            await _connection.DiscoverHostsCommand.ExecuteAsync(null);
            // Sync the discovered address back into our property
            HostAddress = _connection.HostAddress;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [ObservableProperty]
    private string _savedStatus = string.Empty;

    [RelayCommand]
    private async Task SaveAsync()
    {
        Save();
        await _layoutService.FlushAsync();
        SavedStatus = LocalizationService.Instance["Settings_SavedStatus"];

        // Clear status after 3 seconds
        _ = Task.Delay(3000).ContinueWith(_ => SavedStatus = string.Empty);
    }

    [RelayCommand]
    private async Task SaveAndReconnectAsync()
    {
        Save();
        await _layoutService.FlushAsync();

        // Disconnect first if already connected, then reconnect with new settings
        if (_connection.IsConnected || _connection.IsConnecting)
        {
            _connection.DisconnectCommand.Execute(null);
        }

        // Small delay so the disconnect completes
        await Task.Delay(300);
        await _connection.ConnectCommand.ExecuteAsync(null);
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();

    [RelayCommand]
    private void ReplayTutorial()
    {
        _shell.CloseSettingsPanel();
        _shell.ReplayTutorial();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.HostCapabilities)
            or nameof(ConnectionViewModel.IsConnected))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateHostCapabilitySummary);
        }
        else if (e.PropertyName is nameof(ConnectionViewModel.HostAddress))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HostAddress = _connection.HostAddress);
        }
    }

    private void UpdateHostCapabilitySummary()
    {
        HostRuntimeText = _connection.HostRuntimeSummary;
        HostCapabilityText = _connection.RemoteDesktopAvailabilitySummary;
    }

    // ═══════════════ Windows Service Management ═══════════════

    private const string ServiceName = "RemexHost";

    [ObservableProperty]
    private string _serviceStatusText = LocalizationService.Instance["Service_Checking"];

    public string ServiceSectionHeader => OperatingSystem.IsWindows()
        ? LocalizationService.Instance["Settings_ServiceSection_Windows"]
        : LocalizationService.Instance["Settings_ServiceSection_Linux"];

    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsLinux => OperatingSystem.IsLinux();

    public bool ShowInstallButton => !IsServiceInstalled;
    public bool ShowStartStopButtons => IsServiceInstalled;

    [ObservableProperty]
    private bool _isServiceSectionVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowStartStopButtons))]
    private bool _isServiceInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartStopButtons))]
    private bool _isServiceRunning;

    [ObservableProperty]
    private bool _isServiceBusy;

    [ObservableProperty]
    private string _serviceUsername = $".\\{Environment.UserName}";

    public string ServiceAccountDisplay => string.Format(
        LocalizationService.Instance["Settings_AccountFormat"],
        ServiceUsername);

    partial void OnServiceUsernameChanged(string value)
    {
        OnPropertyChanged(nameof(ServiceAccountDisplay));
    }

    [ObservableProperty]
    private string _servicePassword = string.Empty;

    [ObservableProperty]
    private bool _isCredentialPanelOpen;

    [ObservableProperty]
    private string _serviceLog = string.Empty;

    /// <summary>User-configurable path to the Remex.Host.exe binary or its directory.</summary>
    [ObservableProperty]
    private string _hostPath = string.Empty;

    partial void OnHostPathChanged(string value) => Save();

    [RelayCommand]
    private void ConfigureLogin()
    {
        IsCredentialPanelOpen = !IsCredentialPanelOpen;
    }

    [RelayCommand]
    private async Task RefreshServiceAsync()
    {
        await RefreshServiceStatusAsync();
    }

    // ─────────── Install ───────────

    [RelayCommand]
    private async Task BrowseHostPathAsync()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = desktop.MainWindow;
                if (topLevel == null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Remex.Host.exe",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Executable") { Patterns = new[] { "Remex.Host.exe" } },
                        FilePickerFileTypes.All
                    }
                });

                if (files.Count > 0)
                {
                    var path = files[0].TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        HostPath = path;
                        AppendLog($"Host path set to: {path}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Browse error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            await InstallWindowsServiceAsync();
        }
        else if (OperatingSystem.IsLinux())
        {
            await InstallLinuxServiceAsync();
        }
    }

    private async Task InstallWindowsServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(ServicePassword))
        {
            ServiceStatusText = LocalizationService.Instance["Service_PasswordRequired"];
            return;
        }

        IsServiceBusy = true;
        IsCredentialPanelOpen = false;
        var user = NormalizeUsername(ServiceUsername);

        try
        {
            // Step 1: Locate the host binary
            ServiceStatusText = LocalizationService.Instance["Service_LocatingHost"];
            AppendLog("Searching for Remex.Host.exe…");
            var exePath = FindHostExePath();
            if (exePath == null)
            {
                ServiceStatusText = LocalizationService.Instance["Service_HostNotFound"];
                AppendLog("ERROR: Remex.Host.exe not found. Set the host path in Settings or place Remex.Host.exe next to this application.");
                return;
            }
            AppendLog($"Found host binary: {exePath}");

            // Step 1b: Version verification (warn only, don't block)
            var versionWarning = VerifyHostVersion(exePath);
            if (versionWarning != null)
            {
                AppendLog($"WARN: {versionWarning}");
            }

            // Step 2: Create the service
            ServiceStatusText = LocalizationService.Instance["Service_Creating"];
            AppendLog($"sc.exe create {ServiceName} as {user}");
            var binPath = $"\"{exePath}\"";
            var (createOk, createOut) = await RunElevatedAsync(
                "sc.exe", $"create {ServiceName} binPath= {binPath} start= auto DisplayName= \"Remex Host\"");
            AppendLog(createOut);
            if (!createOk && !createOut.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                ServiceStatusText = LocalizationService.Instance["Service_CreateFailed"];
                return;
            }

            // Step 3: Configure login credentials
            ServiceStatusText = LocalizationService.Instance["Service_ConfiguringLogin"];
            var (cfgOk, cfgOut) = await RunElevatedAsync(
                "sc.exe", $"config {ServiceName} obj= \"{user}\" password= \"{ServicePassword}\"");
            AppendLog(cfgOut);
            if (!cfgOk)
            {
                ServiceStatusText = LocalizationService.Instance["Service_ConfigureFailed"];
                return;
            }

            // Step 4: Grant LogonAsService right via the install script helper
            ServiceStatusText = LocalizationService.Instance["Service_GrantingRights"];
            var scriptPath = FindInstallScript();
            if (scriptPath != null)
            {
                var sanitizedUser = (user ?? string.Empty).Replace("'", "''");
                var (_, grantOut) = await RunElevatedAsync(
                    "powershell.exe",
                    $"-ExecutionPolicy Bypass -NoProfile -Command \"& {{ . '{scriptPath}'; Grant-LogOnAsService '{sanitizedUser}' }}\"");
                AppendLog(grantOut);
            }
            else
            {
                AppendLog("WARN: install-service.ps1 not found — grant SeServiceLogonRight manually via secpol.msc.");
            }

            // Step 5: Description
            await RunElevatedAsync("sc.exe",
                $"description {ServiceName} \"Remex remote-execution and telemetry host service.\"");

            // Step 6: Stop embedded host so the service can bind to the port
            if (App.StopEmbeddedHostAsync != null)
            {
                ServiceStatusText = LocalizationService.Instance["Service_StoppingEmbedded"];
                AppendLog("Stopping embedded host to free port for service.");
                await App.StopEmbeddedHostAsync();
            }

            // Step 7: Start
            ServiceStatusText = LocalizationService.Instance["Service_StartingService"];
            var (startOk, startOut) = await RunElevatedAsync("sc.exe", $"start {ServiceName}");
            AppendLog(startOut);

            if (startOk)
            {
                ServiceStatusText = LocalizationService.Instance["Service_InstalledStarted"];
                // Point the client at the service and reconnect.
                var serviceAddr = $"ws://localhost:{Remex.Core.RemexConstants.DefaultPort}{Remex.Core.RemexConstants.WebSocketPath}";
                _connection.HostAddress = serviceAddr;
                AppendLog($"Reconnecting client to {serviceAddr}…");
                _ = _connection.AutoConnectAsync();
            }
            else
            {
                ServiceStatusText = LocalizationService.Instance["Service_InstalledStartFailed"];
            }
        }
        catch (Exception ex)
        {
            ServiceStatusText = $"Error: {ex.Message}";
            AppendLog($"EXCEPTION: {ex.Message}");
        }
        finally
        {
            ServicePassword = string.Empty;
            await RefreshServiceStatusAsync();
            IsServiceBusy = false;
        }
    }

    private async Task InstallLinuxServiceAsync()
    {
        IsServiceBusy = true;
        try
        {
            ServiceStatusText = LocalizationService.Instance["Service_LocatingHost"];
            var hostBinary = FindHostExePath();
            if (string.IsNullOrEmpty(hostBinary))
            {
                ServiceStatusText = LocalizationService.Instance["Service_HostNotFound"];
                return;
            }

            var serviceName = "remex-host";
            var servicePath = $"/etc/systemd/system/{serviceName}.service";
            var user = Environment.UserName;

            var serviceContent = $@"[Unit]
Description=Remex Host Service
After=network.target

[Service]
Type=simple
User={user}
ExecStart={hostBinary}
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target";

            var tempFile = Path.Combine(Path.GetTempPath(), $"{serviceName}.service");
            await File.WriteAllTextAsync(tempFile, serviceContent);

            AppendLog("Generated systemd service unit file.");

            ServiceStatusText = LocalizationService.Instance["Service_InstallingLinux"];

            // Use pkexec for privilege elevation with safe argument passing
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    ArgumentList = { "sh", "-c", $"mv '{tempFile}' '{servicePath}' && systemctl daemon-reload && systemctl enable {serviceName} && systemctl start {serviceName}" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            AppendLog(LocalizationService.Instance["Service_InstallingLinux"]);
            process.Start();

            if (process != null)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    ServiceStatusText = LocalizationService.Instance["Service_InstalledStarted"];
                    AppendLog(LocalizationService.Instance["Service_InstalledStarted"]);
                }
                else
                {
                    ServiceStatusText = LocalizationService.Instance["Service_InstallFailed"];
                    AppendLog(stderr);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceStatusText = LocalizationService.Instance["Service_InstallFailed"];
            AppendLog($"EXCEPTION: {ex.Message}");
        }
        finally
        {
            await RefreshServiceStatusAsync();
            IsServiceBusy = false;
        }
    }

    // ─────────── Uninstall ───────────

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            await UninstallWindowsServiceAsync();
        }
        else if (OperatingSystem.IsLinux())
        {
            await UninstallLinuxServiceAsync();
        }
    }

    private async Task UninstallWindowsServiceAsync()
    {
        IsServiceBusy = true;

        try
        {
            if (IsServiceRunning)
            {
                ServiceStatusText = LocalizationService.Instance["Service_Stopping"];
                var (_, stopOut) = await RunElevatedAsync("sc.exe", $"stop {ServiceName}");
                AppendLog(stopOut);
                await Task.Delay(2000);
            }

            ServiceStatusText = LocalizationService.Instance["Service_DeletingService"];
            AppendLog($"sc.exe delete {ServiceName}");
            var (ok, output) = await RunElevatedAsync("sc.exe", $"delete {ServiceName}");
            AppendLog(output);
            ServiceStatusText = ok ? LocalizationService.Instance["Service_Uninstalling"] : LocalizationService.Instance["Service_UninstallFailed"];
        }
        catch (Exception ex)
        {
            ServiceStatusText = $"Error: {ex.Message}";
            AppendLog($"EXCEPTION: {ex.Message}");
        }
        finally
        {
            await RefreshServiceStatusAsync();
            IsServiceBusy = false;
        }
    }

    private async Task UninstallLinuxServiceAsync()
    {
        IsServiceBusy = true;
        try
        {
            var serviceName = "remex-host";
            var servicePath = $"/etc/systemd/system/{serviceName}.service";

            ServiceStatusText = LocalizationService.Instance["Service_RemovingLinux"];

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    ArgumentList = { "sh", "-c", $"systemctl stop {serviceName} && systemctl disable {serviceName} && rm '{servicePath}' && systemctl daemon-reload" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            AppendLog(LocalizationService.Instance["Service_RemovingLinux"]);
            process.Start();

            if (process != null)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    ServiceStatusText = LocalizationService.Instance["Service_Uninstalling"];
                    AppendLog(LocalizationService.Instance["Service_Uninstalling"]);
                }
                else
                {
                    ServiceStatusText = LocalizationService.Instance["Service_UninstallFailed"];
                    AppendLog(stderr);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceStatusText = LocalizationService.Instance["Service_UninstallFailed"];
            AppendLog($"EXCEPTION: {ex.Message}");
        }
        finally
        {
            await RefreshServiceStatusAsync();
            IsServiceBusy = false;
        }
    }

    // ─────────── Start / Stop ───────────

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        IsServiceBusy = true;
        ServiceStatusText = LocalizationService.Instance["Service_Starting"];

        if (OperatingSystem.IsWindows())
        {
            var (ok, output) = await RunElevatedAsync("sc.exe", $"start {ServiceName}");
            AppendLog(output);
            ServiceStatusText = ok ? LocalizationService.Instance["Service_StartedMsg"] : LocalizationService.Instance["Service_StartFailed"];
        }
        else if (OperatingSystem.IsLinux())
        {
            var (ok, output) = await RunLinuxElevatedAsync($"systemctl start remex-host");
            AppendLog(output);
            ServiceStatusText = ok ? LocalizationService.Instance["Service_StartedMsg"] : LocalizationService.Instance["Service_StartFailed"];
        }

        await RefreshServiceStatusAsync();
        IsServiceBusy = false;
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        IsServiceBusy = true;
        ServiceStatusText = LocalizationService.Instance["Service_Stopping"];

        if (OperatingSystem.IsWindows())
        {
            var (ok, output) = await RunElevatedAsync("sc.exe", $"stop {ServiceName}");
            AppendLog(output);
            ServiceStatusText = ok ? LocalizationService.Instance["Service_StoppedMsg"] : LocalizationService.Instance["Service_StopFailed"];
        }
        else if (OperatingSystem.IsLinux())
        {
            var (ok, output) = await RunLinuxElevatedAsync($"systemctl stop remex-host");
            AppendLog(output);
            ServiceStatusText = ok ? LocalizationService.Instance["Service_StoppedMsg"] : LocalizationService.Instance["Service_StopFailed"];
        }

        await RefreshServiceStatusAsync();
        IsServiceBusy = false;
    }

    // ─────────── Configure Login (post-install) ───────────

    [RelayCommand]
    private async Task ApplyLoginAsync()
    {
        if (!OperatingSystem.IsWindows() || !IsServiceInstalled) return;
        if (string.IsNullOrWhiteSpace(ServicePassword))
        {
            ServiceStatusText = LocalizationService.Instance["Service_PasswordRequired"];
            return;
        }

        IsServiceBusy = true;
        var user = NormalizeUsername(ServiceUsername);
        ServiceStatusText = LocalizationService.Instance["Service_ApplyingCredentials"];

        var (ok, output) = await RunElevatedAsync(
            "sc.exe", $"config {ServiceName} obj= \"{user}\" password= \"{ServicePassword}\"");
        AppendLog(output);

        var scriptPath = FindInstallScript();
        if (scriptPath != null)
        {
            var sanitizedUser = (user ?? string.Empty).Replace("'", "''");
            var (_, grantOut) = await RunElevatedAsync(
                "powershell.exe",
                $"-ExecutionPolicy Bypass -NoProfile -Command \"& {{ . '{scriptPath}'; Grant-LogOnAsService '{sanitizedUser}' }}\"");
            AppendLog(grantOut);
        }

        ServicePassword = string.Empty;
        IsCredentialPanelOpen = false;
        ServiceStatusText = ok ? LocalizationService.Instance["Service_CredentialsUpdated"] : LocalizationService.Instance["Service_CredentialsFailed"];
        await RefreshServiceStatusAsync();
        IsServiceBusy = false;
    }

    // ═══════════════ Service Helpers ═══════════════

    private static string NormalizeUsername(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return $".\\{Environment.UserName}";

        var trimmed = input.Trim();

        if (trimmed.StartsWith(".\\"))
            return trimmed;

        var backslash = trimmed.IndexOf('\\');
        if (backslash >= 0 && backslash < trimmed.Length - 1)
            return $".\\{trimmed[(backslash + 1)..]}";

        return $".\\{trimmed}";
    }

    private async Task RefreshServiceStatusAsync()
    {
        if (OperatingSystem.IsAndroid())
        {
            IsServiceSectionVisible = false;
            return;
        }

        IsServiceSectionVisible = true;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Basic check for remex-host service
                IsServiceInstalled = File.Exists("/etc/systemd/system/remex-host.service");
                if (IsServiceInstalled)
                {
                    var processes = Process.GetProcessesByName("Remex.Host");
                    IsServiceRunning = processes.Length > 0;
                    ServiceStatusText = IsServiceRunning
                        ? LocalizationService.Instance["Service_Running"]
                        : LocalizationService.Instance["Service_Stopped"];
                }
                else
                {
                    ServiceStatusText = LocalizationService.Instance["Service_NotInstalled"];
                }
            }
            catch { IsServiceInstalled = false; ServiceStatusText = LocalizationService.Instance["Service_Checking"]; }
            return;
        }

        try
        {
            var output = await RunLocalAsync("sc.exe", $"query {ServiceName}");

            if (output.Contains("does not exist") || output.Contains("FAILED 1060"))
            {
                IsServiceInstalled = false;
                IsServiceRunning = false;
                ServiceStatusText = LocalizationService.Instance["Service_NotInstalled"];
            }
            else
            {
                IsServiceInstalled = true;

                if (output.Contains("RUNNING"))
                {
                    IsServiceRunning = true;
                    ServiceStatusText = LocalizationService.Instance["Service_Running"];
                }
                else if (output.Contains("STOPPED"))
                {
                    IsServiceRunning = false;
                    ServiceStatusText = LocalizationService.Instance["Service_Stopped"];
                }
                else if (output.Contains("PENDING"))
                {
                    ServiceStatusText = LocalizationService.Instance["Service_Pending"];
                }
                else
                {
                    IsServiceRunning = false;
                    ServiceStatusText = LocalizationService.Instance["Service_Installed"];
                }

                var qcOut = await RunLocalAsync("sc.exe", $"qc {ServiceName}");
                foreach (var line in qcOut.Split('\n'))
                {
                    if (line.Contains("SERVICE_START_NAME"))
                    {
                        var colon = line.IndexOf(':');
                        if (colon >= 0 && colon < line.Length - 1)
                            ServiceUsername = line[(colon + 1)..].Trim();
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ServiceStatusText = $"Error: {ex.Message}";
        }
    }

    private static async Task<string> RunLocalAsync(string fileName, string arguments)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
    }

    private static async Task<(bool Success, string Output)> RunElevatedAsync(string fileName, string arguments)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"remex_svc_{Guid.NewGuid():N}.log");
        try
        {
            var cmdArgs = $"/c \"\"{fileName}\" {arguments} > \"{tempFile}\" 2>&1\"";
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (proc != null)
                await proc.WaitForExitAsync();

            var output = File.Exists(tempFile) ? (await File.ReadAllTextAsync(tempFile)).Trim() : "";
            var success = proc?.ExitCode == 0;

            if (!success && output.Contains("[SC] ChangeServiceConfig SUCCESS", StringComparison.OrdinalIgnoreCase))
                success = true;

            return (success, string.IsNullOrWhiteSpace(output) ? "(no output)" : output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Operation cancelled (UAC denied).");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    /// <summary>
    /// Runs a command with elevated privileges on Linux via pkexec.
    /// </summary>
    private static async Task<(bool Success, string Output)> RunLinuxElevatedAsync(string command)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    ArgumentList = { "sh", "-c", command },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
            return (process.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? "(no output)" : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the full path to the published Remex.Host.exe binary.
    /// Tries multiple strategies in order of priority:
    /// 1. User-configured custom path (if set in profile)
    /// 2. Adjacent to the running desktop client executable
    /// 3. Sibling "Remex.Host" subfolder beside the client
    /// 4. Sibling folder at parent level (e.g., ..\Remex.Host\)
    /// 5. Dev-time publish output (..\..\..\..\publish\Remex.Host)
    /// </summary>
    private string? FindHostExePath()
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var binaryName = OperatingSystem.IsWindows() ? "Remex.Host.exe" : "Remex.Host";

        // Strategy 1: User-configured custom path
        var customPath = _profile.HostPath;
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath) && customPath.EndsWith(binaryName, StringComparison.OrdinalIgnoreCase))
                return customPath;
            var binInCustom = Path.Combine(customPath, binaryName);
            if (File.Exists(binInCustom)) return binInCustom;
        }

        // Strategy 2: Same directory as running client
        var adjacent = Path.Combine(basePath, binaryName);
        if (File.Exists(adjacent)) return adjacent;

        // Strategy 3: Sibling subfolder
        var siblingSubfolder = Path.Combine(basePath, "Remex.Host", binaryName);
        if (File.Exists(siblingSubfolder)) return siblingSubfolder;

        // Strategy 4: Parent-level sibling
        var parentSibling = Path.GetFullPath(Path.Combine(basePath, "..", "Remex.Host", binaryName));
        if (File.Exists(parentSibling)) return parentSibling;

        // Strategy 5: Dev-time publish output
        var devPublish = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "publish", "Remex.Host", binaryName));
        if (File.Exists(devPublish)) return devPublish;

        return null;
    }

    /// <summary>
    /// Compares the host binary's file version against the client assembly version.
    /// Returns a warning string if they differ, or null if they match.
    /// </summary>
    private static string? VerifyHostVersion(string exePath)
    {
        try
        {
            var hostVersion = FileVersionInfo.GetVersionInfo(exePath);
            var clientAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            var clientVersion = clientAssembly.GetName().Version?.ToString() ?? "unknown";

            if (hostVersion.FileVersion != null && hostVersion.FileVersion != clientVersion)
            {
                return $"Version mismatch: Host is {hostVersion.FileVersion}, Client is {clientVersion}. " +
                       "Consider updating the host binary.";
            }
            return null; // versions match
        }
        catch { return null; }
    }

    private static string? FindInstallScript()
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(basePath, "scripts", "install-service.ps1");
        if (File.Exists(candidate)) return candidate;

        var repoRoot = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", ".."));
        candidate = Path.Combine(repoRoot, "scripts", "install-service.ps1");
        if (File.Exists(candidate)) return candidate;

        return null;
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] {message.Trim()}";
        ServiceLog = string.IsNullOrEmpty(ServiceLog) ? entry : $"{ServiceLog}\n{entry}";
    }

    // ═══════════════ Persistence ═══════════════

    private void Save()
    {
        var updated = _profile with
        {
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = HostAddress,
            AccessKey = AccessKey,
            HostPath = HostPath,
            Language = Language,
            StreamQuality = StreamQuality,
            StreamFps = StreamFps
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

    /// <summary>Telemetry data source: "HWInfo", "WindowsPerf", "Linux", or "Unknown".</summary>
    public string Source { get; }

    [ObservableProperty]
    private bool _isPinned;

    public event System.EventHandler<bool>? PinChanged;

    public SensorPinItem(string sensorName, bool isPinned, string source = "Unknown")
    {
        SensorName = sensorName;
        _isPinned = isPinned;
        Source = source;
    }

    partial void OnIsPinnedChanged(bool value) => PinChanged?.Invoke(this, value);
}
public record LanguageItem(string DisplayName, string Code);
