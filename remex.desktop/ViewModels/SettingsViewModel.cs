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
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

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
    private readonly FileTransferRootSettingsService _fileTransferRootSettings;
    private DashboardProfile _profile = new();

    [ObservableProperty]
    private bool _isSnapToGridEnabled;

    [ObservableProperty]
    private int _gridSize = 50;

    [ObservableProperty]
    private string _hostAddress = "wss://localhost:5005/ws";

    [ObservableProperty]
    private string _language = "en";

    /// <summary>
    /// When true, the main window's X button hides the app to the system tray instead
    /// of exiting. When false, closing the window exits the app entirely.
    /// </summary>
    [ObservableProperty]
    private bool _isCloseToTrayEnabled = true;

    partial void OnIsCloseToTrayEnabledChanged(bool value) => Save();

    [ObservableProperty]
    private bool _isLaunchAtLoginEnabled;

    partial void OnIsLaunchAtLoginEnabledChanged(bool value)
    {
        var startupService = App.Services?.GetService(typeof(IStartupRegistrationService)) as IStartupRegistrationService;
        if (startupService != null && startupService.IsSupported)
        {
            startupService.SetEnabled(value);
        }
    }

    [ObservableProperty]
    private bool _isLaunchAtLoginSupported;

    // --- Keep session unlocked (opt-in unattended access) (RemEx-l6o) ---
    // Guards the load-time assignment so seeding the toggle from the persisted flag does not trigger
    // a redundant write (and a possible revert) back through the change handler.
    private bool _suppressKeepUnlockedWrite;

    [ObservableProperty]
    private bool _isKeepSessionUnlockedEnabled;

    [ObservableProperty]
    private bool _isKeepSessionUnlockedSupported;

    partial void OnIsKeepSessionUnlockedEnabledChanged(bool value)
    {
        if (_suppressKeepUnlockedWrite)
        {
            return;
        }

        var svc = App.Services?.GetService(typeof(ISessionKeepUnlockedService)) as ISessionKeepUnlockedService;
        if (svc == null || !svc.IsSupported)
        {
            return;
        }

        if (!svc.SetEnabled(value))
        {
            // Persisting the flag failed (e.g. insufficient rights). Revert the toggle without
            // re-entering this handler so the UI reflects the true, unchanged state.
            _suppressKeepUnlockedWrite = true;
            IsKeepSessionUnlockedEnabled = !value;
            _suppressKeepUnlockedWrite = false;
        }
    }

    /// <summary>Fully exits the application (stops the process), same as the tray "Exit".</summary>
    [RelayCommand]
    private void ExitApplication() => App.RequestApplicationShutdown();

    public ObservableCollection<LanguageItem> AvailableLanguages { get; } = new()
    {
        new("English", "en"),
        new("Español", "es"),
        new("Français", "fr"),
        new("हिन्दी", "hi"),
        new("Bahasa Indonesia", "id"),
        new("Polski", "pl"),
        new("Português (BR)", "pt-BR"),
        new("Türkçe", "tr"),
        new("Українська", "uk")
    };

    public Func<FolderPickerOpenOptions, Task<IReadOnlyList<IStorageFolder>>>? PickSharedFolderAsync { get; set; }

    public ObservableCollection<FileTransferSharedRootItem> SharedRoots { get; } = new();

    public bool SupportsSharedFolderConfiguration => !OperatingSystem.IsAndroid();

    public bool HasSharedRoots => SharedRoots.Count > 0;

    [ObservableProperty]
    private string _hostRuntimeText = LocalizationService.Instance["Service_HostUnavailable"];

    [ObservableProperty]
    private string _hostCapabilityText = LocalizationService.Instance["Service_HostUnavailableHint"];

    /// <summary>Host JPEG compression quality (10–100) for the screen stream.</summary>
    [ObservableProperty]
    private int _streamQuality = 100;

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
        ShellViewModel shell,
        FileTransferRootSettingsService fileTransferRootSettings)
    {
        _layoutService = layoutService;
        _connection = connection;
        _shell = shell;
        _fileTransferRootSettings = fileTransferRootSettings;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    /// <summary>Live connection view-model — bound directly from the Connection settings card.</summary>
    public ConnectionViewModel Connection => _connection;

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
            Language = string.IsNullOrWhiteSpace(_profile.Language) ? "en" : _profile.Language;
            IsCloseToTrayEnabled = _profile.CloseToTray;

            var startupService = App.Services?.GetService(typeof(IStartupRegistrationService)) as IStartupRegistrationService;
            if (startupService != null)
            {
                IsLaunchAtLoginSupported = startupService.IsSupported;
                if (IsLaunchAtLoginSupported)
                {
                    IsLaunchAtLoginEnabled = startupService.IsEnabled();
                }
            }

            var keepUnlockedService = App.Services?.GetService(typeof(ISessionKeepUnlockedService)) as ISessionKeepUnlockedService;
            if (keepUnlockedService != null)
            {
                IsKeepSessionUnlockedSupported = keepUnlockedService.IsSupported;
                if (IsKeepSessionUnlockedSupported)
                {
                    // Seed from the persisted flag without triggering a write-back. (RemEx-l6o)
                    _suppressKeepUnlockedWrite = true;
                    IsKeepSessionUnlockedEnabled = keepUnlockedService.IsEnabled();
                    _suppressKeepUnlockedWrite = false;
                }
            }

            Services.LocalizationService.Instance.SetCulture(Language);
            StreamQuality = _profile.StreamQuality;
            StreamFps = _profile.StreamFps;
            UpdateHostCapabilitySummary();
            RefreshSensors();

            // Seed connection history from the persisted profile
            _connection.ConnectionHistory.Clear();
            foreach (var entry in _profile.ConnectionHistory ?? Enumerable.Empty<ConnectionProfile>())
                _connection.ConnectionHistory.Add(entry);
        });

        await LoadSharedRootsAsync();

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
        foreach (var root in SharedRoots)
            UnsubscribeSharedRoot(root);
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
            var pinnedIds = _profile.PinnedSensorIds ?? Enumerable.Empty<string>();
            var isPinned = pinnedIds.Contains(name);
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
        if (_profile.PinnedSensorIds == null)
        {
            // We can't assign to _profile.PinnedSensorIds directly because it's init-only.
            // But _profile is a local field of type DashboardProfile (record).
            // We should use 'with' or just ensure the list is initialized if it's a List<T>.
            // Wait, DashboardProfile.PinnedSensorIds is public List<string> PinnedSensorIds { get; init; } = new();
            // If it's null, we need to replace the profile instance or the property.
            _profile = _profile with { PinnedSensorIds = new() };
        }

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

    partial void OnLanguageChanged(string value)
    {
        Services.LocalizationService.Instance.SetCulture(value);
        Save();
    }

    [ObservableProperty]
    private bool _isDiscovering;

    /// <summary>Hosts found by the last mDNS discovery run; bound to the host picker ComboBox.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> DiscoveredHosts => _connection.DiscoveredHosts;

    /// <summary>Recently used connection addresses; bound to the history picker ComboBox.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Remex.Core.Models.ConnectionProfile> ConnectionHistory => _connection.ConnectionHistory;

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

    private void ShowTransientStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        SavedStatus = message;

        _ = Task.Delay(3000).ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (SavedStatus == message)
                    SavedStatus = string.Empty;
            }));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Save();
        await _layoutService.FlushAsync();
        ShowTransientStatus(LocalizationService.Instance["Settings_SavedStatus"]);
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
    private void NavigateBack()
    {
        // Refresh Home's pinned sensors so changes made in Settings are immediately visible.
        if (_shell.CurrentView is HomeViewModel home)
            home.RefreshPinnedSensors();

        _shell.NavigateToHome();
    }

    [RelayCommand]
    private void ReplayTutorial()
    {
        _shell.CloseSettingsPanel();
        _shell.ReplayTutorial();
    }

    [RelayCommand]
    private async Task AddSharedFolderAsync()
    {
        if (!SupportsSharedFolderConfiguration)
            return;

        if (PickSharedFolderAsync is null)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferPickerUnavailable"]);
            return;
        }

        var folders = await PickSharedFolderAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_FileTransferPickerTitle"],
            AllowMultiple = false,
        });

        if (folders.Count == 0)
            return;

        var selectedPath = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferLocalPathUnavailable"]);
            return;
        }

        var normalizedPath = Path.GetFullPath(selectedPath);
        if (SharedRoots.Any(root => PathsEqual(root.AbsolutePath, normalizedPath)))
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferFolderExists"]);
            return;
        }

        var item = new FileTransferSharedRootItem(
            $"custom-{Guid.NewGuid():N}",
            GetSharedRootDisplayName(normalizedPath),
            normalizedPath,
            isWritable: false);

        SubscribeSharedRoot(item);
        SharedRoots.Add(item);
        OnPropertyChanged(nameof(HasSharedRoots));

        await SaveSharedRootsAsync(LocalizationService.Instance["Settings_FileTransferFolderAdded"]);
    }

    [RelayCommand]
    private async Task RestoreDefaultSharedFoldersAsync()
    {
        if (!SupportsSharedFolderConfiguration)
            return;

        try
        {
            var roots = await _fileTransferRootSettings.ResetToDefaultsAsync();
            ReplaceSharedRoots(roots);
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferDefaultsRestored"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.HostCapabilities)
            or nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.IsConnecting)
            or nameof(ConnectionViewModel.IsAutoReconnecting))
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

    private async Task LoadSharedRootsAsync()
    {
        if (!SupportsSharedFolderConfiguration)
            return;

        try
        {
            var roots = await _fileTransferRootSettings.LoadAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ReplaceSharedRoots(roots));
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message)));
        }
    }

    private void ReplaceSharedRoots(IReadOnlyList<FileTransferRootConfiguration> roots)
    {
        foreach (var existing in SharedRoots)
            UnsubscribeSharedRoot(existing);

        SharedRoots.Clear();

        foreach (var root in roots)
        {
            var item = new FileTransferSharedRootItem(root.RootId, root.DisplayName, root.AbsolutePath, root.IsWritable);
            SubscribeSharedRoot(item);
            SharedRoots.Add(item);
        }

        OnPropertyChanged(nameof(HasSharedRoots));
    }

    private void SubscribeSharedRoot(FileTransferSharedRootItem item)
    {
        item.WritableChanged += OnSharedRootWritableChanged;
        item.RemoveRequested += OnSharedRootRemoveRequested;
    }

    private void UnsubscribeSharedRoot(FileTransferSharedRootItem item)
    {
        item.WritableChanged -= OnSharedRootWritableChanged;
        item.RemoveRequested -= OnSharedRootRemoveRequested;
    }

    private async void OnSharedRootWritableChanged(object? sender, bool isWritable)
    {
        await SaveSharedRootsAsync(LocalizationService.Instance["Settings_FileTransferSaved"]);
    }

    private async void OnSharedRootRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not FileTransferSharedRootItem item)
            return;

        UnsubscribeSharedRoot(item);
        SharedRoots.Remove(item);
        OnPropertyChanged(nameof(HasSharedRoots));

        await SaveSharedRootsAsync(LocalizationService.Instance["Settings_FileTransferSaved"]);
    }

    private async Task SaveSharedRootsAsync(string successMessage)
    {
        try
        {
            await _fileTransferRootSettings.SaveAsync(SharedRoots.Select(root => new FileTransferRootConfiguration
            {
                RootId = root.RootId,
                DisplayName = root.DisplayName,
                AbsolutePath = root.AbsolutePath,
                IsWritable = root.IsWritable,
                CanRename = root.IsWritable,
                CanMove = root.IsWritable,
                CanDelete = root.IsWritable,
            }).ToList());

            ShowTransientStatus(successMessage);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static string GetSharedRootDisplayName(string absolutePath)
    {
        var trimmed = absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    // Windows Service Management was removed in RemEx 2.0 (RemEx-aep Phase 3). RemEx no longer runs
    // as a Windows Service; auto-start is an elevated Task Scheduler logon task driven by the
    // "Launch at login" toggle above (see StartupRegistrationService / autostart-remex.ps1).
    // ═══════════════ Persistence ═══════════════

    private void Save()
    {
        var updated = _profile with
        {
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = HostAddress,
            Language = Language,
            CloseToTray = IsCloseToTrayEnabled,
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

public partial class FileTransferSharedRootItem : ObservableObject
{
    public string RootId { get; }

    public string DisplayName { get; }

    public string AbsolutePath { get; }

    [ObservableProperty]
    private bool _isWritable;

    public event EventHandler<bool>? WritableChanged;
    public event EventHandler? RemoveRequested;

    public FileTransferSharedRootItem(string rootId, string displayName, string absolutePath, bool isWritable)
    {
        RootId = rootId;
        DisplayName = displayName;
        AbsolutePath = absolutePath;
        _isWritable = isWritable;
    }

    partial void OnIsWritableChanged(bool value) => WritableChanged?.Invoke(this, value);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this, EventArgs.Empty);
}

public record LanguageItem(string DisplayName, string Code);
