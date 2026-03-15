using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Core.Models;
using Remex.Core.Services.Network;
using Remex.Core.Services.Command;

namespace Remex.Client.ViewModels;

public partial class RemoteViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IWakeOnLanService _wolService;
    private readonly ISystemCommandService _commandService;
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
        ISystemCommandService commandService,
        DashboardLayoutService layoutService)
    {
        Connection = connection;
        _shell = shell;
        _wolService = wolService;
        _commandService = commandService;
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
        var (ok, msg) = await Connection.SendCommandAsync("Lock");
        WolStatusText = ok ? "✅ Lock sent" : $"❌ {msg}";
    }

    [RelayCommand]
    private async Task ShutdownPcAsync()
    {
        var (ok, msg) = await Connection.SendCommandAsync("Shutdown");
        WolStatusText = ok ? "✅ Shutdown sent" : $"❌ {msg}";
    }

    [RelayCommand]
    private async Task RestartPcAsync()
    {
        var (ok, msg) = await Connection.SendCommandAsync("Restart");
        WolStatusText = ok ? "✅ Restart sent" : $"❌ {msg}";
    }

    [RelayCommand]
    private async Task ForceRestartAsync()
    {
        var (ok, msg) = await Connection.SendCommandAsync("ForceRestart");
        WolStatusText = ok ? "✅ Force Restart sent" : $"❌ {msg}";
    }

    [RelayCommand]
    private async Task RestartToUefiAsync()
    {
        var (ok, msg) = await Connection.SendCommandAsync("RestartToUefi");
        WolStatusText = ok ? "✅ Restart to UEFI sent" : $"❌ {msg}";
    }

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
