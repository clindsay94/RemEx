using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>One entry in the tray flyout's toolbar row: a tile, a divider, or an app shortcut.</summary>
/// <remarks>
/// A CLOSED HIERARCHY BY DESIGN (Flyout D2 .1, RemEx-4kv0g.18.5). <c>ToolbarItems</c> is one list of
/// this base type so the row's <c>ItemsControl</c> can pick the right shape per item through
/// Avalonia's <c>DataTemplates</c>/<c>x:DataType</c> matching — no <c>if</c>s in XAML, and no second
/// list to keep in sync with the first.
/// </remarks>
public abstract record TrayToolbarItem;

/// <summary>One button in the tray flyout's toolbar row.</summary>
public sealed record TrayTile : TrayToolbarItem
{
    /// <summary>
    /// Stable identifier — <c>lock</c>, <c>sleep</c>, <c>remote</c>, <c>send</c>, <c>pair</c> or
    /// <c>power</c> — independent of <see cref="Label"/> so a hidden-tile setting
    /// (<c>CustomizationSettings.FlyoutHiddenTileIds</c>) survives a language switch.
    /// </summary>
    public required string Id { get; init; }

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

/// <summary>Separates the six action tiles from the app shortcuts that follow them, when any exist.</summary>
public sealed record TrayToolbarDivider : TrayToolbarItem;

/// <summary>An app-launcher shortcut in the tray flyout's toolbar row (Flyout D2 .1, RemEx-4kv0g.18.5).</summary>
/// <remarks>
/// LOCAL ONLY, LIKE EVERY OTHER ITEM ON THIS ROW — see <see cref="TrayFlyoutViewModel"/>'s own
/// remarks. <see cref="LaunchCommand"/> never checks <c>ConnectionViewModel.IsConnected</c> the way
/// <c>AppLauncherViewModel.LaunchAppAsync</c> does; forwarding a launch to a connected phone from the
/// tray of the PC that phone is paired to would be the phone-control command RemEx does not have.
/// </remarks>
public sealed record TrayShortcut : TrayToolbarItem
{
    public required Guid EntryId { get; init; }
    public required string DisplayName { get; init; }
    public string? IconBase64 { get; init; }
    public required ICommand LaunchCommand { get; init; }
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
/// whose label implies the phone is being controlled. This is also why <see cref="TrayShortcut"/>'s
/// launch never routes through a connected session the way the App Launcher page's own launch does.
/// </para>
/// </remarks>
public sealed partial class TrayFlyoutViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly HomeViewModel _home;
    private readonly ILauncherStorageService _launcherStorage;

    /// <summary>
    /// The launcher's own entries, cached from the last <see cref="ILauncherStorageService.LoadEntriesAsync"/>
    /// call so <see cref="RebuildToolbar"/> can run synchronously between reloads. Reloaded in the
    /// constructor and on every <see cref="Refresh"/> (RemEx-4kv0g.18.5) — the same "reload on show"
    /// contract <c>Refresh</c> already gives phone presence and the pinned-sensor list.
    /// </summary>
    private IReadOnlyList<AppEntry> _launcherEntries = [];

    /// <summary>Supplied by the view; see <c>ConfirmationDialogHost</c>. Title, message, button.</summary>
    /// <remarks>
    /// Null means "cannot confirm", and every caller must read that as "do not proceed" — the same
    /// contract every other destructive command in this app follows (RemEx-07jx).
    /// </remarks>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    public PhonePresenceMonitor Presence => PhonePresenceMonitor.Instance;

    /// <summary>Exposed because the status strip binds <c>Connection.StatusText</c>.</summary>
    public ConnectionViewModel Connection => _shell.Connection;

    /// <summary>
    /// The Home pins, minus <c>CustomizationSettings.FlyoutHiddenSensorIds</c> (Flyout D2 .1,
    /// RemEx-4kv0g.18.5). A SEPARATE COLLECTION FROM <c>HomeViewModel.PinnedSensors</c>, not a
    /// filtered view over it, because Avalonia's <c>ItemsControl</c> needs a collection it can bind
    /// to directly and <c>Where(...)</c> over an <c>ObservableCollection</c> does not itself raise
    /// <c>CollectionChanged</c>. Rebuilt on <see cref="Refresh"/>, whenever
    /// <c>HomeViewModel.PinnedSensors</c> itself changes, and whenever customization settings change
    /// (the hidden list can move even with the pins untouched).
    /// </summary>
    public ObservableCollection<SensorViewModel> FlyoutSensors { get; } = new();

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
    private IReadOnlyList<TrayToolbarItem> _toolbarItems = [];

    /// <summary>
    /// The <see cref="TrayTile"/> subset of <see cref="ToolbarItems"/>, kept for the tests and call
    /// sites that only ever cared about tiles.
    /// </summary>
    /// <remarks>
    /// SET ONCE PER <see cref="RebuildToolbar"/> CALL, NOT COMPUTED ON EVERY READ. A plain
    /// <c>ToolbarItems.OfType&lt;TrayTile&gt;().ToList()</c> getter would hand back a fresh list
    /// object on every access, which breaks <c>TrayFlyoutOnlineDeviceCountTests</c>' proof that a
    /// bare presence signal does not re-run <c>RebuildToolbar</c> — that test snapshots this
    /// reference once and asserts it is the SAME object after the signal, not merely an equal one.
    /// </remarks>
    public IReadOnlyList<TrayTile> Tiles { get; private set; } = [];

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

    public TrayFlyoutViewModel(ShellViewModel shell, HomeViewModel home, ILauncherStorageService launcherStorage)
    {
        _shell = shell;
        _home = home;
        _launcherStorage = launcherStorage;

        // Rebuild when phone presence changes, so the Remote tile enables and disables in place
        // rather than at the next time the flyout happens to be reopened. Also refresh the badge's
        // OnlineDeviceCount on every presence signal, not only IsPhoneAttached (review, MEDIUM,
        // RemEx-rjnbo.1): RebuildToolbar previously only ran on show / IsPhoneAttached / locale, and
        // a PINNED flyout is never re-shown (:126), so the count froze the moment a second paired
        // device changed state without flipping IsPhoneAttached itself. Both handlers subscribe to
        // the same PhonePresenceMonitor.PropertyChanged event that already drives IsPhoneAttached -
        // no dispatcher marshalling here, matching every other Presence handler in this file and
        // ShellViewModel's, because the poll that raises it already runs on the UI thread.
        Presence.PropertyChanged += OnPresenceChanged;
        Presence.PropertyChanged += OnPresenceChangedForOnlineDeviceCount;

        // And rebuild on a language switch. Every tile label is a snapshot taken in RebuildToolbar,
        // and a PINNED flyout is never re-shown - so without this it keeps the previous language
        // until the app restarts, sitting next to a shell and a tray menu that both changed.
        // PhonePresenceMonitor and the tray menu subscribe for the same reason.
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        // Rebuild the toolbar and the cards row whenever customization settings change (Flyout D2
        // .1, RemEx-4kv0g.18.5): FlyoutOpacity itself only ever reaches ThemeService, but
        // FlyoutHiddenTileIds/FlyoutAppIds/FlyoutHiddenSensorIds live on the same record and this is
        // the one signal that fires whenever any of them are saved. ShellViewModel.Customization is
        // reached rather than a direct ThemeService.CustomizationApplied subscription so this view
        // model does not need a third settings-carrying dependency threaded through its
        // constructor - ShellViewModel already re-raises PropertyChanged(nameof(Customization)) from
        // that exact event (see ShellViewModel's own _onCustomizationApplied).
        _shell.PropertyChanged += OnShellPropertyChanged;

        // The cards row tracks HomeViewModel.PinnedSensors directly: a pin/unpin on the canvas must
        // reach the flyout without waiting for the next Refresh (a pinned flyout is never re-shown).
        _home.PinnedSensors.CollectionChanged += OnPinnedSensorsChanged;

        RebuildToolbar();
        RebuildFlyoutSensors();

        // Fire-and-forget, like AppLauncherViewModel.LoadLaunchersAsync: the toolbar already rendered
        // above with whatever was cached (nothing, on first construction), and rebuilds again once
        // the launcher's own entries are in.
        _ = ReloadLauncherEntriesAsync();
    }

    /// <summary>Refreshes everything the flyout shows. Called each time it is about to be shown.</summary>
    public void Refresh()
    {
        _home.RefreshPinnedSensors();
        Presence.Refresh();
        RebuildToolbar();
        RebuildFlyoutSensors();
        _ = ReloadLauncherEntriesAsync();
    }

    /// <summary>
    /// Detaches every subscription above. This view model is registered with
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
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _home.PinnedSensors.CollectionChanged -= OnPinnedSensorsChanged;
    }

    private void OnPresenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhonePresenceMonitor.IsPhoneAttached))
            RebuildToolbar();
    }

    /// <summary>
    /// Keeps <see cref="OnlineDeviceCount"/> live for a PINNED flyout (review, MEDIUM, RemEx-rjnbo.1).
    /// </summary>
    /// <remarks>
    /// DELIBERATELY SEPARATE FROM <see cref="OnPresenceChanged"/> AND FROM <see cref="RebuildToolbar"/>.
    /// A pinned flyout is shown once and never rebuilt again, so the only way its count can move is a
    /// handler that runs off the presence signal directly rather than off the next call to
    /// <see cref="Refresh"/>. It refreshes only the count, not the whole toolbar, so a test can pin
    /// that this path runs without RebuildToolbar running a second time alongside it. Unfiltered on
    /// <c>e.PropertyName</c> on purpose: <see cref="IPairedDeviceSource"/> has no change event of its
    /// own (RemEx-rjnbo.1's own remarks), so every presence tick is the closest live signal there is
    /// that a paired device's online state may have moved even when <c>IsPhoneAttached</c> itself did
    /// not flip.
    /// </remarks>
    private void OnPresenceChangedForOnlineDeviceCount(object? sender, PropertyChangedEventArgs e) =>
        RefreshOnlineDeviceCount();

    /// <summary>The <see cref="OnlineDeviceCount"/> half of <see cref="RebuildToolbar"/>, split out so
    /// it can run on its own from a live presence signal without rebuilding the whole toolbar.</summary>
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
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => RebuildToolbar();

    /// <summary>
    /// Fires whenever <c>ShellViewModel.Customization</c> changes — i.e. on every
    /// <c>ThemeService.CustomizationApplied</c>, since that is the only thing that sets it (Flyout D2
    /// .1, RemEx-4kv0g.18.5). Both the toolbar's hidden tiles/shortcuts and the cards row's hidden
    /// sensors live on that one settings record, so both rebuild together.
    /// </summary>
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.Customization))
            return;

        RebuildToolbar();
        RebuildFlyoutSensors();
    }

    private void OnPinnedSensorsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildFlyoutSensors();

    /// <summary>
    /// Rebuilds <see cref="FlyoutSensors"/> from <c>HomeViewModel.PinnedSensors</c>, minus
    /// <c>CustomizationSettings.FlyoutHiddenSensorIds</c> (Flyout D2 .1, RemEx-4kv0g.18.5). The
    /// hidden list is empty until .18.6 gives it a Personalize control, so today this always mirrors
    /// every pin — the filter exists so that task is a settings-only change, not a view model one.
    /// </summary>
    private void RebuildFlyoutSensors()
    {
        // NULL-SAFE ON PURPOSE, NOT JUST EMPTY-SAFE. RemexJson's source-generated deserializer
        // leaves an absent JSON key at the CLR default for its declared type - null for a List<T>,
        // not the record's own `= new()` initializer (see CustomizationMigration's schema-6->7 arm
        // doc). CustomizationMigration repairs this for every REAL load, but a hand-built or
        // script-generated profile that already claims the current schema (bypassing every arm) can
        // still reach here with a null list, and hidden.Count on null is a NullReferenceException
        // that would take the whole flyout down over a settings file, not a code bug.
        var hidden = _shell.Customization.FlyoutHiddenSensorIds ?? [];

        FlyoutSensors.Clear();
        foreach (var sensor in _home.PinnedSensors)
        {
            if (hidden.Count > 0 && hidden.Contains(sensor.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            FlyoutSensors.Add(sensor);
        }
    }

    /// <summary>
    /// Reloads <see cref="_launcherEntries"/> from storage and rebuilds the toolbar against the
    /// fresh list — the same "reload on Refresh" contract phone presence and the pinned sensors
    /// already get.
    /// </summary>
    private async Task ReloadLauncherEntriesAsync()
    {
        try
        {
            _launcherEntries = await _launcherStorage.LoadEntriesAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, like LauncherStorageService's own catch-and-empty on the read side: a
            // corrupt or unreadable launchers.json must not stop the six action tiles from showing.
            System.Diagnostics.Debug.WriteLine($"[TrayFlyout] Failed to load launcher entries: {ex.Message}");
            _launcherEntries = [];
        }

        RebuildToolbar();
    }

    private void RebuildToolbar()
    {
        var remoteEnabled = TrayTileRules.IsRemoteDesktopEnabled(Presence.IsPhoneAttached);

        RefreshOnlineDeviceCount();

        var settings = _shell.Customization;
        // Null-safe for the same reason RebuildFlyoutSensors' own hidden-list read is - see that
        // method's remarks.
        var hiddenTileIds = settings.FlyoutHiddenTileIds ?? [];

        var tiles = new List<TrayTile>
        {
            new TrayTile
            {
                Id = "lock",
                Label = LocalizationService.Instance["Tray_Tile_Lock"],
                Icon = MaterialIconKind.Lock,
                Command = _shell.Connection.LockCommand,
            },
            new TrayTile
            {
                Id = "sleep",
                Label = LocalizationService.Instance["Tray_Tile_Sleep"],
                Icon = MaterialIconKind.WeatherNight,
                Command = _shell.Connection.SleepCommand,
            },
            new TrayTile
            {
                Id = "remote",
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
                Id = "send",
                Label = LocalizationService.Instance["Tray_Tile_SendFile"],
                Icon = MaterialIconKind.Upload,
                Command = OpenTransfersCommand,
                OpensMainWindow = true,
            },
            new TrayTile
            {
                Id = "pair",
                Label = LocalizationService.Instance["Btn_Pair"],
                Icon = MaterialIconKind.LinkVariant,
                // Always enabled: RemEx supports several paired devices, so gating this on
                // "already paired" would block adding a second phone.
                Command = OpenPairingCommand,
                OpensMainWindow = true,
            },
            new TrayTile
            {
                Id = "power",
                Label = LocalizationService.Instance["PaletteCategory_Power"],
                Icon = MaterialIconKind.Power,
                Command = NoOpCommand,
                HasSubmenu = true,
            },
        };

        var visibleTiles = hiddenTileIds.Count == 0
            ? tiles
            : tiles.Where(tile => !hiddenTileIds.Contains(tile.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        var items = new List<TrayToolbarItem>(visibleTiles);

        // App shortcuts, after a divider, only when at least one selected id still resolves to a
        // launcher entry (Flyout D2 .1, RemEx-4kv0g.18.5 §2). LAUNCHER ORDER, NOT TICKED ORDER
        // (.18.5 review carry-forward, RemEx-4kv0g.18.6) - spec §2 says "order = launcher order", so
        // the row has to match the App Launcher page regardless of the order ids were ticked in
        // Personalize. An id that no longer exists in the launcher is skipped rather than shown
        // broken, and a settings.FlyoutAppIds that resolves to nothing at all must not leave a
        // dangling divider with no shortcuts after it.
        var appIds = settings.FlyoutAppIds ?? [];
        if (appIds.Count > 0)
        {
            var appIdSet = appIds.ToHashSet();
            var orderedEntries = _launcherEntries
                .OrderBy(e => e.Order)
                .Where(e => appIdSet.Contains(e.Id))
                .ToList();

            if (orderedEntries.Count > 0)
            {
                items.Add(new TrayToolbarDivider());
                items.AddRange(orderedEntries.Select(entry => new TrayShortcut
                {
                    EntryId = entry.Id,
                    DisplayName = entry.DisplayName,
                    IconBase64 = entry.IconBase64,
                    LaunchCommand = new AsyncRelayCommand(() => LaunchShortcutAsync(entry)),
                }));
            }

            // Logged as the set-difference, not per-miss inside the loop above - orderedEntries no
            // longer walks appIds in ticked order, so there is no single loop iteration left to log
            // a miss from. Debug.WriteLine, not an ILogger (fix round 1, RemEx-4kv0g.18.6 review,
            // LOW) - this view model has no ILogger field (see the other Debug.WriteLine at :367,
            // same reasoning); adding one is out of this fix's scope.
            var missingIds = appIdSet.Except(orderedEntries.Select(e => e.Id));
            foreach (var missingId in missingIds)
                System.Diagnostics.Debug.WriteLine($"[TrayFlyout] FlyoutAppIds contains {missingId}, no matching launcher entry - skipped.");
        }

        ToolbarItems = items;
        Tiles = visibleTiles;

        OnPropertyChanged(nameof(PinTooltip));
    }

    /// <summary>
    /// Launches an app-launcher entry from the tray, LOCALLY ONLY — see <see cref="TrayShortcut"/>'s
    /// own remarks for why this never checks <c>ConnectionViewModel.IsConnected</c> the way
    /// <c>AppLauncherViewModel.LaunchAppAsync</c> does.
    /// </summary>
    private static async Task LaunchShortcutAsync(AppEntry entry)
    {
        // Feeds the Home "Recent activity" panel, same as the App Launcher page's own launch.
        ActivityService.Instance.Record(ActivityKind.AppLaunched, entry.DisplayName);

        await EmbeddedHostServiceLocator.Require<IAppLauncherService>().LaunchAppAsync(entry.TargetPath);
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
