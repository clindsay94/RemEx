using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Core.Models;
using Remex.Core.Services.Network;

namespace Remex.Client.ViewModels;

public partial class RemoteViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IWakeOnLanService _wolService;
    private readonly DashboardLayoutService _layoutService;
    private DashboardProfile _profile = new();

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    private string _wolMacAddress = string.Empty;

    [ObservableProperty]
    private string _wolBroadcastIp = "255.255.255.255";

    [ObservableProperty]
    private int _wolPort = 9;

    [ObservableProperty]
    private string _wolStatusText = string.Empty;

    public RemoteViewModel(
        ConnectionViewModel connection,
        ShellViewModel shell,
        IWakeOnLanService wolService,
        DashboardLayoutService layoutService)
    {
        Connection = connection;
        _shell = shell;
        _wolService = wolService;
        _layoutService = layoutService;

        _ = LoadWolConfigAsync();
    }

    private async Task LoadWolConfigAsync()
    {
        _profile = await _layoutService.LoadAsync().ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            WolMacAddress = _profile.WolMacAddress;
            WolBroadcastIp = string.IsNullOrWhiteSpace(_profile.WolBroadcastIp)
                ? "255.255.255.255" : _profile.WolBroadcastIp;
            WolPort = _profile.WolPort > 0 ? _profile.WolPort : 9;
        });
    }

    partial void OnWolMacAddressChanged(string value) => SaveWolConfig();
    partial void OnWolBroadcastIpChanged(string value) => SaveWolConfig();
    partial void OnWolPortChanged(int value) => SaveWolConfig();

    private void SaveWolConfig()
    {
        var updated = _profile with
        {
            WolMacAddress = WolMacAddress,
            WolBroadcastIp = WolBroadcastIp,
            WolPort = WolPort,
        };
        _profile = updated;
        _layoutService.RequestSave(updated);
    }

        [RelayCommand]
    private async Task SendWolAsync()
    {
        if (string.IsNullOrWhiteSpace(WolMacAddress))
        {
            WolStatusText = "⚠ Enter a MAC address first.";
            return;
        }

        try
        {
            if (Connection.IsConnected)
            {
                WolStatusText = "Sending magic packet via host…";
                var (ok, msg) = await Connection.SendCommandAsync("WakeOnLan",
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "MacAddress", WolMacAddress },
                        { "BroadcastIp", WolBroadcastIp },
                        { "Port", WolPort.ToString() },
                    });
                WolStatusText = ok ? $"✅ {msg}" : $"❌ {msg}";
            }
            else
            {
                WolStatusText = "Sending magic packet locally…";
                await _wolService.WakeAsync(WolMacAddress, WolBroadcastIp, WolPort);
                WolStatusText = $"✅ Packet sent to {WolMacAddress}";
            }
        }
        catch (Exception ex)
        {
            WolStatusText = $"❌ Failed: {ex.Message}";
        }
    }


    [RelayCommand]
    private async Task LockPcAsync()
    {
        await ExecuteRemoteCommandAsync("Lock", "Lock sent");
    }

    [RelayCommand]
    private async Task ShutdownPcAsync()
    {
        await ExecuteRemoteCommandAsync("Shutdown", "Shutdown sent");
    }

    [RelayCommand]
    private async Task ForceShutdownPcAsync()
    {
        await ExecuteRemoteCommandAsync("ForceShutdown", "Force shutdown sent");
    }

    [RelayCommand]
    private async Task RestartPcAsync()
    {
        await ExecuteRemoteCommandAsync("Restart", "Restart sent");
    }

    [RelayCommand]
    private async Task ForceRestartAsync()
    {
        await ExecuteRemoteCommandAsync("ForceRestart", "Force restart sent");
    }

    [RelayCommand]
    private async Task RestartToUefiAsync()
    {
        await ExecuteRemoteCommandAsync("RestartToUefi", "Restart to UEFI sent");
    }

    [RelayCommand]
    private async Task SleepPcAsync()
    {
        await ExecuteRemoteCommandAsync("Sleep", "Sleep sent");
    }

    [RelayCommand]
    private async Task HibernatePcAsync()
    {
        await ExecuteRemoteCommandAsync("Hibernate", "Hibernate sent");
    }

    [RelayCommand]
    private async Task SignOutPcAsync()
    {
        await ExecuteRemoteCommandAsync("SignOut", "Sign out sent");
    }

    private async Task ExecuteRemoteCommandAsync(string action, string successMessage)
    {
        var (ok, msg) = await Connection.SendCommandAsync(action);
        WolStatusText = ok ? $"✅ {successMessage}" : $"❌ {msg}";
    }

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
