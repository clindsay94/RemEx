using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services.Network;
using Remex.Core.Validation;

namespace Remex.Client.ViewModels;

public partial class RemoteViewModel : ObservableValidator, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IWakeOnLanService _wolService;
    private readonly DashboardLayoutService _layoutService;
    private DashboardProfile _profile = new();

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [ValidMacAddress]
    private string _wolMacAddress = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [ValidIpAddress]
    private string _wolBroadcastIp = "255.255.255.255";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [ValidPort]
    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535")]
    private int _wolPort = 9;

    [ObservableProperty]
    private string _wolStatusText = string.Empty;

    public RemoteViewModel(
        ConnectionViewModel connection,
        ShellViewModel shell,
        IWakeOnLanService wolService,
        DashboardLayoutService layoutService)
    {
        Connection = Guard.NotNull(connection);
        _shell = Guard.NotNull(shell);
        _wolService = Guard.NotNull(wolService);
        _layoutService = Guard.NotNull(layoutService);

        _ = LoadWolConfigAsync();
    }

    private async Task LoadWolConfigAsync()
    {
        _profile = await _layoutService.LoadAsync();
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
        // Validate inputs before sending WOL packet
        ValidateAllProperties();
        if (HasErrors)
        {
            var macErrors = GetErrors(nameof(WolMacAddress)).Cast<ValidationResult>().Select(e => e.ErrorMessage).FirstOrDefault();
            var ipErrors = GetErrors(nameof(WolBroadcastIp)).Cast<ValidationResult>().Select(e => e.ErrorMessage).FirstOrDefault();
            var portErrors = GetErrors(nameof(WolPort)).Cast<ValidationResult>().Select(e => e.ErrorMessage).FirstOrDefault();

            WolStatusText = macErrors ?? ipErrors ?? portErrors ?? "Invalid WOL settings";
            return;
        }

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


    /// <summary>
    /// Delegate set by the View to display a confirmation dialog.
    /// Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    private async Task<bool> ConfirmAsync(string titleKey, string messageKey, string btnKey)
    {
        if (OnConfirmationRequested is null)
            return true;
        return await OnConfirmationRequested(
            LocalizationService.Instance[titleKey],
            LocalizationService.Instance[messageKey],
            LocalizationService.Instance[btnKey]);
    }

    [RelayCommand]
    private async Task LockPcAsync()
    {
        await ExecuteRemoteCommandAsync("Lock", LocalizationService.Instance["Wol_LockSent"]);
    }

    [RelayCommand]
    private async Task ShutdownPcAsync()
    {
        if (!await ConfirmAsync("Confirm_Shutdown_Title", "Confirm_Shutdown_Message", "Confirm_Shutdown_Btn"))
            return;
        await ExecuteRemoteCommandAsync("Shutdown", LocalizationService.Instance["Wol_ShutdownSent"]);
    }

    [RelayCommand]
    private async Task ForceShutdownPcAsync()
    {
        if (!await ConfirmAsync("Confirm_ForceShutdown_Title", "Confirm_ForceShutdown_Message", "Confirm_ForceShutdown_Btn"))
            return;
        await ExecuteRemoteCommandAsync("ForceShutdown", LocalizationService.Instance["Wol_ForceShutdownSent"]);
    }

    [RelayCommand]
    private async Task RestartPcAsync()
    {
        if (!await ConfirmAsync("Confirm_Restart_Title", "Confirm_Restart_Message", "Confirm_Restart_Btn"))
            return;
        await ExecuteRemoteCommandAsync("Restart", LocalizationService.Instance["Wol_RestartSent"]);
    }

    [RelayCommand]
    private async Task ForceRestartAsync()
    {
        if (!await ConfirmAsync("Confirm_ForceRestart_Title", "Confirm_ForceRestart_Message", "Confirm_ForceRestart_Btn"))
            return;
        await ExecuteRemoteCommandAsync("ForceRestart", LocalizationService.Instance["Wol_ForceRestartSent"]);
    }

    [RelayCommand]
    private async Task RestartToUefiAsync()
    {
        if (!await ConfirmAsync("Confirm_RebootUefi_Title", "Confirm_RebootUefi_Message", "Confirm_RebootUefi_Btn"))
            return;
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

    public void Dispose()
    {
        // No resources to dispose currently, but implementing IDisposable for consistency
        // in the ViewModel disposal hierarchy
    }
}
