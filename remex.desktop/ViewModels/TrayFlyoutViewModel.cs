using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>One button in the tray flyout's action grid.</summary>
public sealed record TrayTile
{
    public required string Label { get; init; }

    /// <summary>Resolved once at build time, not looked up per render.</summary>
    public Geometry? Icon { get; init; }

    public required ICommand Command { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? DisabledTooltip { get; init; }
    public bool HasSubmenu { get; init; }
}

/// <summary>
/// The tray flyout's own view model.
/// </summary>
/// <remarks>
/// COMPOSES, DOES NOT REIMPLEMENT. Presence comes from the one polling singleton every other
/// indicator reads; power commands are <c>ConnectionViewModel</c>'s existing
/// <c>[RelayCommand]</c>s; the sensors are <c>HomeViewModel</c>'s list, not a second copy.
/// <para>
/// IT IS A SEPARATE CLASS FROM <see cref="HomeViewModel"/> for a reason. The flyout previously used
/// <c>HomeViewModel</c> as its data context, which is why it had telemetry and no actions — that
/// view model has no commands to offer. Adding power commands there would couple the Home page to
/// <c>ConnectionViewModel</c> for a different surface's benefit, and would make the tile set
/// impossible to test without standing up the whole dashboard.
/// </para>
/// <para>
/// EVERY ACTION HERE TARGETS THIS PC. RemEx has no PC-to-phone command channel — there is no
/// message type in <c>remex.core</c> that could carry one (see RemEx-uov9y). Do not add a tile
/// whose label implies the phone is being controlled.
/// </para>
/// </remarks>
public sealed partial class TrayFlyoutViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly HomeViewModel _home;

    /// <summary>Supplied by the view; see <c>ConfirmationDialogHost</c>. Title, message, button.</summary>
    /// <remarks>
    /// Null means "cannot confirm", and every caller must read that as "do not proceed" — the same
    /// contract every other destructive command in this app follows (RemEx-07jx).
    /// </remarks>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    public PhonePresenceMonitor Presence => PhonePresenceMonitor.Instance;

    /// <summary>Exposed because the status strip binds <c>Connection.StatusText</c>.</summary>
    public ConnectionViewModel Connection => _shell.Connection;

    public ObservableCollection<SensorViewModel> PinnedSensors => _home.PinnedSensors;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private IReadOnlyList<TrayTile> _tiles = [];

    public TrayFlyoutViewModel(ShellViewModel shell, HomeViewModel home)
    {
        _shell = shell;
        _home = home;

        // Rebuild when phone presence changes, so the Remote tile enables and disables in place
        // rather than at the next time the flyout happens to be reopened.
        Presence.PropertyChanged += OnPresenceChanged;

        RebuildTiles();
    }

    /// <summary>Refreshes everything the flyout shows. Called each time it is about to be shown.</summary>
    public void Refresh()
    {
        _home.RefreshPinnedSensors();
        Presence.Refresh();
        RebuildTiles();
    }

    private void OnPresenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhonePresenceMonitor.IsPhoneAttached))
            RebuildTiles();
    }

    private void RebuildTiles()
    {
        var remoteEnabled = TrayTileRules.IsRemoteDesktopEnabled(Presence.IsPhoneAttached);

        Tiles =
        [
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Lock"],
                Icon = FindIcon("IconLock"),
                Command = _shell.Connection.LockCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Sleep"],
                Icon = FindIcon("IconMoon"),
                Command = _shell.Connection.SleepCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_RemoteDesktop"],
                Icon = FindIcon("IconRemote"),
                Command = OpenRemoteDesktopCommand,
                IsEnabled = remoteEnabled,
                DisabledTooltip = remoteEnabled
                    ? null
                    : LocalizationService.Instance["Tray_Disabled_NeedsPhone"],
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_SendFile"],
                Icon = FindIcon("IconUpload"),
                Command = OpenTransfersCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Btn_Pair"],
                Icon = FindIcon("IconConnection"),
                // Always enabled: RemEx supports several paired devices, so gating this on
                // "already paired" would block adding a second phone.
                Command = OpenPairingCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["PaletteCategory_Power"],
                Icon = FindIcon("IconPower"),
                Command = NoOpCommand,
                HasSubmenu = true,
            },
        ];
    }

    /// <summary>
    /// Resolves an icon geometry from the application's resources, tolerating a missing key.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing. A typo'd or removed icon key should cost a tile its
    /// glyph, not take down the tray flyout — the label alone still says what the tile does.
    /// <para>
    /// Same shape as <c>ThemeResources.TryGet</c>, and for the reason recorded there:
    /// <c>TryFindResource</c> does not exist on <see cref="Application"/> in this Avalonia version,
    /// so the lookup goes through <c>TryGetResource</c> with the active theme variant.
    /// </para>
    /// </remarks>
    private static Geometry? FindIcon(string key)
    {
        var app = Application.Current;
        if (app is null)
            return null;

        return app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as Geometry : null;
    }

    [RelayCommand]
    private void OpenRemoteDesktop() => _shell.NavigateToRemoteDesktop();

    [RelayCommand]
    private void OpenTransfers() => _shell.NavigateToFileTransfer();

    [RelayCommand]
    private void OpenPairing() => _shell.NavigateToSettings();

    /// <summary>The Power tile opens a submenu from the view; the tile's own command does nothing.</summary>
    [RelayCommand]
    private void NoOp() { }

    /// <summary>The localized label for a power action, used by the submenu and the confirm dialog.</summary>
    public static string PowerLabel(TrayPowerAction action) => LocalizationService.Instance[action switch
    {
        TrayPowerAction.Restart => "Confirm_Restart_Btn",
        TrayPowerAction.Shutdown => "Confirm_Shutdown_Btn",
        TrayPowerAction.SignOut => "Confirm_SignOut_Btn",
        TrayPowerAction.Hibernate => "Tray_Power_Hibernate",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No label for this power action."),
    }];

    [RelayCommand]
    private async Task InvokePowerAsync(TrayPowerAction action)
    {
        // The confirm-then-execute decision lives in TrayPowerInvoker so it can be unit tested;
        // this method only supplies the two delegates.
        await TrayPowerInvoker.InvokeAsync(
            action,
            confirm: OnConfirmationRequested is null ? null : ConfirmAsync,
            execute: a => a switch
            {
                TrayPowerAction.Restart => _shell.Connection.RestartAsync(),
                TrayPowerAction.Shutdown => _shell.Connection.ShutdownAsync(),
                TrayPowerAction.SignOut => _shell.Connection.SignOutAsync(),
                TrayPowerAction.Hibernate => _shell.Connection.HibernateAsync(),
                _ => Task.CompletedTask,
            });
    }

    private Task<bool> ConfirmAsync(TrayPowerAction action)
    {
        var (title, message, button) = ConfirmKeys(action);
        return OnConfirmationRequested!(
            LocalizationService.Instance[title],
            LocalizationService.Instance[message],
            LocalizationService.Instance[button]);
    }

    /// <summary>
    /// The existing confirmation strings for a session-ending action.
    /// </summary>
    /// <remarks>
    /// Hibernate has no arm on purpose, and the default THROWS rather than inventing a string.
    /// <see cref="TrayTileRules.RequiresConfirmation"/> returns <c>false</c> for it, so reaching
    /// here with Hibernate means the policy and this table have gone out of step — which is worth a
    /// loud failure, not a dialog asking the user to confirm something the design says is
    /// recoverable. Note there is deliberately no <c>Confirm_Hibernate_Message</c> resource; adding
    /// one would be nine translations of a string nothing can display.
    /// </remarks>
    private static (string Title, string Message, string Button) ConfirmKeys(TrayPowerAction action) => action switch
    {
        TrayPowerAction.Restart => ("Confirm_Restart_Title", "Confirm_Restart_Message", "Confirm_Restart_Btn"),
        TrayPowerAction.Shutdown => ("Confirm_Shutdown_Title", "Confirm_Shutdown_Message", "Confirm_Shutdown_Btn"),
        TrayPowerAction.SignOut => ("Confirm_SignOut_Title", "Confirm_SignOut_Message", "Confirm_SignOut_Btn"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "This action is not confirmed - see TrayTileRules.RequiresConfirmation."),
    };
}
