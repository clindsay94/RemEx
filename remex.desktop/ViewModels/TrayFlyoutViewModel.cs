using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>One button in the tray flyout's action grid.</summary>
public sealed record TrayTile
{
    public required string Label { get; init; }

    /// <summary>
    /// The glyph, named rather than drawn. A <see cref="MaterialIconKind"/> is a value the tile
    /// carries directly, so unlike the geometry this replaced it needs no resource lookup and
    /// cannot silently come back null when a key is missing (RemEx-wyx2c).
    /// </summary>
    public required MaterialIconKind Icon { get; init; }

    public required ICommand Command { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? DisabledTooltip { get; init; }
    public bool HasSubmenu { get; init; }

    /// <summary>
    /// Whether clicking this tile should also raise the main window.
    /// </summary>
    /// <remarks>
    /// The navigating tiles need it and the power tiles must not have it. RemEx closes to the tray
    /// by default, so a tile that only tells <c>ShellViewModel</c> to change page changes a page
    /// nobody can see — the click reads as doing nothing. Lock and Sleep are the opposite case:
    /// popping the window up as the screen locks would be absurd. The view acts on this rather than
    /// the view model, because raising a window is not a view model's to do (RemEx-07jx).
    /// </remarks>
    public bool OpensMainWindow { get; init; }
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
public sealed partial class TrayFlyoutViewModel : ObservableObject, IDisposable
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

    /// <summary>
    /// The literal key for <see cref="PinTooltip"/>. Tray_Pin / Tray_Unpin were translated into
    /// all nine locales in 689b61b but never bound to anything, so the pin toggle had no tooltip
    /// in any language until this property wired them up.
    /// </summary>
    public static string PinTooltipKey(bool isPinned) => isPinned ? "Tray_Unpin" : "Tray_Pin";

    public string PinTooltip => LocalizationService.Instance[PinTooltipKey(IsPinned)];

    partial void OnIsPinnedChanged(bool value) => OnPropertyChanged(nameof(PinTooltip));

    [ObservableProperty]
    private IReadOnlyList<TrayTile> _tiles = [];

    /// <summary>
    /// Online paired devices, shown as the presence badge's content (RemEx-rjnbo.1).
    /// <see cref="IPairedDeviceSource"/> already exists for <c>SettingsViewModel</c>'s paired-device
    /// list; this resolves the same source rather than inventing a second count.
    /// </summary>
    [ObservableProperty]
    private int _onlineDeviceCount;

    /// <summary>
    /// Whether the presence badge should show <see cref="OnlineDeviceCount"/> as content. The
    /// presence dot itself (<c>Classes="presence"</c>, coloured by <c>Classes.connected</c>) stays
    /// visible either way - unlike the Files/Diagnostics badges, this one is never fully hidden at
    /// zero, because it doubles as the "is anything attached at all" indicator.
    /// </summary>
    public bool HasOnlineDevices => OnlineDeviceCount > 0;

    partial void OnOnlineDeviceCountChanged(int value) => OnPropertyChanged(nameof(HasOnlineDevices));

    public TrayFlyoutViewModel(ShellViewModel shell, HomeViewModel home)
    {
        _shell = shell;
        _home = home;

        // Rebuild when phone presence changes, so the Remote tile enables and disables in place
        // rather than at the next time the flyout happens to be reopened. Also refresh the badge's
        // OnlineDeviceCount on every presence signal, not only IsPhoneAttached (review, MEDIUM,
        // RemEx-rjnbo.1): RebuildTiles previously only ran on show / IsPhoneAttached / locale, and a
        // PINNED flyout is never re-shown (:126), so the count froze the moment a second paired
        // device changed state without flipping IsPhoneAttached itself. Both handlers subscribe to
        // the same PhonePresenceMonitor.PropertyChanged event that already drives IsPhoneAttached -
        // no dispatcher marshalling here, matching every other Presence handler in this file and
        // ShellViewModel's, because the poll that raises it already runs on the UI thread.
        Presence.PropertyChanged += OnPresenceChanged;
        Presence.PropertyChanged += OnPresenceChangedForOnlineDeviceCount;

        // And rebuild on a language switch. Every tile label is a snapshot taken in RebuildTiles,
        // and a PINNED flyout is never re-shown - so without this it keeps the previous language
        // until the app restarts, sitting next to a shell and a tray menu that both changed.
        // PhonePresenceMonitor and the tray menu subscribe for the same reason.
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        RebuildTiles();
    }

    /// <summary>Refreshes everything the flyout shows. Called each time it is about to be shown.</summary>
    public void Refresh()
    {
        _home.RefreshPinnedSensors();
        Presence.Refresh();
        RebuildTiles();
    }

    /// <summary>
    /// Detaches both subscriptions above. This view model is registered with
    /// <c>AddSingleton</c> (App.axaml.cs), so in practice the standard DI container calls this once,
    /// at process shutdown, when it disposes its singletons — there is no earlier point in this
    /// view model's life where detaching would be correct. It exists now (rather than the previous
    /// "neither unsubscribe is needed" reasoning) because <see cref="PinTooltip"/> made this the
    /// first computed property here that <c>LocalizedPropertyRefreshTests</c> can see, and that test
    /// requires a matching <c>-=</c> in the file regardless of the singleton's actual lifetime.
    /// </summary>
    public void Dispose()
    {
        Presence.PropertyChanged -= OnPresenceChanged;
        Presence.PropertyChanged -= OnPresenceChangedForOnlineDeviceCount;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }

    private void OnPresenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhonePresenceMonitor.IsPhoneAttached))
            RebuildTiles();
    }

    /// <summary>
    /// Keeps <see cref="OnlineDeviceCount"/> live for a PINNED flyout (review, MEDIUM, RemEx-rjnbo.1).
    /// </summary>
    /// <remarks>
    /// DELIBERATELY SEPARATE FROM <see cref="OnPresenceChanged"/> AND FROM <see cref="RebuildTiles"/>.
    /// A pinned flyout is shown once and never rebuilt again, so the only way its count can move is a
    /// handler that runs off the presence signal directly rather than off the next call to
    /// <see cref="Refresh"/>. It refreshes only the count, not the whole tile set, so a test can pin
    /// that this path runs without RebuildTiles running a second time alongside it. Unfiltered on
    /// <c>e.PropertyName</c> on purpose: <see cref="IPairedDeviceSource"/> has no change event of its
    /// own (RemEx-rjnbo.1's own remarks), so every presence tick is the closest live signal there is
    /// that a paired device's online state may have moved even when <c>IsPhoneAttached</c> itself did
    /// not flip.
    /// </remarks>
    private void OnPresenceChangedForOnlineDeviceCount(object? sender, PropertyChangedEventArgs e) =>
        RefreshOnlineDeviceCount();

    /// <summary>The <see cref="OnlineDeviceCount"/> half of <see cref="RebuildTiles"/>, split out so
    /// it can run on its own from a live presence signal without rebuilding the whole tile set.</summary>
    private void RefreshOnlineDeviceCount()
    {
        // Resolved on every call rather than cached (RemEx-rjnbo.1, same reasoning as
        // SettingsViewModel.RefreshPairedDevices): the embedded host publishes its container after
        // it starts, and this view model can be built first.
        OnlineDeviceCount = EmbeddedHostServiceLocator.TryResolve<IPairedDeviceSource>()?
            .PairedDevices().Count(device => device.IsOnline) ?? 0;
    }

    /// <summary>
    /// Re-reads every tile label after a language switch.
    /// </summary>
    /// <remarks>
    /// Unfiltered on purpose. <c>LocalizationService.SetCulture</c> raises three indexer-shaped
    /// names — <c>Item</c>, <c>Item[]</c> and <c>string.Empty</c> — and no real property name, so
    /// there is nothing to match on and this runs three times per language switch. That is
    /// accepted rather than overlooked: it is six resource lookups, three times, on an action a
    /// user takes about once. Matching one of the three by string would be cheaper and would break
    /// silently the day someone tidies that list.
    /// </remarks>
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => RebuildTiles();

    private void RebuildTiles()
    {
        var remoteEnabled = TrayTileRules.IsRemoteDesktopEnabled(Presence.IsPhoneAttached);

        RefreshOnlineDeviceCount();

        Tiles =
        [
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Lock"],
                Icon = MaterialIconKind.Lock,
                Command = _shell.Connection.LockCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Sleep"],
                Icon = MaterialIconKind.WeatherNight,
                Command = _shell.Connection.SleepCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_RemoteDesktop"],
                Icon = MaterialIconKind.Monitor,
                Command = OpenRemoteDesktopCommand,
                OpensMainWindow = true,
                IsEnabled = remoteEnabled,
                DisabledTooltip = remoteEnabled
                    ? null
                    : LocalizationService.Instance["Tray_Disabled_NeedsPhone"],
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_SendFile"],
                Icon = MaterialIconKind.Upload,
                Command = OpenTransfersCommand,
                OpensMainWindow = true,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Btn_Pair"],
                Icon = MaterialIconKind.LinkVariant,
                // Always enabled: RemEx supports several paired devices, so gating this on
                // "already paired" would block adding a second phone.
                Command = OpenPairingCommand,
                OpensMainWindow = true,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["PaletteCategory_Power"],
                Icon = MaterialIconKind.Power,
                Command = NoOpCommand,
                HasSubmenu = true,
            },
        ];

        OnPropertyChanged(nameof(PinTooltip));
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
