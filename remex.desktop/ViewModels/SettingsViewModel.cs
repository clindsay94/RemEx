using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Guards;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// Manages snap-to-grid toggle, grid size, persisted host address,
/// and sensor pinning to the Home screen.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ConnectionViewModel _connection;
    private readonly ShellViewModel _shell;
    private readonly FileTransferRootSettingsService _fileTransferRootSettings;
    private readonly RemexSavefileService _savefileService;
    private DashboardProfile _profile = new();

    [ObservableProperty]
    private bool _isSnapToGridEnabled;

    [ObservableProperty]
    private int _gridSize = 50;

    [ObservableProperty]
    private string _hostAddress = "wss://localhost:5005/ws";

    [ObservableProperty]
    private string _language = "en";

    /// <summary>
    /// When true, the main window's X button hides the app to the system tray instead
    /// of exiting. When false, closing the window exits the app entirely.
    /// </summary>
    [ObservableProperty]
    private bool _isCloseToTrayEnabled = true;

    partial void OnIsCloseToTrayEnabledChanged(bool value) => Save();

    /// <summary>
    /// When true, the app checks GitHub for a newer release on startup and surfaces it in About.
    /// A single anonymous request to api.github.com; no telemetry. Defaults to true.
    /// </summary>
    [ObservableProperty]
    private bool _isCheckForUpdatesEnabled = true;

    partial void OnIsCheckForUpdatesEnabledChanged(bool value) => Save();

    // Guards the load-time assignment so SEEDING the toggle from the real registration state does not
    // turn straight back into a write. Its neighbour (keep-session-unlocked) has had this guard since
    // RemEx-l6o; launch-at-login did not, so every trip through Settings re-registered the logon task
    // - rewriting it with whatever Environment.ProcessPath happened to be at that moment.
    private bool _suppressLaunchAtLoginWrite;

    [ObservableProperty]
    private bool _isLaunchAtLoginEnabled;

    partial void OnIsLaunchAtLoginEnabledChanged(bool value)
    {
        if (_suppressLaunchAtLoginWrite) return;

        var startupService = App.Services?.GetService(typeof(IStartupRegistrationService)) as IStartupRegistrationService;
        if (startupService != null && startupService.IsSupported)
        {
            startupService.SetEnabled(value);
        }
    }

    /// <summary>
    /// What seeding the launch-at-login toggle from a tri-state read should produce.
    /// </summary>
    /// <param name="Enabled">The value the switch should take.</param>
    /// <param name="StateUnknown">Whether the query failed, and the user must be told so.</param>
    internal readonly record struct LaunchAtLoginSeed(bool Enabled, bool StateUnknown);

    /// <summary>
    /// Decides what the toggle shows given the real registration state (RemEx-h5lr).
    /// </summary>
    /// <param name="registered">From <c>TryIsEnabled()</c>; null means the query failed.</param>
    /// <param name="currentToggleState">What the switch shows now.</param>
    /// <remarks>
    /// **INTERNAL AND PURE SO A TEST CAN CALL THE REAL RULE.** Review caught the first version of
    /// this covered only by a private reimplementation inside the test file — the same "the test
    /// exercises a stand-in" gap that mutation testing had already found once in this change, on the
    /// schtasks mapping. A copy of the logic in the test verifies the copy.
    /// <para>
    /// UNKNOWN HOLDS THE CURRENT VALUE rather than asserting either state. Forcing "off" is the
    /// drift this bead exists to fix; forcing "on" is the same lie in the other direction. And since
    /// the switch is also the write control, the caller must apply this WITHOUT triggering a write.
    /// </para>
    /// </remarks>
    internal static LaunchAtLoginSeed SeedLaunchAtLogin(bool? registered, bool currentToggleState) =>
        new(registered ?? currentToggleState, registered is null);

    /// <summary>
    /// True when the real registration state could not be read (RemEx-h5lr).
    /// </summary>
    /// <remarks>
    /// **THE SWITCH IS ALSO THE CONTROL THAT WRITES, WHICH IS WHY THIS IS NOT COSMETIC.** The query
    /// can fail for reasons that say nothing about the task — an EDR blocking <c>schtasks</c>, an
    /// unavailable Task Scheduler endpoint, an unreadable autostart directory — and the old read
    /// reported every one of those as "off". A user who then flips the switch to correct it issues a
    /// real registration against a state nobody established, and the UI has meanwhile told them
    /// their PC will not start RemEx at sign-in, which may be untrue.
    /// </remarks>
    [ObservableProperty]
    private bool _isLaunchAtLoginStateUnknown;

    [ObservableProperty]
    private bool _isLaunchAtLoginSupported;

    // --- Keep session unlocked (opt-in unattended access) (RemEx-l6o) ---
    // Guards the load-time assignment so seeding the toggle from the persisted flag does not trigger
    // a redundant write (and a possible revert) back through the change handler.
    private bool _suppressKeepUnlockedWrite;

    [ObservableProperty]
    private bool _isKeepSessionUnlockedEnabled;

    [ObservableProperty]
    private bool _isKeepSessionUnlockedSupported;

    partial void OnIsKeepSessionUnlockedEnabledChanged(bool value)
    {
        if (_suppressKeepUnlockedWrite)
        {
            return;
        }

        var svc = App.Services?.GetService(typeof(ISessionKeepUnlockedService)) as ISessionKeepUnlockedService;
        if (svc == null || !svc.IsSupported)
        {
            return;
        }

        if (!svc.SetEnabled(value))
        {
            // Persisting the flag failed (e.g. insufficient rights). Revert the toggle without
            // re-entering this handler so the UI reflects the true, unchanged state.
            _suppressKeepUnlockedWrite = true;
            IsKeepSessionUnlockedEnabled = !value;
            _suppressKeepUnlockedWrite = false;
        }
    }

    /// <summary>Fully exits the application (stops the process), same as the tray "Exit".</summary>
    [RelayCommand]
    private void ExitApplication() => App.RequestApplicationShutdown();

    public ObservableCollection<LanguageItem> AvailableLanguages { get; } = new()
    {
        new("English", "en"),
        new("Español", "es"),
        new("Français", "fr"),
        new("हिन्दी", "hi"),
        new("Bahasa Indonesia", "id"),
        new("Polski", "pl"),
        new("Português (BR)", "pt-BR"),
        new("Türkçe", "tr"),
        new("Українська", "uk")
    };

    public Func<FolderPickerOpenOptions, Task<IReadOnlyList<IStorageFolder>>>? PickSharedFolderAsync { get; set; }

    /// <summary>Wired by the view to a native "Save File" picker. Used by <see cref="ExportSettingsCommand"/>.</summary>
    public Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickSaveFileAsync { get; set; }

    /// <summary>Wired by the view to a native "Open File" picker. Used by <see cref="ImportSettingsCommand"/>.</summary>
    public Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? PickOpenFileAsync { get; set; }

    public ObservableCollection<FileTransferSharedRootItem> SharedRoots { get; } = new();

    /// <summary>Always true on this platform; see the note on <see cref="SupportsTrustManagement"/>.</summary>
    public bool SupportsSharedFolderConfiguration => true;

    public bool HasSharedRoots => SharedRoots.Count > 0;

    // ═══════════════ File-sharing trust management (2.1) ═══════════════

    /// <summary>Per-paired-device file-sharing consent (full-browse + auto-accept), backed by the
    /// in-process host's <see cref="IFileTrustService"/>. Only meaningful when this PC is running the
    /// embedded host (it is the serving device); resolved lazily and null in client-only mode.</summary>
    private IFileTrustService? _fileTrustService;
    private bool _fileTrustServiceResolved;

    public ObservableCollection<FileTrustDeviceItem> TrustedDevices { get; } = new();

    /// <summary>
    /// The phones paired to this PC (RemEx-kirdm).
    /// </summary>
    /// <remarks>
    /// A DIFFERENT LIST FROM <see cref="TrustedDevices"/>, AND THEY MUST NOT BE MERGED. That one is
    /// the File-Sharing Trust list — per-device file grants, revocable without touching pairing. This
    /// one is the pairing itself. Showing them as a single list would let a user revoke the wrong
    /// thing, which on the pairing side means a phone that has to pair again with a new PIN.
    /// </remarks>
    public ObservableCollection<PairedDeviceItem> PairedDevices { get; } = new();

    /// <summary>Whether this PC can list its pairings at all — false when no host is in this process.</summary>
    /// <remarks>
    /// COMPUTED, NOT A FLAG SET BY THE REFRESH (review). It was an [ObservableProperty] defaulting to
    /// false, assigned true only inside RefreshPairedDevices — whose only caller was the Refresh
    /// button INSIDE the card this gates. A closed loop: the card was never visible, so the button
    /// was never reachable, so the flag was never set. The whole feature was dead in the shipped
    /// binary and nothing failed, logged or looked wrong. SupportsTrustManagement, six lines up, is
    /// computed for exactly this reason and is why the Trust card renders before anything loads.
    /// </remarks>
    public bool CanListPairedDevices => ResolvePairedDeviceSource() is not null;

    /// <summary>Finds the host's paired-device list, or null when no host is in this process.</summary>
    /// <remarks>
    /// Resolved on every call rather than cached: the embedded host publishes its container after it
    /// starts and this view model can be built first, so a cached null would stick for the session
    /// (the mistake found in review of RemEx-n8xk, on the same two-container arrangement).
    /// </remarks>
    private static IPairedDeviceSource? ResolvePairedDeviceSource() => ResolveHostService<IPairedDeviceSource>();

    /// <summary>
    /// Finds a service the embedded host registered, or null when no host is in this process.
    /// </summary>
    /// <remarks>
    /// ONE RESOLVER, BECAUSE THE THIRD COPY WAS ABOUT TO LAND. The paired-device surface now has a
    /// list, a renamer and a revoker, and each had its own verbatim copy of this two-container
    /// fallback (review of RemEx-4gbp2 called it before the third arrived).
    /// <para>
    /// RESOLVED ON EVERY CALL, never cached: the host publishes its container after it starts and
    /// this view model can be built first, so a cached null would stick for the session — the mistake
    /// found in review of RemEx-n8xk. The app container is tried first and is expected to miss; it is
    /// there so a desktop-side test double would win.
    /// </para>
    /// </remarks>
    private static T? ResolveHostService<T>() where T : class
        => App.Services?.GetService(typeof(T)) as T
            ?? App.EmbeddedHostServices?.GetService(typeof(T)) as T;

    /// <summary>
    /// Re-reads the paired devices and their live state.
    /// </summary>
    /// <remarks>
    /// THE SOURCE IS RESOLVED ON EVERY CALL, not cached — the embedded host publishes its container
    /// after it starts and this view model can be built first, so a cached null would stick for the
    /// session (the mistake found in review of RemEx-n8xk, on the same two-container arrangement).
    /// <para>
    /// The rows are REPLACED rather than diffed. The list is a handful of items a person reads, the
    /// refresh is user-initiated or on-navigate, and a diff would buy nothing but a way to leave a
    /// stale row on screen.
    /// </para>
    /// </remarks>
    public void RefreshPairedDevices()
    {
        var source = ResolvePairedDeviceSource();

        OnPropertyChanged(nameof(CanListPairedDevices));
        OnPropertyChanged(nameof(CanRenamePairedDevices));
        OnPropertyChanged(nameof(CanUnpairDevices));
        PairedDevices.Clear();
        if (source is null) return;

        var unknown = LocalizationService.Instance["Settings_PairedDeviceUnknownDate"];
        var rows = source.PairedDevices();

        // THE USER'S OVERRIDE OUTRANKS THE DEVICE'S REPORTED NAME, which is what
        // PairedDeviceDisplayName.Resolve implements — it takes the override map and falls back. The
        // two are kept apart all the way from their separate stores to here, so a re-pair can refresh
        // one without discarding the other (review of RemEx-4gbp2).
        var names = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.NameOverride) || !string.IsNullOrWhiteSpace(r.DeviceName))
            .ToDictionary(
                r => r.ClientId,
                r => (r.NameOverride ?? r.DeviceName)!,
                StringComparer.Ordinal);

        foreach (var row in rows)
        {
            PairedDevices.Add(new PairedDeviceItem
            {
                ClientId = row.ClientId,
                DisplayName = PairedDeviceDisplayName.Resolve(row.ClientId, names),
                FirstPairedText = PairedDeviceRowText.Describe(
                    row.FirstPairedUtc, unknown, System.Globalization.CultureInfo.CurrentCulture),
                LastSeenText = PairedDeviceRowText.Describe(
                    row.LastSeenUtc, unknown, System.Globalization.CultureInfo.CurrentCulture),
                IsOnline = row.IsOnline,
                // SEEDED FROM THE OVERRIDE, NOT LEFT EMPTY (review). An empty field beside a Rename
                // button makes the button's RESTING state destructive: blank means clear, so a second
                // click — or a click after the post-apply refresh — would wipe the name just set.
                // Seeding it means clearing is "select all, delete, apply", which is the deliberate
                // act the hint describes.
                PendingName = row.NameOverride ?? string.Empty,
                StatusAccessibleName = LocalizationService.Instance[
                    row.IsOnline ? "A11y_PairedDeviceOnline" : "A11y_PairedDeviceOffline"],
            });
        }
    }

    [RelayCommand]
    private void RefreshPairedDeviceList() => RefreshPairedDevices();

    /// <summary>Whether this PC can rename a paired device — false when no host is in this process.</summary>
    public bool CanRenamePairedDevices => ResolvePairedDeviceNameWriter() is not null;

    private static IPairedDeviceNameWriter? ResolvePairedDeviceNameWriter()
        => ResolveHostService<IPairedDeviceNameWriter>();

    /// <summary>Whether this PC can end a pairing — false when no host is in this process.</summary>
    public bool CanUnpairDevices => ResolveHostService<IPairedDeviceRevoker>() is not null;

    /// <summary>
    /// Ends a device's pairing, after a confirmation that says what it costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FAILS CLOSED, matching every other confirmed action in this class: an unwired view model, or a
    /// view with no visible parent window, DECLINES rather than revoking unconfirmed. This is the one
    /// action here that cannot be undone — the phone must pair again with a new PIN, because the
    /// credential is gone — so an unconfirmed revocation is the worst possible failure mode.
    /// </para>
    /// <para>
    /// The confirmation names the device and says what happens next. "Remove" on its own reads like
    /// tidying a list, and a user who reads it that way will be surprised when their phone stops
    /// connecting.
    /// </para>
    /// <para>
    /// AND IT REPORTS, like the two sibling confirmed actions twelve lines apart from it. The revoker
    /// throws when a teardown failed; letting that escape an <c>AsyncRelayCommand</c> kills the app
    /// on the dispatcher, and swallowing it would be worse — the row vanishes from a rebuilt list, the
    /// user reads that as success, and the pairing is still on disk after a restart.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task UnpairDeviceAsync(PairedDeviceItem? item)
    {
        if (item is null) return;

        var revoker = ResolveHostService<IPairedDeviceRevoker>();
        if (revoker is null) return;

        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_Unpair_Title"],
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Instance["Confirm_Unpair_Message"],
                    item.DisplayName),
                LocalizationService.Instance["Confirm_Unpair_Btn"]))
        {
            return;
        }

        string? error = null;
        try
        {
            await revoker.RevokeAsync(item.ClientId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // BOTH CARDS, AND ON THE FAILURE PATH TOO. The device appears twice on this page — once as a
        // pairing and once, if it has file-access grants, under Trusted Devices — and revoking clears
        // both stores. Rebuilding only the first would leave the same phone listed below with a
        // "revoke trust" button for a pairing that no longer exists. Rebuilding after a FAILED
        // revocation matters more: a partial teardown is exactly when the two lists can disagree, and
        // the screen should show what is actually stored rather than what was meant to happen.
        RefreshPairedDevices();
        await LoadTrustedDevicesAsync();

        ShowTransientStatus(error is null
            ? LocalizationService.Instance["Settings_DeviceUnpaired"]
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Instance["Status_ErrorFormat"],
                error));
    }

    /// <summary>
    /// Applies the name typed into a row, then rebuilds the list so the row shows the result.
    /// </summary>
    /// <remarks>
    /// REFRESHING AFTERWARDS IS THE POINT, not tidiness. The store normalizes — it trims, caps at 48
    /// characters, and treats blank as CLEAR — so what the user typed and what is now stored are not
    /// always the same string. Showing the typed text would tell them a 60-character name had been
    /// kept whole. Re-reading shows what a phone will actually be called.
    /// </remarks>
    [RelayCommand]
    private void ApplyPairedDeviceRename(PairedDeviceItem? item)
    {
        if (item is null) return;

        var writer = ResolvePairedDeviceNameWriter();
        if (writer is null) return;

        writer.Rename(item.ClientId, item.PendingName);
        RefreshPairedDevices();
    }

    /// <summary>
    /// True when the trust-management UI should be shown, i.e. an embedded host is present.
    /// </summary>
    /// <remarks>
    /// The "and not Android" half of this condition is gone because it could never be false:
    /// remex.desktop targets net10.0, not net10.0-android, so <c>OperatingSystem.IsAndroid()</c>
    /// is unreachable here (RemEx-f167). Kept as a property rather than inlined at the call site
    /// because it gates a whole Settings section and the binding needs a name.
    /// </remarks>
    public bool SupportsTrustManagement => ResolveTrustService() is not null;

    public bool HasTrustedDevices => TrustedDevices.Count > 0;

    /// <summary>
    /// Test-only seam: supplies the trust service instead of resolving it from the embedded host.
    /// </summary>
    /// <remarks>
    /// <see cref="ResolveTrustService"/> reaches into <c>EmbeddedHostServiceLocator</c>, which in a
    /// test run has no host to find — so every trust action returned early and the destructive-action
    /// coverage stopped at the guard rather than proving anything past it. Setting this also latches
    /// the resolution flag, so the locator is never consulted (RemEx-e1re).
    /// </remarks>
    internal IFileTrustService? FileTrustServiceForTests
    {
        set
        {
            _fileTrustService = value;
            _fileTrustServiceResolved = true;
        }
    }

    private IFileTrustService? ResolveTrustService()
    {
        if (_fileTrustServiceResolved)
            return _fileTrustService;

        _fileTrustServiceResolved = true;
        try
        {
            _fileTrustService = EmbeddedHostServiceLocator.Require<IFileTrustService>();
        }
        catch (Exception)
        {
            // Client-only mode (no embedded host): trust management is unavailable.
            _fileTrustService = null;
        }
        return _fileTrustService;
    }

    [ObservableProperty]
    private string _hostRuntimeText = LocalizationService.Instance["Service_HostUnavailable"];

    [ObservableProperty]
    private string _hostCapabilityText = LocalizationService.Instance["Service_HostUnavailableHint"];

    /// <summary>Host JPEG compression quality (10–100) for the screen stream.</summary>
    [ObservableProperty]
    private int _streamQuality = 100;

    /// <summary>Host target frames-per-second for the screen stream.</summary>
    [ObservableProperty]
    private int _streamFps = 30;

    partial void OnStreamQualityChanged(int value) => Save();
    partial void OnStreamFpsChanged(int value) => Save();

    /// <summary>Available sensors with checkboxes for pinning to Home.</summary>
    public ObservableCollection<SensorPinItem> AvailableSensors { get; } = new();

    public SettingsViewModel(
        DashboardLayoutService layoutService,
        ConnectionViewModel connection,
        ShellViewModel shell,
        FileTransferRootSettingsService fileTransferRootSettings,
        RemexSavefileService savefileService)
    {
        _layoutService = layoutService;
        _connection = connection;
        _shell = shell;
        _fileTransferRootSettings = fileTransferRootSettings;
        _savefileService = Guard.NotNull(savefileService);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    /// <summary>Whether a phone is attached, shared with every other indicator (RemEx-7zzw).</summary>
    /// <remarks>
    /// The same singleton the shell reads. Bound by this screen's status dot so it cannot disagree
    /// with the sidebar about whether a phone is there — which is what happened when RemEx-0z7w
    /// rebound only the shell.
    /// </remarks>
    public PhonePresenceMonitor Presence => PhonePresenceMonitor.Instance;

    /// <summary>Live connection view-model — bound directly from the Connection settings card.</summary>
    public ConnectionViewModel Connection => _connection;

    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetCulture raises "Item", "Item[]" and "" in sequence, so an unguarded handler runs three
        // times per switch. Harmless when the body only re-raises notifications; this one re-reads
        // state, so it does the work once. Borrowed from AboutViewModel, which was the only handler
        // in the codebase getting this right.
        if (!string.IsNullOrEmpty(e.PropertyName))
            return;

        // The paired-device rows hold ALREADY-FORMATTED strings, including the localized "unknown"
        // date marker, so nothing in the binding layer can re-translate them on a language switch —
        // they have to be rebuilt (review; RemEx-q3h0's pattern).
        RefreshPairedDevices();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_connection.HostCapabilities == null)
            {
                // Not connected: these two are the placeholder wording, resolved here.
                HostRuntimeText = LocalizationService.Instance["Service_HostUnavailable"];
                HostCapabilityText = LocalizationService.Instance["Service_HostUnavailableHint"];
                return;
            }

            // CONNECTED - the case this handler used to skip entirely, and the case a user is
            // normally in. Both properties hold a SNAPSHOT taken from the connection's localized
            // summaries, so nothing about them changes when the language does; they simply kept the
            // previous wording until host capabilities happened to change next. Recomputing from the
            // source is the whole fix (RemEx-q3h0).
            UpdateHostCapabilitySummary();
        });
    }

    /// <summary>Loads current values from the persisted profile.</summary>
    public async Task InitializeAsync()
    {
        _profile = await _layoutService.LoadAsync();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsSnapToGridEnabled = _profile.IsSnapToGridEnabled;
            GridSize = _profile.GridSize;
            HostAddress = _profile.HostAddress;
            Language = string.IsNullOrWhiteSpace(_profile.Language) ? "en" : _profile.Language;
            IsCloseToTrayEnabled = _profile.CloseToTray;
            IsCheckForUpdatesEnabled = _profile.CheckForUpdatesAutomatically;

            var startupService = App.Services?.GetService(typeof(IStartupRegistrationService)) as IStartupRegistrationService;
            if (startupService != null)
            {
                IsLaunchAtLoginSupported = startupService.IsSupported;
                if (IsLaunchAtLoginSupported)
                {
                    // TRI-STATE READ, SEEDED WITHOUT WRITING BACK. A null means the query failed, and
                    // the switch must not present that as "off" - that is the drift RemEx-q0j7 was a
                    // real instance of, and it is worse than a stale display because the switch is
                    // also the write path.
                    var seed = SeedLaunchAtLogin(startupService.TryIsEnabled(), IsLaunchAtLoginEnabled);
                    IsLaunchAtLoginStateUnknown = seed.StateUnknown;

                    _suppressLaunchAtLoginWrite = true;
                    try
                    {
                        IsLaunchAtLoginEnabled = seed.Enabled;
                    }
                    finally
                    {
                        _suppressLaunchAtLoginWrite = false;
                    }
                }
            }

            var keepUnlockedService = App.Services?.GetService(typeof(ISessionKeepUnlockedService)) as ISessionKeepUnlockedService;
            if (keepUnlockedService != null)
            {
                IsKeepSessionUnlockedSupported = keepUnlockedService.IsSupported;
                if (IsKeepSessionUnlockedSupported)
                {
                    // Seed from the persisted flag without triggering a write-back. (RemEx-l6o)
                    _suppressKeepUnlockedWrite = true;
                    IsKeepSessionUnlockedEnabled = keepUnlockedService.IsEnabled();
                    _suppressKeepUnlockedWrite = false;
                }
            }

            Services.LocalizationService.Instance.SetCulture(Language);
            StreamQuality = _profile.StreamQuality;
            StreamFps = _profile.StreamFps;
            UpdateHostCapabilitySummary();
            RefreshSensors();

            // Seed connection history from the persisted profile
            _connection.ConnectionHistory.Clear();
            foreach (var entry in _profile.ConnectionHistory ?? Enumerable.Empty<ConnectionProfile>())
                _connection.ConnectionHistory.Add(entry);
        });

        await LoadSharedRootsAsync();
        await LoadTrustedDevicesAsync();

        // MARSHALLED, because this mutates an ObservableCollection bound to an ItemsControl and we
        // are on a continuation after two awaits, not necessarily on the UI thread (review).
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshPairedDevices);
    }

    /// <summary>
    /// Rebuilds the available sensors list from the canvas VM's current cards.
    /// </summary>
    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        foreach (var item in AvailableSensors)
            item.PinChanged -= OnSensorPinChanged;
        foreach (var root in SharedRoots)
            UnsubscribeSharedRoot(root);
        foreach (var device in TrustedDevices)
            UnsubscribeTrustDevice(device);
    }

    public void RefreshSensors()
    {
        // Unsubscribe from old items before clearing
        foreach (var item in AvailableSensors)
            item.PinChanged -= OnSensorPinChanged;
        AvailableSensors.Clear();

        var canvas = _shell.CanvasViewModel;
        if (canvas is null) return;

        var sensorCards = canvas.Cards
            .Where(c => c.CardType == "Sensor" && c.Sensor != null)
            .OrderBy(c => c.Sensor!.Name);

        foreach (var card in sensorCards)
        {
            var name = card.Sensor!.Name;
            var pinnedIds = _profile.PinnedSensorIds ?? Enumerable.Empty<string>();
            var isPinned = pinnedIds.Contains(name);
            var source = card.Sensor.RawReading?.Source ?? "Unknown";
            var item = new SensorPinItem(name, isPinned, source);
            item.PinChanged += OnSensorPinChanged;
            AvailableSensors.Add(item);
        }
    }

    private void OnSensorPinChanged(object? sender, bool isPinned)
    {
        if (sender is not SensorPinItem item) return;

        // Update the canvas card's pinned state
        var canvas = _shell.CanvasViewModel;
        var card = canvas?.Cards.FirstOrDefault(c => c.Sensor?.Name == item.SensorName);
        if (card != null)
        {
            card.IsPinnedToHome = isPinned;
        }

        // Update profile
        if (_profile.PinnedSensorIds == null)
        {
            // We can't assign to _profile.PinnedSensorIds directly because it's init-only.
            // But _profile is a local field of type DashboardProfile (record).
            // We should use 'with' or just ensure the list is initialized if it's a List<T>.
            // Wait, DashboardProfile.PinnedSensorIds is public List<string> PinnedSensorIds { get; init; } = new();
            // If it's null, we need to replace the profile instance or the property.
            _profile = _profile with { PinnedSensorIds = new() };
        }

        if (isPinned && !_profile.PinnedSensorIds.Contains(item.SensorName))
            _profile.PinnedSensorIds.Add(item.SensorName);
        else if (!isPinned)
            _profile.PinnedSensorIds.Remove(item.SensorName);

        Save();
    }

    // ═══════════════ Change handlers ═══════════════

    partial void OnIsSnapToGridEnabledChanged(bool value)
    {
        if (_shell.CanvasViewModel is { } canvas)
            canvas.IsSnapToGridEnabled = value;
        Save();
    }

    partial void OnGridSizeChanged(int value)
    {
        if (_shell.CanvasViewModel is { } canvas)
            canvas.GridSize = value;
        Save();
    }

    partial void OnHostAddressChanged(string value)
    {
        // Push the value to the live ConnectionViewModel.
        _connection.HostAddress = value;
        Save();
    }

    partial void OnLanguageChanged(string value)
    {
        Services.LocalizationService.Instance.SetCulture(value);
        Save();
    }

    [ObservableProperty]
    private bool _isDiscovering;

    /// <summary>Hosts found by the last mDNS discovery run; bound to the host picker ComboBox.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> DiscoveredHosts => _connection.DiscoveredHosts;

    /// <summary>Recently used connection addresses; bound to the history picker ComboBox.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Remex.Core.Models.ConnectionProfile> ConnectionHistory => _connection.ConnectionHistory;

    [RelayCommand]
    private async Task DiscoverHostAsync()
    {
        IsDiscovering = true;
        try
        {
            await _connection.DiscoverHostsCommand.ExecuteAsync(null);
            // Sync the discovered address back into our property
            HostAddress = _connection.HostAddress;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [ObservableProperty]
    private string _savedStatus = string.Empty;

    private void ShowTransientStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        SavedStatus = message;

        _ = Task.Delay(3000).ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (SavedStatus == message)
                    SavedStatus = string.Empty;
            }));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Save();
        await _layoutService.FlushAsync();
        ShowTransientStatus(LocalizationService.Instance["Settings_SavedStatus"]);
    }

    [RelayCommand]
    private async Task SaveAndReconnectAsync()
    {
        Save();
        await _layoutService.FlushAsync();

        // Disconnect first if already connected, then reconnect with new settings
        if (_connection.IsConnected || _connection.IsConnecting)
        {
            _connection.DisconnectCommand.Execute(null);
        }

        // Small delay so the disconnect completes
        await Task.Delay(300);
        await _connection.ConnectCommand.ExecuteAsync(null);
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack()
    {
        // Refresh Home's pinned sensors so changes made in Settings are immediately visible.
        if (_shell.CurrentView is HomeViewModel home)
            home.RefreshPinnedSensors();

        _shell.NavigateToHome();
    }

    [RelayCommand]
    private void ReplayTutorial()
    {
        _shell.CloseSettingsPanel();
        _shell.ReplayTutorial();
    }

    [RelayCommand]
    private async Task AddSharedFolderAsync()
    {
        if (!SupportsSharedFolderConfiguration)
            return;

        if (PickSharedFolderAsync is null)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferPickerUnavailable"]);
            return;
        }

        var folders = await PickSharedFolderAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_FileTransferPickerTitle"],
            AllowMultiple = false,
        });

        if (folders.Count == 0)
            return;

        var selectedPath = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferLocalPathUnavailable"]);
            return;
        }

        var normalizedPath = Path.GetFullPath(selectedPath);
        if (SharedRoots.Any(root => PathsEqual(root.AbsolutePath, normalizedPath)))
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferFolderExists"]);
            return;
        }

        var item = new FileTransferSharedRootItem(
            $"custom-{Guid.NewGuid():N}",
            GetSharedRootDisplayName(normalizedPath),
            normalizedPath,
            isWritable: false);

        SubscribeSharedRoot(item);
        SharedRoots.Add(item);
        OnPropertyChanged(nameof(HasSharedRoots));

        await SaveSharedRootsAsync(LocalizationService.Instance["Settings_FileTransferFolderAdded"]);
    }

    /// <summary>
    /// Delegate set by the View to display a confirmation dialog.
    /// Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// Guards the three destructive Settings actions: restoring default shared folders, removing a
    /// shared folder, and revoking a device's trust (RemEx-6p1f).
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    [RelayCommand]
    private async Task RestoreDefaultSharedFoldersAsync()
    {
        if (!SupportsSharedFolderConfiguration)
            return;

        // Silently discards every folder the user added to sharing, so confirm first (RemEx-6p1f).
        // Fails CLOSED: with no dialog wired the current folder list is left alone.
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_RestoreFolders_Title"],
                LocalizationService.Instance["Confirm_RestoreFolders_Msg"],
                LocalizationService.Instance["Settings_FileTransferRestoreDefaults"]))
        {
            return;
        }

        try
        {
            var roots = await _fileTransferRootSettings.ResetToDefaultsAsync();
            ReplaceSharedRoots(roots);
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferDefaultsRestored"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    private async Task LoadTrustedDevicesAsync()
    {
        var service = ResolveTrustService();
        OnPropertyChanged(nameof(SupportsTrustManagement));
        if (service is null)
            return;

        try
        {
            var records = await service.GetAllAsync(CancellationToken.None);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ReplaceTrustedDevices(records));
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message)));
        }
    }

    [RelayCommand]
    private async Task RefreshTrustedDevicesAsync() => await LoadTrustedDevicesAsync();

    private void ReplaceTrustedDevices(IReadOnlyList<FileTrustRecord> records)
    {
        foreach (var existing in TrustedDevices)
            UnsubscribeTrustDevice(existing);

        TrustedDevices.Clear();

        foreach (var record in records)
        {
            var item = new FileTrustDeviceItem(record.ClientId, record.FullBrowseGranted, record.AutoAcceptIncoming);
            SubscribeTrustDevice(item);
            TrustedDevices.Add(item);
        }

        OnPropertyChanged(nameof(HasTrustedDevices));
    }

    private void SubscribeTrustDevice(FileTrustDeviceItem item)
    {
        item.FullBrowseChanged += OnTrustFullBrowseChanged;
        item.AutoAcceptChanged += OnTrustAutoAcceptChanged;
        item.RevokeRequested += OnTrustRevokeRequested;
    }

    private void UnsubscribeTrustDevice(FileTrustDeviceItem item)
    {
        item.FullBrowseChanged -= OnTrustFullBrowseChanged;
        item.AutoAcceptChanged -= OnTrustAutoAcceptChanged;
        item.RevokeRequested -= OnTrustRevokeRequested;
    }

    private async void OnTrustFullBrowseChanged(object? sender, bool granted)
    {
        if (sender is not FileTrustDeviceItem item || ResolveTrustService() is not { } service)
            return;
        try
        {
            await service.SetFullBrowseGrantedAsync(item.ClientId, granted, CancellationToken.None);
            ShowTransientStatus(LocalizationService.Instance["Settings_TrustUpdated"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    private async void OnTrustAutoAcceptChanged(object? sender, bool autoAccept)
    {
        if (sender is not FileTrustDeviceItem item || ResolveTrustService() is not { } service)
            return;
        try
        {
            await service.SetAutoAcceptIncomingAsync(item.ClientId, autoAccept, CancellationToken.None);
            ShowTransientStatus(LocalizationService.Instance["Settings_TrustUpdated"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    /// <summary>
    /// Event plumbing only. The work lives in <see cref="RevokeTrustAsync"/> so it can be awaited.
    /// </summary>
    /// <remarks>
    /// An <c>async void</c> handler cannot be awaited, so a test can raise the event but not know
    /// when the handler finished — which left this destructive action, and the shared-root one below,
    /// as the two of six that RemEx-w9ui could not cover with the fail-closed cases. Splitting the
    /// body out costs one line and makes the same three cases apply here as everywhere else
    /// (RemEx-e1re).
    /// </remarks>
    private async void OnTrustRevokeRequested(object? sender, EventArgs e)
    {
        if (sender is FileTrustDeviceItem item) await RevokeTrustAsync(item);
    }

    /// <summary>
    /// Revokes one paired device's file-access trust, after confirmation.
    /// </summary>
    /// <remarks>
    /// Internal rather than private purely so the fail-closed tests can await it; nothing in
    /// production calls it except the handler above.
    /// </remarks>
    internal async Task RevokeTrustAsync(FileTrustDeviceItem item)
    {
        if (ResolveTrustService() is not { } service)
            return;

        // One mis-click here severs that phone's file access until the user pairs and approves it
        // again, so confirm first (RemEx-6p1f). The confirmation lives in this handler rather than
        // in FileTrustDeviceItem.Revoke() because this is where the revocation actually happens —
        // the item only raises the event. Fails CLOSED: with no dialog wired, trust is kept.
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_RevokeTrust_Title"],
                string.Format(
                    LocalizationService.Instance["Confirm_RevokeTrust_Format"],
                    item.ShortId),
                LocalizationService.Instance["Settings_TrustRevoke"]))
        {
            return;
        }

        try
        {
            await service.RevokeAsync(item.ClientId, CancellationToken.None);
            UnsubscribeTrustDevice(item);
            TrustedDevices.Remove(item);
            OnPropertyChanged(nameof(HasTrustedDevices));
            ShowTransientStatus(LocalizationService.Instance["Settings_TrustRevoked"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    // ═══════════════ Backup & Restore (savefile export/import) ═══════════════

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (PickSaveFileAsync is null)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferPickerUnavailable"]);
            return;
        }

        try
        {
            var file = await PickSaveFileAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["Settings_ExportButton"],
                SuggestedFileName = $"RemEx-settings-{DateTime.Now:yyyyMMdd}",
                DefaultExtension = "remexsave",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(LocalizationService.Instance["Settings_SavefileType"])
                    {
                        Patterns = new[] { "*.remexsave" },
                    },
                },
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await _savefileService.ExportAsync(stream);

            ShowTransientStatus(LocalizationService.Instance["Settings_ExportDone"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        if (PickOpenFileAsync is null)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_FileTransferPickerUnavailable"]);
            return;
        }

        try
        {
            var files = await PickOpenFileAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["Settings_ImportButton"],
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(LocalizationService.Instance["Settings_SavefileType"])
                    {
                        Patterns = new[] { "*.remexsave" },
                    },
                },
            });

            if (files.Count == 0)
                return;

            await using var stream = await files[0].OpenReadAsync();
            var result = await _savefileService.ImportAsync(stream);

            // Import wrote the imported layout to disk and reloaded the live theme/language, but the
            // already-open canvas holds its own cards — refresh it in place so the restored layout
            // appears without an app restart (RemEx-83c4). Also refresh this screen's sensor list.
            _shell.CanvasViewModel?.ReloadFromPersistedLayout();
            RefreshSensors();

            ShowTransientStatus(string.Format(
                LocalizationService.Instance["Settings_ImportDone"],
                result.AppliedSections.Count,
                result.Warnings.Count));
        }
        catch (SavefileNewerVersionException)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_ImportNewerVersion"]);
        }
        catch (SavefileFormatException)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_ImportInvalidFile"]);
        }
        catch (JsonException)
        {
            ShowTransientStatus(LocalizationService.Instance["Settings_ImportInvalidFile"]);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.HostCapabilities)
            or nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.IsConnecting)
            or nameof(ConnectionViewModel.IsAutoReconnecting))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateHostCapabilitySummary);
        }
        else if (e.PropertyName is nameof(ConnectionViewModel.HostAddress))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HostAddress = _connection.HostAddress);
        }
    }

    private void UpdateHostCapabilitySummary()
    {
        HostRuntimeText = _connection.HostRuntimeSummary;
        HostCapabilityText = _connection.RemoteDesktopAvailabilitySummary;
    }

    private async Task LoadSharedRootsAsync()
    {
        if (!SupportsSharedFolderConfiguration)
            return;

        try
        {
            var roots = await _fileTransferRootSettings.LoadAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ReplaceSharedRoots(roots));
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message)));
        }
    }

    private void ReplaceSharedRoots(IReadOnlyList<FileTransferRootConfiguration> roots)
    {
        foreach (var existing in SharedRoots)
            UnsubscribeSharedRoot(existing);

        SharedRoots.Clear();

        foreach (var root in roots)
        {
            var item = new FileTransferSharedRootItem(root.RootId, root.DisplayName, root.AbsolutePath, root.IsWritable);
            SubscribeSharedRoot(item);
            SharedRoots.Add(item);
        }

        OnPropertyChanged(nameof(HasSharedRoots));
    }

    private void SubscribeSharedRoot(FileTransferSharedRootItem item)
    {
        item.WritableChanged += OnSharedRootWritableChanged;
        item.RemoveRequested += OnSharedRootRemoveRequested;
    }

    private void UnsubscribeSharedRoot(FileTransferSharedRootItem item)
    {
        item.WritableChanged -= OnSharedRootWritableChanged;
        item.RemoveRequested -= OnSharedRootRemoveRequested;
    }

    private async void OnSharedRootWritableChanged(object? sender, bool isWritable)
    {
        await SaveSharedRootsAsync(LocalizationService.Instance["Settings_FileTransferSaved"]);
    }

    /// <summary>
    /// Event plumbing only. The work lives in <see cref="RemoveSharedRootAsync"/> so it can be
    /// awaited — see the note on <see cref="OnTrustRevokeRequested"/>.
    /// </summary>
    private async void OnSharedRootRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is FileTransferSharedRootItem item) await RemoveSharedRootAsync(item);
    }

    /// <summary>
    /// Removes one shared folder, after confirmation. Internal so the fail-closed tests can await it.
    /// </summary>
    internal async Task RemoveSharedRootAsync(FileTransferSharedRootItem item)
    {
        // Removing a shared root revokes the phone's access to that whole folder tree, so confirm
        // first (RemEx-6p1f). Same reasoning as trust revocation: the confirmation belongs in this
        // handler, not in the item's Remove() command, because the removal happens here. Fails
        // CLOSED: with no dialog wired the folder stays shared.
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_RemoveSharedFolder_Title"],
                string.Format(
                    LocalizationService.Instance["Confirm_RemoveSharedFolder_Format"],
                    item.DisplayName),
                LocalizationService.Instance["Settings_FileTransferRemove"]))
        {
            return;
        }

        try
        {
            // Re-find by RootId rather than trusting `item`'s object identity: while the
            // confirmation dialog above was awaited, ReplaceSharedRoots may have rebuilt the whole
            // collection (a host push or a savefile import), in which case `item` is a stale
            // instance no longer present by reference and SharedRoots.Remove(item) would silently
            // no-op. This is an access-REVOCATION path (a shared root grants the phone access to
            // an entire folder tree), so we must not tell the user it was saved unless something
            // was actually removed (RemEx-xqcx).
            var current = SharedRoots.FirstOrDefault(root => root.RootId == item.RootId);
            if (current is null)
            {
                // Already gone from the list - there is nothing to unsubscribe, remove, or save.
                // Reporting "Saved" here would be a false confirmation of a revocation that never
                // happened. No localized "already removed" string currently exists for this exact
                // case, so we simply avoid the false-positive message rather than show nothing
                // truthful; see handoff notes for the follow-up to add one.
                return;
            }

            UnsubscribeSharedRoot(current);
            SharedRoots.Remove(current);
            OnPropertyChanged(nameof(HasSharedRoots));
        }
        catch (Exception ex)
        {
            // OnSharedRootRemoveRequested is async void (it must be, as an event handler), so an
            // exception here would otherwise escape uncaught. SaveSharedRootsAsync below already
            // has its own try/catch; this covers the unsubscribe/remove lines that ran before it.
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
            return;
        }

        await SaveSharedRootsAsync(LocalizationService.Instance["Settings_FileTransferSaved"]);
    }

    private async Task SaveSharedRootsAsync(string successMessage)
    {
        try
        {
            await _fileTransferRootSettings.SaveAsync(SharedRoots.Select(root => new FileTransferRootConfiguration
            {
                RootId = root.RootId,
                DisplayName = root.DisplayName,
                AbsolutePath = root.AbsolutePath,
                IsWritable = root.IsWritable,
                CanRename = root.IsWritable,
                CanMove = root.IsWritable,
                CanDelete = root.IsWritable,
            }).ToList());

            ShowTransientStatus(successMessage);
        }
        catch (Exception ex)
        {
            ShowTransientStatus(string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static string GetSharedRootDisplayName(string absolutePath)
    {
        var trimmed = absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    // Windows Service Management was removed in RemEx 2.0 (RemEx-aep Phase 3). RemEx no longer runs
    // as a Windows Service; auto-start is an elevated Task Scheduler logon task driven by the
    // "Launch at login" toggle above (see StartupRegistrationService / autostart-remex.ps1).
    // ═══════════════ Persistence ═══════════════

    private void Save()
    {
        var updated = _profile with
        {
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = HostAddress,
            Language = Language,
            CloseToTray = IsCloseToTrayEnabled,
            CheckForUpdatesAutomatically = IsCheckForUpdatesEnabled,
            StreamQuality = StreamQuality,
            StreamFps = StreamFps
        };

        _profile = updated;
        _layoutService.RequestSave(updated);
    }
}

/// <summary>
/// Represents a sensor that can be pinned/unpinned to Home from Settings.
/// </summary>
public partial class SensorPinItem : ObservableObject
{
    public string SensorName { get; }

    /// <summary>Telemetry data source: "HWInfo", "WindowsPerf", "Linux", or "Unknown".</summary>
    public string Source { get; }

    [ObservableProperty]
    private bool _isPinned;

    public event System.EventHandler<bool>? PinChanged;

    public SensorPinItem(string sensorName, bool isPinned, string source = "Unknown")
    {
        SensorName = sensorName;
        _isPinned = isPinned;
        Source = source;
    }

    partial void OnIsPinnedChanged(bool value) => PinChanged?.Invoke(this, value);
}

public partial class FileTransferSharedRootItem : ObservableObject
{
    public string RootId { get; }

    public string DisplayName { get; }

    public string AbsolutePath { get; }

    [ObservableProperty]
    private bool _isWritable;

    public event EventHandler<bool>? WritableChanged;
    public event EventHandler? RemoveRequested;

    public FileTransferSharedRootItem(string rootId, string displayName, string absolutePath, bool isWritable)
    {
        RootId = rootId;
        DisplayName = displayName;
        AbsolutePath = absolutePath;
        _isWritable = isWritable;
    }

    partial void OnIsWritableChanged(bool value) => WritableChanged?.Invoke(this, value);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this, EventArgs.Empty);
}

public record LanguageItem(string DisplayName, string Code);

/// <summary>
/// One paired device's file-sharing trust state, shown in the Settings trust-management list (2.1).
/// Toggling a switch raises an event the <see cref="SettingsViewModel"/> persists through the
/// <see cref="IFileTrustService"/>; the initial seed is suppressed so loading does not write back.
/// </summary>
public partial class FileTrustDeviceItem : ObservableObject
{
    public string ClientId { get; }

    /// <summary>Short, friendly identifier for a (non-technical) user — the leading chars of the client id.</summary>
    public string ShortId => ClientId.Length > 12 ? ClientId[..12] + "…" : ClientId;

    private readonly bool _seeding;

    [ObservableProperty]
    private bool _fullBrowseGranted;

    [ObservableProperty]
    private bool _autoAcceptIncoming;

    public event EventHandler<bool>? FullBrowseChanged;
    public event EventHandler<bool>? AutoAcceptChanged;
    public event EventHandler? RevokeRequested;

    public FileTrustDeviceItem(string clientId, bool fullBrowseGranted, bool autoAcceptIncoming)
    {
        ClientId = clientId;
        _seeding = true;
        FullBrowseGranted = fullBrowseGranted;
        AutoAcceptIncoming = autoAcceptIncoming;
        _seeding = false;
    }

    partial void OnFullBrowseGrantedChanged(bool value)
    {
        if (!_seeding)
            FullBrowseChanged?.Invoke(this, value);
    }

    partial void OnAutoAcceptIncomingChanged(bool value)
    {
        if (!_seeding)
            AutoAcceptChanged?.Invoke(this, value);
    }

    [RelayCommand]
    private void Revoke() => RevokeRequested?.Invoke(this, EventArgs.Empty);
}
