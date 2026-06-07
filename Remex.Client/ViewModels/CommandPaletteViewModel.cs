using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Models;
using Remex.Client.Services;

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
        new(LocalizationService.Instance["Palette_Home"],                    LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToHomeCommand),
        new(LocalizationService.Instance["Palette_SensorCanvas"],           LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToCanvasCommand),
        new(LocalizationService.Instance["Palette_RemoteControl"],          LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToRemoteCommand),
        new(LocalizationService.Instance["Palette_AppLauncher"],            LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToAppLauncherCommand),
        new(LocalizationService.Instance["Palette_TaskManager"],            LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToTaskManagerCommand),
        new(LocalizationService.Instance["Palette_RemoteDesktop"],          LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToRemoteDesktopCommand),
        new(LocalizationService.Instance["Palette_FileTransfer"],           LocalizationService.Instance["PaletteCategory_Navigate"], shell.NavigateToFileTransferCommand),
        new(LocalizationService.Instance["Palette_Settings"],                LocalizationService.Instance["PaletteCategory_Navigate"], shell.ToggleSettingsPanelCommand),
        new(LocalizationService.Instance["Palette_ToggleSettingsPanel"],   LocalizationService.Instance["PaletteCategory_Interface"], shell.ToggleSettingsPanelCommand),
        new(LocalizationService.Instance["Palette_CloseSettingsPanel"],    LocalizationService.Instance["PaletteCategory_Interface"], shell.CloseSettingsPanelCommand),
        new(LocalizationService.Instance["Palette_ToggleNavigationDrawer"],LocalizationService.Instance["PaletteCategory_Interface"], shell.ToggleDrawerCommand),
        new(LocalizationService.Instance["Palette_UndoCanvasEdit"],        LocalizationService.Instance["PaletteCategory_Canvas"],   shell.CanvasUndoCommand),
        new(LocalizationService.Instance["Palette_RedoCanvasEdit"],        LocalizationService.Instance["PaletteCategory_Canvas"],   shell.CanvasRedoCommand),
        new(LocalizationService.Instance["Palette_DismissConnectionBanner"],LocalizationService.Instance["PaletteCategory_Status"],   shell.DismissConnectionBannerCommand),
        new(LocalizationService.Instance["Palette_AboutWhatsNew"],      LocalizationService.Instance["PaletteCategory_Help"],     shell.NavigateToAboutCommand),
        new(LocalizationService.Instance["Palette_TutorialGlossary"],     LocalizationService.Instance["PaletteCategory_Help"],     shell.ReplayTutorialCommand),
        new(LocalizationService.Instance["Palette_LockPc"],                 LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.LockCommand),
        new(LocalizationService.Instance["Palette_SleepPc"],                LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.SleepCommand),
        new(LocalizationService.Instance["Palette_HibernatePc"],            LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.HibernateCommand),
        new(LocalizationService.Instance["Palette_SignOutPc"],             LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.SignOutCommand),
        new(LocalizationService.Instance["Palette_ShutdownPc"],             LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.ShutdownCommand),
        new(LocalizationService.Instance["Palette_ForceShutdownPc"],       LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.ForceShutdownCommand),
        new(LocalizationService.Instance["Palette_RestartPc"],              LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.RestartCommand),
        new(LocalizationService.Instance["Palette_RestartPcToUefi"],      LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.RestartToUefiCommand),
        new(LocalizationService.Instance["Palette_WakeOnLan"],             LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.WakeOnLanCommand),
        new(LocalizationService.Instance["Palette_Disconnect"],              LocalizationService.Instance["PaletteCategory_Connection"], shell.Connection.DisconnectCommand),
        new(LocalizationService.Instance["Palette_Connect"],                 LocalizationService.Instance["PaletteCategory_Connection"], shell.Connection.ConnectCommand),
    ];
}
