using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Services;
using Remex.Core.Messages;

namespace Remex.Client.ViewModels;

public partial class AppLauncherViewModel : ObservableObject
{
    private const string DefaultHexColor = "#4A3AFF";

    private readonly ShellViewModel _shell;
    private readonly ILauncherStorageService _storageService;

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    private ObservableCollection<AppEntry> _launchers = new();

    public AppLauncherViewModel(ConnectionViewModel connection, ShellViewModel shell, ILauncherStorageService storageService)
    {
        Connection = connection;
        _shell = shell;
        _storageService = storageService;

        Connection.LauncherEntriesReceived += entries =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Launchers = new ObservableCollection<AppEntry>(NormalizeEntries(entries));
            });
        };

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
                displayName = "Unnamed App";
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
            iconBase64);
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
            var request = new CommandRequest("LaunchApp", new System.Collections.Generic.Dictionary<string, string>
            {
                ["TargetPath"] = entry.TargetPath,
            });
            await RemExLocalIPC.SendCommandAsync(request);
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

    public Action? OnOpenAddProgramDialogRequested { get; set; }
}
