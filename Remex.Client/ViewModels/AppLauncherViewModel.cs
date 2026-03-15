using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the App Launcher page.
/// Will provide remote application launching capabilities.
/// </summary>
public partial class AppLauncherViewModel : ObservableObject
{
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

        _ = LoadLaunchersAsync();
    }

    private async Task LoadLaunchersAsync()
    {
        var entries = await _storageService.LoadEntriesAsync();
        Launchers = new ObservableCollection<AppEntry>(entries);
    }

    public async Task SaveLaunchersAsync()
    {
        await _storageService.SaveEntriesAsync(Launchers);
    }

    [RelayCommand]
    private async Task LaunchAppAsync(AppEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.TargetPath))
            return;

        // If not connected to a remote host, we might just run it locally (Server Mode equivalent)
        // But assuming we are using IPC to trigger it on the background service.
        var request = new CommandRequest("LaunchApp", entry.TargetPath);
        var response = await RemExLocalIPC.SendCommandAsync(request);

        // You could show a notification here based on response.Success
    }

    [RelayCommand]
    private async Task RemoveAppAsync(AppEntry entry)
    {
        if (entry != null && Launchers.Contains(entry))
        {
            Launchers.Remove(entry);
            try
            {
                await SaveLaunchersAsync();
            }
            catch (Exception)
            {
                // TODO: Log error and notify user that saving failed.
            }
        }
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();

    [RelayCommand]
    private void OpenAddProgramDialog()
    {
        // Handled in the view code-behind, or we can use an Action to open the dialog
        OnOpenAddProgramDialogRequested?.Invoke();
    }

    public Action? OnOpenAddProgramDialogRequested { get; set; }
}
