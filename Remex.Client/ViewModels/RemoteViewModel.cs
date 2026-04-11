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
            WolStatusText = LocalizationService.Instance["Wol_EnterMac"];
            return;
        }

        try
        {
            if (Connection.IsConnected)
            {
                WolStatusText = LocalizationService.Instance["Wol_SendingViaHost"];
                var (ok, msg) = await Connection.SendCommandAsync("WakeOnLan",
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "MacAddress", WolMacAddress },
                        { "BroadcastIp", WolBroadcastIp },
                        { "Port", WolPort.ToString() },
                    });
                WolStatusText = ok
                    ? string.Format(LocalizationService.Instance["Wol_SuccessFormat"], msg)
                    : string.Format(LocalizationService.Instance["Wol_ErrorFormat"], msg);
            }
            else
            {
                WolStatusText = LocalizationService.Instance["Wol_SendingLocal"];
                await _wolService.WakeAsync(WolMacAddress, WolBroadcastIp, WolPort);
                WolStatusText = string.Format(LocalizationService.Instance["Wol_SentToFormat"], WolMacAddress);
            }
        }
        catch (Exception ex)
        {
            WolStatusText = string.Format(LocalizationService.Instance["Wol_ErrorFormat"], ex.Message);
        }
    }


    [RelayCommand]
    private async Task LockPcAsync()
    {
        await ExecuteRemoteCommandAsync("Lock", LocalizationService.Instance["Wol_LockSent"]);
    }

    [RelayCommand]
    private async Task ShutdownPcAsync()
    {
        await ExecuteRemoteCommandAsync("Shutdown", LocalizationService.Instance["Wol_ShutdownSent"]);
    }

    [RelayCommand]
    private async Task ForceShutdownPcAsync()
    {
        await ExecuteRemoteCommandAsync("ForceShutdown", LocalizationService.Instance["Wol_ForceShutdownSent"]);
    }

    [RelayCommand]
    private async Task RestartPcAsync()
    {
        await ExecuteRemoteCommandAsync("Restart", LocalizationService.Instance["Wol_RestartSent"]);
    }

    [RelayCommand]
    private async Task ForceRestartAsync()
    {
        await ExecuteRemoteCommandAsync("ForceRestart", LocalizationService.Instance["Wol_ForceRestartSent"]);
    }

    [RelayCommand]
    private async Task RestartToUefiAsync()
    {
        await ExecuteRemoteCommandAsync("RestartToUefi", LocalizationService.Instance["Wol_RebootUefiSent"]);
    }

    [RelayCommand]
    private async Task SleepPcAsync()
    {
        await ExecuteRemoteCommandAsync("Sleep", LocalizationService.Instance["Wol_SleepSent"]);
    }

    [RelayCommand]
    private async Task HibernatePcAsync()
    {
        await ExecuteRemoteCommandAsync("Hibernate", LocalizationService.Instance["Wol_HibernateSent"]);
    }

    [RelayCommand]
    private async Task SignOutPcAsync()
    {
        await ExecuteRemoteCommandAsync("SignOut", LocalizationService.Instance["Wol_SignOutSent"]);
    }

    private async Task ExecuteRemoteCommandAsync(string action, string successMessage)
    {
        var (ok, msg) = await Connection.SendCommandAsync(action);
        WolStatusText = ok
            ? string.Format(LocalizationService.Instance["Wol_SuccessFormat"], successMessage)
            : string.Format(LocalizationService.Instance["Wol_ErrorFormat"], msg);
    }

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
