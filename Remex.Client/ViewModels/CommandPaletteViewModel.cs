using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Models;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Ctrl+K universal command palette overlay.
/// Filters the full command list as the user types.
/// </summary>
public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly CommandPaletteEntry[] _allEntries;

    /// <summary>Raised when the palette should close (command executed or dismissed).</summary>
    public event Action? CloseRequested;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    public ObservableCollection<CommandPaletteEntry> FilteredResults { get; } = new();

    public CommandPaletteViewModel(ShellViewModel shell)
    {
        _allEntries = BuildEntries(shell);
        RefreshFilter();
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();

    private void RefreshFilter()
    {
        FilteredResults.Clear();
        var q = SearchText.Trim();
        foreach (var entry in _allEntries)
        {
            if (string.IsNullOrEmpty(q) ||
                entry.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredResults.Add(entry);
            }
        }
        IsEmpty = FilteredResults.Count == 0;
    }

    [RelayCommand]
    private void ExecuteEntry(CommandPaletteEntry? entry)
    {
        if (entry is null) return;
        if (entry.Command.CanExecute(null))
            entry.Command.Execute(null);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Dismiss() => CloseRequested?.Invoke();

    private static CommandPaletteEntry[] BuildEntries(ShellViewModel shell) =>
    [
        new("Home",                    "Navigate", shell.NavigateToHomeCommand),
        new("Sensor Canvas",           "Navigate", shell.NavigateToCanvasCommand),
        new("Remote Control",          "Navigate", shell.NavigateToRemoteCommand),
        new("App Launcher",            "Navigate", shell.NavigateToAppLauncherCommand),
        new("Task Manager",            "Navigate", shell.NavigateToTaskManagerCommand),
        new("Remote Desktop",          "Navigate", shell.NavigateToRemoteDesktopCommand),
        new("Settings",                "Navigate", shell.ToggleSettingsPanelCommand),
        new("Lock PC",                 "Power",    shell.Connection.LockCommand),
        new("Sleep PC",                "Power",    shell.Connection.SleepCommand),
        new("Disconnect",              "Connection", shell.Connection.DisconnectCommand),
        new("Connect",                 "Connection", shell.Connection.ConnectCommand),
    ];
}
