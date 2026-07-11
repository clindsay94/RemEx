using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Core.Messages;

namespace Remex.Desktop.ViewModels;

public partial class AppLauncherViewModel : ObservableObject, IDisposable
{
    private const string DefaultHexColor = "#4A3AFF";

    private readonly ShellViewModel _shell;
    private readonly ILauncherStorageService _storageService;
    private readonly RemexSavefileService? _savefileService;
    private readonly Action<System.Collections.Generic.List<AppEntry>> _launcherEntriesHandler;

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    private ObservableCollection<AppEntry> _launchers = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    public IEnumerable<AppEntry> FilteredApps =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Launchers
            : Launchers.Where(a => a.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredApps));

    partial void OnLaunchersChanged(ObservableCollection<AppEntry> value) => OnPropertyChanged(nameof(FilteredApps));

    public AppLauncherViewModel(ConnectionViewModel connection, ShellViewModel shell, ILauncherStorageService storageService, RemexSavefileService? savefileService = null)
    {
        Connection = connection;
        _shell = shell;
        _storageService = storageService;
        // Optional: only populated once WP-A's savefile service is registered in DI. Used solely
        // to nudge the rolling auto-snapshot after a local (unconnected) launcher save.
        _savefileService = savefileService;

        _launcherEntriesHandler = entries =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Launchers = new ObservableCollection<AppEntry>(NormalizeEntries(entries));
            });
        };
        Connection.LauncherEntriesReceived += _launcherEntriesHandler;

        _ = LoadLaunchersAsync();
    }

    private ConnectionViewModel _connection => Connection;

    private async Task LoadLaunchersAsync()
    {
        // If connected, host will sync. Fallback to local storage
        var entries = await _storageService.LoadEntriesAsync();
        Launchers = new ObservableCollection<AppEntry>(NormalizeEntries(entries));
    }

    public async Task SaveLaunchersAsync()
    {
        await _storageService.SaveEntriesAsync(Launchers);
        // Single hook point for every local (unconnected) launcher persist path — Remove, Submit
        // (Android add panel), PersistOrderAsync, and the add/edit dialogs all funnel through here.
        _savefileService?.NotifyStateChanged();
    }

    private static AppEntry NormalizeEntry(AppEntry entry)
    {
        var targetPath = NormalizeString(entry.TargetPath);
        var displayName = NormalizeString(entry.DisplayName);
        var hexColor = NormalizeString(entry.HexColor);
        var iconBase64 = NormalizeString(entry.IconBase64);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                displayName = Path.GetFileNameWithoutExtension(targetPath);
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = LocalizationService.Instance["AppLauncher_UnnamedApp"];
            }
        }

        if (string.IsNullOrWhiteSpace(hexColor) || !hexColor.StartsWith("#", StringComparison.Ordinal))
        {
            hexColor = DefaultHexColor;
        }

        if (string.IsNullOrWhiteSpace(iconBase64))
        {
            iconBase64 = null;
        }

        return new AppEntry(
            entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            displayName,
            targetPath,
            hexColor,
            iconBase64,
            entry.Order);
    }

    private static string NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    private static System.Collections.Generic.List<AppEntry> NormalizeEntries(System.Collections.Generic.IEnumerable<AppEntry> entries)
    {
        return entries
            .Select(NormalizeEntry)
            .GroupBy(e => string.IsNullOrWhiteSpace(e.TargetPath) ? e.Id.ToString() : e.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Order)
            .ToList();
    }

    [RelayCommand]
    private async Task LaunchAppAsync(AppEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.TargetPath))
            return;

        if (Connection.IsConnected)
        {
            var p = new System.Collections.Generic.Dictionary<string, string> { { "TargetPath", entry.TargetPath } };
            await Connection.SendCommandAsync("LaunchApp", p);
        }
        else
        {
            // Not connected to a remote phone: launch on THIS PC via the in-process host's launcher.
            // (Formerly forwarded over the RemExLocalIPC pipe to a separate service process.) (RemEx-aep Phase 3)
            await Remex.Desktop.Services.EmbeddedHostServiceLocator
                .Require<Remex.Core.Services.IAppLauncherService>()
                .LaunchAppAsync(entry.TargetPath);
        }
    }

    [RelayCommand]
    private async Task RemoveAppAsync(AppEntry entry)
    {
        if (entry != null && Launchers.Contains(entry))
        {
            Launchers.Remove(entry);

            if (Connection.IsConnected)
            {
                var msg = new RemexMessage { Type = MessageTypes.LauncherRemove, LauncherEntry = entry };
                var ws = Connection.GetWebSocket();
                if (ws != null) { await Remex.Core.Messages.MessageSerializer.SendAsync(ws, msg); }
            }
            else
            {
                await SaveLaunchersAsync();
            }
        }
    }

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();

    public Action<AppEntry>? OnOpenEditProgramDialogRequested { get; set; }

    [RelayCommand]
    private void EditApp(AppEntry entry) => OnOpenEditProgramDialogRequested?.Invoke(entry);

    /// <summary>
    /// Applies an edited entry in place (replace-in-place keeps the card's visual slot), then
    /// persists — <see cref="PersistOrderAsync"/> sends a <c>LauncherSync</c> when connected, else
    /// saves to disk.
    /// </summary>
    public async Task ApplyEditedEntryAsync(AppEntry updated)
    {
        var idx = -1;
        for (var i = 0; i < Launchers.Count; i++)
        {
            if (Launchers[i].Id == updated.Id)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0) return;

        Launchers[idx] = updated;
        OnPropertyChanged(nameof(FilteredApps));
        await PersistOrderAsync();
    }

    /// <summary>
    /// One-shot sort by display name — operates on the full <see cref="Launchers"/> collection
    /// regardless of any active search filter. No sticky sort mode: a later drag or arrow move
    /// persists the new manual order on top of this.
    /// </summary>
    [RelayCommand]
    private async Task SortByNameAsync(string direction)
    {
        var ordered = LauncherOrdering.SortByName(Launchers, direction);
        Launchers = new ObservableCollection<AppEntry>(ordered);
        await PersistOrderAsync();
    }

    [RelayCommand]
    private void OpenAddProgramDialog()
    {
        if (OperatingSystem.IsAndroid())
        {
            IsAndroidAddPanelOpen = !IsAndroidAddPanelOpen;
        }
        else
        {
            OnOpenAddProgramDialogRequested?.Invoke();
        }
    }

    [ObservableProperty]
    private bool _isAndroidAddPanelOpen;

    [ObservableProperty]
    private string _androidNewAppName = string.Empty;

    [ObservableProperty]
    private string _androidNewAppPath = string.Empty;

    [RelayCommand]
    private async Task SubmitAndroidNewAppAsync()
    {
        if (string.IsNullOrWhiteSpace(AndroidNewAppName) || string.IsNullOrWhiteSpace(AndroidNewAppPath))
            return;

        var entry = NormalizeEntry(new AppEntry(
            Guid.NewGuid(),
            AndroidNewAppName,
            AndroidNewAppPath,
            "#4A3AFF",
            null
        ));

        if (Connection.IsConnected)
        {
            var msg = new RemexMessage { Type = MessageTypes.LauncherAdd, LauncherEntry = entry };
            var ws = Connection.GetWebSocket();
            if (ws != null) { await Remex.Core.Messages.MessageSerializer.SendAsync(ws, msg); }
        }
        else
        {
            Launchers.Add(entry);
            await SaveLaunchersAsync();
        }

        AndroidNewAppName = string.Empty;
        AndroidNewAppPath = string.Empty;
        IsAndroidAddPanelOpen = false;
    }

    [RelayCommand]
    private async Task ReorderLauncherAsync((AppEntry source, AppEntry target) param)
    {
        var sourceIndex = Launchers.IndexOf(param.source);
        var targetIndex = Launchers.IndexOf(param.target);

        if (sourceIndex == -1 || targetIndex == -1 || sourceIndex == targetIndex)
            return;

        Launchers.Move(sourceIndex, targetIndex);
        await PersistOrderAsync();
    }

    [RelayCommand]
    private async Task MoveLeftAsync(AppEntry entry)
    {
        if (entry == null) return;
        var index = Launchers.IndexOf(entry);
        if (index <= 0) return;
        Launchers.Move(index, index - 1);
        await PersistOrderAsync();
    }

    [RelayCommand]
    private async Task MoveRightAsync(AppEntry entry)
    {
        if (entry == null) return;
        var index = Launchers.IndexOf(entry);
        if (index < 0 || index >= Launchers.Count - 1) return;
        Launchers.Move(index, index + 1);
        await PersistOrderAsync();
    }

    private async Task PersistOrderAsync()
    {
        // Update Order property for all
        var reindexed = LauncherOrdering.Reindex(Launchers);
        for (int i = 0; i < reindexed.Count; i++)
        {
            Launchers[i] = reindexed[i];
        }

        if (Connection.IsConnected)
        {
            var msg = new RemexMessage { Type = MessageTypes.LauncherSync, LauncherEntries = Launchers.ToList() };
            var ws = Connection.GetWebSocket();
            if (ws != null) { await Remex.Core.Messages.MessageSerializer.SendAsync(ws, msg); }
        }
        else
        {
            await SaveLaunchersAsync();
        }
    }

    public Action? OnOpenAddProgramDialogRequested { get; set; }

    public void Dispose()
    {
        Connection.LauncherEntriesReceived -= _launcherEntriesHandler;
    }
}
