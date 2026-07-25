using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

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

    /// <summary>
    /// Delegate set by <see cref="ShellViewModel.OpenCommandPalette"/> to display a confirmation
    /// dialog. Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    /// <remarks>
    /// Owned by the MAIN window, not by the palette window: the palette closes before the dialog is
    /// shown, so a dialog parented to it would lose its owner mid-flight.
    /// </remarks>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    [RelayCommand]
    private async Task ExecuteEntryAsync(CommandPaletteEntry? entry)
    {
        if (entry is null) return;
        if (!entry.Command.CanExecute(null))
        {
            CloseRequested?.Invoke();
            return;
        }

        if (!entry.RequiresConfirmation)
        {
            entry.Command.Execute(null);
            CloseRequested?.Invoke();
            return;
        }

        // Destructive entry (RemEx-eifi). Close the palette FIRST: it dismisses itself on
        // Deactivated, so leaving it open while a modal dialog takes focus would race its own
        // close against the dialog. Then confirm against the main window, and only then run.
        CloseRequested?.Invoke();

        // Fails CLOSED, matching every other confirmed action: no dialog wired means the PC is not
        // shut down, restarted, or rebooted to firmware.
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance[entry.ConfirmTitleKey!],
                LocalizationService.Instance[entry.ConfirmMessageKey!],
                LocalizationService.Instance[entry.ConfirmBtnKey!]))
        {
            return;
        }

        // Re-check before running. These commands carry no CanExecute predicate, so this reflects
        // "a previous invocation is still in flight" rather than connection state — connection loss
        // is handled inside SendCommandAsync, which returns (false, "Not connected"). It is still
        // worth keeping as a free double-fire guard now that a dialog sits in the middle.
        if (entry.Command.CanExecute(null))
            entry.Command.Execute(null);
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
        new(LocalizationService.Instance["Palette_ShutdownPc"],             LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.ShutdownCommand,      "Confirm_Shutdown_Title",      "Confirm_Shutdown_Message",      "Confirm_Shutdown_Btn"),
        new(LocalizationService.Instance["Palette_ForceShutdownPc"],       LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.ForceShutdownCommand, "Confirm_ForceShutdown_Title", "Confirm_ForceShutdown_Message", "Confirm_ForceShutdown_Btn"),
        new(LocalizationService.Instance["Palette_RestartPc"],              LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.RestartCommand,       "Confirm_Restart_Title",       "Confirm_Restart_Message",       "Confirm_Restart_Btn"),
        new(LocalizationService.Instance["Palette_RestartPcToUefi"],      LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.RestartToUefiCommand, "Confirm_RebootUefi_Title",    "Confirm_RebootUefi_Message",    "Confirm_RebootUefi_Btn"),
        new(LocalizationService.Instance["Palette_WakeOnLan"],             LocalizationService.Instance["PaletteCategory_Power"],    shell.Connection.WakeOnLanCommand),
        new(LocalizationService.Instance["Palette_Disconnect"],              LocalizationService.Instance["PaletteCategory_Connection"], shell.Connection.DisconnectCommand),
        new(LocalizationService.Instance["Palette_Connect"],                 LocalizationService.Instance["PaletteCategory_Connection"], shell.Connection.ConnectCommand),
    ];
}
