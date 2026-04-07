using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private string _hostRuntimeText = "Host capabilities unavailable";

    [ObservableProperty]
    private string _hostCapabilityText = "Connect to a host to inspect runtime capabilities.";

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
            AccessKey = _profile.AccessKey;
            Language = string.IsNullOrWhiteSpace(_profile.Language) ? "en" : _profile.Language;
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
        try 
        {
            var culture = new System.Globalization.CultureInfo(value);
            Remex.Client.Localization.Strings.Culture = culture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch { }
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
        SavedStatus = "Settings Saved!";
        
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
    private string _serviceStatusText = "Checking…";

    [ObservableProperty]
    private bool _isWindowsServiceSectionVisible;

    [ObservableProperty]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private bool _isServiceRunning;

    [ObservableProperty]
    private bool _isServiceBusy;

    [ObservableProperty]
    private string _serviceUsername = $".\\{Environment.UserName}";

    [ObservableProperty]
    private string _servicePassword = string.Empty;

    [ObservableProperty]
    private bool _isCredentialPanelOpen;

    [ObservableProperty]
    private string _serviceLog = string.Empty;

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
    private async Task InstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(ServicePassword))
        {
            ServiceStatusText = "Password is required.";
            return;
        }

        IsServiceBusy = true;
        IsCredentialPanelOpen = false;
        var user = NormalizeUsername(ServiceUsername);

        try
        {
            // Step 1: Publish
            ServiceStatusText = "Publishing Remex.Host…";
            AppendLog("Publishing Remex.Host…");
            var (pubOk, pubOut) = await PublishHostAsync();
            AppendLog(pubOut);
            if (!pubOk)
            {
                ServiceStatusText = "Publish failed — see log.";
                return;
            }

            var publishDir = GetPublishDir();
            var exePath = Path.Combine(publishDir, "Remex.Host.exe");
            if (!File.Exists(exePath))
            {
                ServiceStatusText = $"Remex.Host.exe not found in {publishDir}";
                AppendLog($"ERROR: {exePath} not found after publish.");
                return;
            }

            // Step 2: Create the service
            ServiceStatusText = "Creating service…";
            AppendLog($"sc.exe create {ServiceName} as {user}");
            var binPath = $"\"{exePath}\"";
            var (createOk, createOut) = await RunElevatedAsync(
                "sc.exe", $"create {ServiceName} binPath= {binPath} start= auto DisplayName= \"Remex Host\"");
            AppendLog(createOut);
            if (!createOk && !createOut.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                ServiceStatusText = "Failed to create service — see log.";
                return;
            }

            // Step 3: Configure login credentials
            ServiceStatusText = "Configuring login…";
            var (cfgOk, cfgOut) = await RunElevatedAsync(
                "sc.exe", $"config {ServiceName} obj= \"{user}\" password= \"{ServicePassword}\"");
            AppendLog(cfgOut);
            if (!cfgOk)
            {
                ServiceStatusText = "Failed to configure credentials — see log.";
                return;
            }

            // Step 4: Grant LogonAsService right via the install script helper
            ServiceStatusText = "Granting logon rights…";
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
                ServiceStatusText = "Stopping embedded host…";
                AppendLog("Stopping embedded host to free port for service.");
                await App.StopEmbeddedHostAsync();
            }

            // Step 7: Start
            ServiceStatusText = "Starting service…";
            var (startOk, startOut) = await RunElevatedAsync("sc.exe", $"start {ServiceName}");
            AppendLog(startOut);

            if (startOk)
            {
                ServiceStatusText = "Installed & started.";
                // Point the client at the service and reconnect.
                var serviceAddr = $"ws://localhost:{Remex.Core.RemexConstants.DefaultPort}{Remex.Core.RemexConstants.WebSocketPath}";
                _connection.HostAddress = serviceAddr;
                AppendLog($"Reconnecting client to {serviceAddr}…");
                _ = _connection.AutoConnectAsync();
            }
            else
            {
                ServiceStatusText = "Installed — start may have failed, see log.";
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

    // ─────────── Uninstall ───────────

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsServiceBusy = true;

        try
        {
            if (IsServiceRunning)
            {
                ServiceStatusText = "Stopping service…";
                var (_, stopOut) = await RunElevatedAsync("sc.exe", $"stop {ServiceName}");
                AppendLog(stopOut);
                await Task.Delay(2000);
            }

            ServiceStatusText = "Deleting service…";
            AppendLog($"sc.exe delete {ServiceName}");
            var (ok, output) = await RunElevatedAsync("sc.exe", $"delete {ServiceName}");
            AppendLog(output);
            ServiceStatusText = ok ? "Service uninstalled." : "Uninstall may have failed — see log.";
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

    // ─────────── Start / Stop ───────────

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsServiceBusy = true;
        ServiceStatusText = "Starting…";

        var (ok, output) = await RunElevatedAsync("sc.exe", $"start {ServiceName}");
        AppendLog(output);
        ServiceStatusText = ok ? "Started." : "Start failed — see log.";

        await RefreshServiceStatusAsync();
        IsServiceBusy = false;
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsServiceBusy = true;
        ServiceStatusText = "Stopping…";

        var (ok, output) = await RunElevatedAsync("sc.exe", $"stop {ServiceName}");
        AppendLog(output);
        ServiceStatusText = ok ? "Stopped." : "Stop failed — see log.";

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
            ServiceStatusText = "Password is required.";
            return;
        }

        IsServiceBusy = true;
        var user = NormalizeUsername(ServiceUsername);
        ServiceStatusText = "Applying credentials…";

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
        ServiceStatusText = ok ? "Credentials updated — restart the service for changes to take effect." : "Failed — see log.";
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
        if (!OperatingSystem.IsWindows())
        {
            IsWindowsServiceSectionVisible = false;
            return;
        }

        IsWindowsServiceSectionVisible = true;

        try
        {
            var output = await RunLocalAsync("sc.exe", $"query {ServiceName}");

            if (output.Contains("does not exist") || output.Contains("FAILED 1060"))
            {
                IsServiceInstalled = false;
                IsServiceRunning = false;
                ServiceStatusText = "Not Installed";
            }
            else
            {
                IsServiceInstalled = true;

                if (output.Contains("RUNNING"))
                {
                    IsServiceRunning = true;
                    ServiceStatusText = "Running";
                }
                else if (output.Contains("STOPPED"))
                {
                    IsServiceRunning = false;
                    ServiceStatusText = "Stopped";
                }
                else if (output.Contains("PENDING"))
                {
                    ServiceStatusText = "Pending…";
                }
                else
                {
                    IsServiceRunning = false;
                    ServiceStatusText = "Installed";
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

    private async Task<(bool Success, string Output)> PublishHostAsync()
    {
        try
        {
            var projectDir = FindProjectDir();
            if (projectDir == null)
                return (false, "Could not locate Remex.Host project directory.");

            var publishDir = GetPublishDir();
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"publish \"{projectDir}\" -c Release -o \"{publishDir}\" --self-contained false",
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

            var output = $"{stdout}\n{stderr}".Trim();
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, $"Publish exception: {ex.Message}");
        }
    }

    private static string GetPublishDir()
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", ".."));
        var candidate = Path.Combine(repoRoot, "publish", "Remex.Host");
        if (Directory.Exists(Path.GetDirectoryName(candidate)!) || Directory.Exists(repoRoot))
            return candidate;
        return Path.Combine(basePath, "publish", "Remex.Host");
    }

    private static string? FindProjectDir()
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", ".."));
        var candidate = Path.Combine(repoRoot, "Remex.Host");
        if (File.Exists(Path.Combine(candidate, "Remex.Host.csproj")))
            return candidate;
        candidate = Path.Combine(basePath, "..", "Remex.Host");
        if (File.Exists(Path.Combine(candidate, "Remex.Host.csproj")))
            return candidate;
        return null;
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
public record LanguageItem(string DisplayName, string Code);
