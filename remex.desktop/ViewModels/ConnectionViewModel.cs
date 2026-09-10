using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Core;
using Remex.Core.Exceptions;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Network;
using Remex.Core.Services.Security;
using Remex.Core.Validation;
using Remex.Desktop.Services.Security;

namespace Remex.Desktop.ViewModels;

// IFileTransferConnection is satisfied by members that already existed — the FileTransferMessageReceived
// event and SendAsync(RemexMessage). Declaring it adds no code here; it lets FileTransferClient depend
// on the two things it uses instead of the whole view model, so the download unwind path became
// testable (RemEx-qmnl).
public partial class ConnectionViewModel : ObservableValidator, IDisposable, IFileTransferConnection
{
    private const int MaxLatencyPoints = 30;
    private const int MaxReconnectDelaySeconds = 30;
    private ClientWebSocket? _webSocket;
    // Serializes all outbound sends. A WebSocket permits only one outstanding SendAsync at a time, so a
    // file-transfer chunk loop sending concurrently with a browse/ping/launcher/layout send throws
    // "There is already one outstanding 'SendAsync'..." and can fault the socket mid-transfer.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _reconnectCts;
    private bool _userDisconnected;
    private bool _isPairedWithCurrentHost;
    private string? _cachedLocalIpv4;

    // Snapshot of pinned host SPKI hashes captured immediately before each WebSocket connect
    // attempt. The TLS validation callback reads this synchronously, eliminating the
    // .GetAwaiter().GetResult() deadlock risk inside the callback.
    private IReadOnlyDictionary<string, string>? _pinSnapshot;
    // Set true when the user has explicitly initiated pairing for the current connect attempt.
    // Allows trust-on-first-use only when the operator opted in; otherwise the cert callback
    // fails closed on any unrecognized SPKI hash.
    private bool _allowFirstTimeTrustForCurrentConnect;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendPingCommand))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusAccessibleName))]
    private bool _isConnected;

    /// <summary>
    /// Accessible name for the connection status indicator: what it IS, plus its state.
    /// </summary>
    /// <remarks>
    /// The dot used to take its name from <see cref="StatusText"/>, a general-purpose transient
    /// message. A screen reader therefore announced whatever that last held - a command result, a
    /// pairing note - as the NAME of the connection indicator, which is both wrong and unstable:
    /// the same element answered to a different name minute to minute (RemEx-x12a).
    /// <para>
    /// Two states only, tracking <see cref="IsConnected"/>, which is exactly what the dot conveys
    /// visually through its <c>connected</c> class. Sighted and screen-reader users now get the same
    /// information from it rather than two different things.
    /// </para>
    /// </remarks>
    public string ConnectionStatusAccessibleName =>
        IsConnected
            ? LocalizationService.Instance["A11y_ConnectionConnected"]
            : LocalizationService.Instance["A11y_ConnectionDisconnected"];

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessageResourceType = typeof(Localization.Strings), ErrorMessageResourceName = nameof(Localization.Strings.Connection_PcAddressRequired))]
    [ValidWebSocketUri]
    private string _hostAddress = $"wss://localhost:{RemexConstants.DefaultPort}{RemexConstants.WebSocketPath}";

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance["Status_Disconnected"];

    [ObservableProperty]
    private string _latencyText = "—";

    [ObservableProperty]
    private bool _isAutoReconnecting;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _qrCodeImage;

    [ObservableProperty]
    private bool _showQrCode;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private HostCapabilities? _hostCapabilities;

    /// <summary>
    /// Active pairing PIN published by the in-process host. Null when no pairing is in progress.
    /// </summary>
    [ObservableProperty]
    // The PIN string is read by every countdown property through HasActivePairingPin, so it has to
    // announce them too. They stayed correct only because every call site happened to assign the
    // expiry on the adjacent line — and CommunityToolkit skips the whole setter body, hook and
    // notifications included, when a value is assigned unchanged (review).
    [NotifyPropertyChangedFor(nameof(HasActivePairingPin))]
    [NotifyPropertyChangedFor(nameof(IsPairingPinExpired))]
    [NotifyPropertyChangedFor(nameof(IsPairingPinExpiringSoon))]
    [NotifyPropertyChangedFor(nameof(PairingPinExpiresInText))]
    private string? _activePairingPin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePairingPin))]
    [NotifyPropertyChangedFor(nameof(PairingPinExpiresInText))]
    [NotifyPropertyChangedFor(nameof(IsPairingPinExpiringSoon))]
    [NotifyPropertyChangedFor(nameof(IsPairingPinExpired))]
    private DateTimeOffset? _activePairingExpiresAt;

    public bool HasActivePairingPin => !string.IsNullOrEmpty(ActivePairingPin);

    /// <summary>
    /// Whether the pairing-PIN panel should be visible. Auto-set to true when a PIN
    /// arrives so the user sees it without having to click; the user can dismiss it
    /// and re-open via <see cref="ShowPairingPinPanelCommand"/>.
    /// </summary>
    [ObservableProperty]
    private bool _showPairingPin;

    /// <summary>
    /// Stamps the issue time whenever an expiry arrives or is cleared.
    /// </summary>
    /// <remarks>
    /// HOOKED TO THE PROPERTY RATHER THAN WRITTEN AT EACH CALL SITE. There are ELEVEN places that
    /// assign ActivePairingExpiresAt — three attach paths, a poll, a reveal, and their teardowns —
    /// and pairing them by hand would mean eleven chances to add a twelfth and forget. A missed one
    /// would leave a stale issue time against a fresh expiry, so the countdown would measure a window
    /// that never existed: the failure would be a wrong number on screen, not a crash.
    /// </remarks>
    partial void OnActivePairingExpiresAtChanged(DateTimeOffset? value)
        => _activePairingIssuedAt = value is null ? null : PairingClock();

    /// <summary>
    /// Test-only seam (visible to <c>Remex.Desktop.Tests</c> via <c>InternalsVisibleTo</c>). The clock
    /// the pairing countdown reads. Not used by DI — production always gets <c>UtcNow</c>.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS, NO TEST CAN TELL DELEGATION FROM A HAND-ROLLED SUBTRACTION, and I found that out
    /// the way I keep finding these things out: I replaced the <see cref="PairingPinCountdown"/> call
    /// with a plain subtraction and every test still passed. They could not have failed — reading the
    /// real clock means the boundary cases the component exists for (expiry INCLUSIVE at the instant,
    /// a backwards clock unable to lend the PIN more life, a zero-length window being expired rather
    /// than unlimited) are unreachable, because time moves between constructing the state and
    /// asserting on it. With the clock frozen, that same injection fails two tests.
    /// <para>
    /// An <c>init</c> property rather than a constructor parameter: the container never sets it, and
    /// there is no shared state for the parallel runner to race on.
    /// </para>
    /// </remarks>
    internal Func<DateTimeOffset> PairingClock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>When this PIN was learned, so the countdown has a window to measure.</summary>
    /// <remarks>
    /// <see cref="PairingPinCountdown.Evaluate"/> takes an issue time and a validity window rather
    /// than an expiry instant. Which moment is called "issued" does not matter to the answer — the
    /// window is computed as <c>expiresAt - issuedAt</c>, so remaining always reduces to
    /// <c>expiresAt - now</c> — but it does matter that both come from the same reading, or an
    /// attach that finds a PIN already in flight would measure a window it never saw the start of.
    /// </remarks>
    private DateTimeOffset? _activePairingIssuedAt;

    /// <summary>
    /// What the countdown says right now: valid, nearly gone, or dead.
    /// </summary>
    /// <remarks>
    /// DELEGATED TO <see cref="PairingPinCountdown"/> RATHER THAN DECIDED HERE (RemEx-7ykyn). It has
    /// shipped with 9 tests and mutation verification since RemEx-scwy and was consumed by nothing —
    /// the fourth component found in that state in this repo. Its decisions are ones this property
    /// previously got wrong or did not make at all: expiry is INCLUSIVE, because the host stops
    /// accepting the PIN at the boundary and an off-by-one shows a countdown reading zero beside a
    /// PIN the host has already refused; a backwards clock CANNOT lend the PIN more life than it has,
    /// which the old subtraction would have done on an NTP correction; and a zero or negative window
    /// is EXPIRED rather than unlimited, because it almost certainly means a field that was never
    /// populated.
    /// </remarks>
    public PairingPinStatus PairingPinCountdownStatus =>
        _activePairingIssuedAt is null || ActivePairingExpiresAt is null
            ? new PairingPinStatus(PairingPinState.Expired, TimeSpan.Zero)
            : PairingPinCountdown.Evaluate(
                _activePairingIssuedAt.Value,
                ActivePairingExpiresAt.Value - _activePairingIssuedAt.Value,
                PairingClock());

    /// <summary>Localized "Expires in Ns" countdown, refreshed once per second by the timer.</summary>
    public string PairingPinExpiresInText
    {
        get
        {
            if (ActivePairingExpiresAt is null) return string.Empty;
            var seconds = (int)Math.Max(0, PairingPinCountdownStatus.Remaining.TotalSeconds);
            return string.Format(LocalizationService.Instance["Settings_PairingPinExpiresIn"], seconds);
        }
    }

    /// <summary>Whether the PIN is close enough to expiry to say so more loudly.</summary>
    /// <remarks>
    /// The threshold is 15 seconds and it was chosen from the TASK, not from a round number: read six
    /// digits off one screen and type them into a phone, possibly one-handed. A warning with three
    /// seconds left tells the user something they can no longer act on.
    /// </remarks>
    public bool IsPairingPinExpiringSoon =>
        PairingPinCountdownStatus.State == PairingPinState.ExpiringSoon;

    /// <summary>
    /// Whether the digits should be REPLACED by a "get a new one" action rather than shown.
    /// </summary>
    /// <remarks>
    /// REPLACE, NOT GREY OUT, and that is a decision rather than a style preference. An expired PIN
    /// rendered faintly is still six digits on a screen, and a user looking at their phone rather
    /// than at the PC will type them — the visual treatment carries no information to the person
    /// actually doing the task. <see cref="PairingPinCountdown.ShouldDisplayPin"/> owns the rule.
    /// </remarks>
    public bool IsPairingPinExpired =>
        HasActivePairingPin && !PairingPinCountdown.ShouldDisplayPin(PairingPinCountdownStatus.State);

    private DispatcherTimer? _pairingExpiryTimer;
    private DispatcherTimer? _standalonePairingPinPollingTimer;

    /// <summary>
    /// LAN address phones on the same network should use to reach this PC's host.
    /// Computed lazily from the loopback host address.
    /// </summary>
    /// <remarks>
    /// Recognises loopback with <see cref="IsLoopbackAddress"/> rather than its own literal list.
    /// It previously tested <c>uri.Host is "localhost" or "127.0.0.1" or "::1"</c>, which missed two
    /// forms and, on missing them, fell through to returning the address UNCHANGED — offering a
    /// loopback URL as the address to type into a phone, which can never connect and reports no
    /// error (RemEx-eskd). <c>Uri.Host</c> returns the bracketed <c>"[::1]"</c> for an IPv6 literal
    /// so that arm never matched anything, and all of <c>127.0.0.0/8</c> is loopback while only
    /// <c>127.0.0.1</c> was listed.
    ///
    /// LATENT IN THIS PROPERTY ONLY, and the distinction matters: nothing binds
    /// <see cref="LanHostAddress"/> today (RemEx-19al decides whether to wire it up or delete it),
    /// so no user hit it HERE. The identical defect in <c>GenerateQrCodeAsync</c> was NOT latent —
    /// that command is bound in two views and put the loopback host straight into the pairing QR
    /// payload. Both are fixed under RemEx-eskd. Do not read "latent" as applying to the defect.
    ///
    /// This is the display half of the split described on RemEx-19pj and grants no privilege: the
    /// only thing widening it changes is whether the LAN IP is substituted into a string shown to
    /// the user. The pairing-bypass and trust-on-first-use gate is <see cref="IsLoopbackHost"/>,
    /// which is deliberately NOT changed here — it is behind operator sign-off (RemEx-19pj).
    /// </remarks>
    public string? LanHostAddress
    {
        get
        {
            try
            {
                var uri = new Uri(HostAddress);
                if (IsLoopbackAddress(uri))
                {
                    var ip = _cachedLocalIpv4 ??= GetLocalIpv4Address();
                    if (ip is null) return null;
                    var port = uri.Port > 0 ? uri.Port : RemexConstants.DefaultPort;
                    return $"{uri.Scheme}://{ip}:{port}{uri.AbsolutePath}";
                }
                return HostAddress;
            }
            catch { return null; }
        }
    }

    partial void OnHostAddressChanged(string value) => OnPropertyChanged(nameof(LanHostAddress));

    private IPairingService? _pairingService;
    private IPairingPinQueryService? _standalonePairingPinQueryService;

    /// <summary>
    /// Whether either pairing path has been wired up, which is what gates the button.
    /// </summary>
    /// <remarks>
    /// **THE PAIRING BUTTON USED TO BE HIDDEN WHENEVER <c>IsConnected</c> WAS TRUE, WHICH IS ALMOST
    /// ALWAYS (RemEx-f66j, item 4).** That flag is the desktop's own WebSocket to its embedded host —
    /// up essentially always, per RemEx-porg — so "show the pairing PIN" vanished for most users most
    /// of the time. It was not merely inverted, it was the wrong property: <c>RevealPairingPinAsync</c>
    /// goes to <c>IPairingService</c> in-process, or to <c>IPairingPinQueryService</c> over IPC, and
    /// neither one touches that socket. Worse, on the reading where the gate looked deliberate — show
    /// it only while disconnected — the button appeared exactly when the services behind it were least
    /// likely to be attached.
    /// <para>
    /// BE PRECISE ABOUT WHAT THIS PROVES, because an overclaim here is how the last gate drifted
    /// (review). It says a service object was attached — NOT that a PIN can actually be produced. On
    /// Windows <c>IPairingPinQueryService</c> is an unconditional DI singleton, so the attach always
    /// runs and this is effectively always true; if no host is behind it, the command fails and says
    /// so via <c>Status_FailedGeneratePin</c>. That is the right trade for this button: one that
    /// explains itself beats one that is not there, which is the entire bug being fixed.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private bool _canRevealPairingPin;

    /// <summary>
    /// Subscribes to pairing-pin events on the in-process host's PairingService so the
    /// desktop UI can show the user the PIN their phone is asking for.
    /// </summary>
    public void AttachEmbeddedPairingService(IPairingService service)
    {
        Guard.NotNull(service);
        StopStandalonePairingPinPolling();
        _pairingService = service;
        CanRevealPairingPin = true;
        service.PinDisplayed += (pin, expires) =>
            Dispatcher.UIThread.Post(() =>
            {
                ActivePairingPin = pin;
                ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires);
                ShowPairingPin = true;
                StartPairingExpiryTimer();
            });
        service.PinCleared += () =>
            Dispatcher.UIThread.Post(() =>
            {
                ClearActivePairingPin();
            });

        if (service.TryGetActivePinInfo(out var activePin, out var activeExpiresAt))
        {
            ActivePairingPin = activePin;
            ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activeExpiresAt);
            ShowPairingPin = true;
            StartPairingExpiryTimer();
        }
        else
        {
            ClearActivePairingPin();
        }
    }

    public void AttachStandalonePairingPinQueryService(IPairingPinQueryService service)
    {
        Guard.NotNull(service);
        _standalonePairingPinQueryService = service;
        CanRevealPairingPin = true;
    }

    public void StartStandalonePairingPinPolling()
    {
        if (_standalonePairingPinQueryService is null || _standalonePairingPinPollingTimer is not null)
        {
            return;
        }

        _standalonePairingPinPollingTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            async (_, _) => await RefreshStandalonePairingPinAsync());
        _standalonePairingPinPollingTimer.Start();
    }

    public async Task RefreshStandalonePairingPinAsync()
    {
        if (_standalonePairingPinQueryService is null)
        {
            return;
        }

        try
        {
            var activePin = await _standalonePairingPinQueryService.GetActivePairingPinAsync();
            var applySnapshot = () =>
            {
                if (activePin is not null)
                {
                    if (ActivePairingPin != activePin.Pin || ActivePairingExpiresAt != DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs))
                    {
                        ActivePairingPin = activePin.Pin;
                        ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs);
                        ShowPairingPin = true;
                        StartPairingExpiryTimer();
                    }
                    return;
                }

                if (HasActivePairingPin && (_pairingService is null || !_pairingService.IsPairingActive))
                {
                    ClearActivePairingPin();
                }
            };

            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            {
                applySnapshot();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(applySnapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Standalone pairing PIN refresh failed.");
        }
    }

    /// <summary>
    /// Retires the active PIN and everything that encodes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE TEARDOWN, BECAUSE THERE ARE FOUR PATHS AND I ONLY FIXED ONE. RemEx-hprgb retired the QR
    /// when the PIN EXPIRED — but the PIN is also cleared when the pairing session is CONSUMED, which
    /// is the success path of every pairing, and that handler stops the expiry timer, so the retire
    /// I had added became unreachable. A phone would pair successfully and the QR encoding the
    /// now-burned PIN would stay on screen indefinitely, ready to hand the next scan a dead code
    /// (review). Hoisting the teardown is what stops a fifth caller appearing without it.
    /// </para>
    /// <para>
    /// The QR goes and the PIN string does not, on the expiry path only — see
    /// <see cref="OnPairingExpiryTick"/>. Everywhere else the PIN is genuinely gone, so both go.
    /// </para>
    /// </remarks>
    internal void ClearActivePairingPinForTests() => ClearActivePairingPin();

    private void ClearActivePairingPin()
    {
        StopPairingExpiryTimer();
        CloseQrCode();
        ActivePairingPin = null;
        ActivePairingExpiresAt = null;
        ShowPairingPin = false;
    }

    /// <summary>
    /// One tick of the pairing countdown. Internal so a test can drive it with a frozen clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT NO LONGER TEARS THE STATE DOWN AT THE BOUNDARY, and that was a real bug found in review.
    /// The tick used to publish <see cref="IsPairingPinExpired"/> and then, in the SAME callback,
    /// null the PIN and set <c>ShowPairingPin = false</c>. Both boundaries are inclusive and fire on
    /// the same instant, and no render pass runs inside one callback — so the "this PIN has expired,
    /// get a new one" branch was never painted. The user saw the whole card vanish, which is exactly
    /// the outcome the bead's recorded decision rejected, and there was no route back: reopening the
    /// panel is gated on <c>HasActivePairingPin</c>, which had just been cleared.
    /// </para>
    /// <para>
    /// The PIN string is now kept so the panel can say it is dead and offer a replacement. Keeping
    /// six expired digits in memory costs nothing — they are already on screen, and the host has
    /// stopped accepting them, so they are no more sensitive than any other number.
    /// </para>
    /// <para>
    /// NINE GREEN TESTS SAID NOTHING ABOUT THIS because every one of them drove view-model
    /// properties and none drove the timer. Extracting the body is what makes the tick reachable.
    /// </para>
    /// </remarks>
    internal void OnPairingExpiryTick()
    {
        // ALL THREE, not just the text. IsPairingPinExpiringSoon and IsPairingPinExpired are also
        // functions of the current time, and the second decides whether the digits are on screen at
        // all — a tick that refreshed only the caption would leave the digits up beside a line
        // saying they had expired.
        OnPropertyChanged(nameof(PairingPinExpiresInText));
        OnPropertyChanged(nameof(IsPairingPinExpiringSoon));
        OnPropertyChanged(nameof(IsPairingPinExpired));

        // The countdown, not a second subtraction: one boundary rule, stated once.
        if (IsPairingPinExpired)
        {
            // THE QR CARRIES THE SAME PIN, so it dies with it (RemEx-hprgb). The two panels are
            // independent Borders and can both be open, so leaving the QR up meant the PC said "this
            // PIN has expired" while offering, twelve pixels away, a code encoding that exact PIN. A
            // phone that scanned it got a pairing failure with nothing on either screen explaining
            // why. The PIN itself is deliberately KEPT so the panel can say it is dead and offer a
            // replacement; the QR is not, because a QR cannot say anything — it can only be scanned.
            // TIMER FIRST, then the work that could in principle fail. A throw out of CloseQrCode
            // with the timer still running would re-enter this callback every second and take the
            // dispatcher down with it; unconditional teardown should never sit downstream of
            // anything fallible (review).
            StopPairingExpiryTimer();
            CloseQrCode();
        }
    }

    private void StartPairingExpiryTimer()
    {
        if (_pairingExpiryTimer is not null) return;
        _pairingExpiryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPairingExpiryTick());
        _pairingExpiryTimer.Start();
    }

    private void StopPairingExpiryTimer()
    {
        _pairingExpiryTimer?.Stop();
        _pairingExpiryTimer = null;
    }

    private void StopStandalonePairingPinPolling()
    {
        _standalonePairingPinPollingTimer?.Stop();
        _standalonePairingPinPollingTimer = null;
    }

    [RelayCommand]
    private void ShowPairingPinPanel() => ShowPairingPin = HasActivePairingPin;

    [RelayCommand]
    public async Task RevealPairingPinAsync()
    {
        // 1. If embedded host is active, start pairing directly on it
        if (_pairingService is not null)
        {
            try
            {
                var state = await _pairingService.GetOrStartPairingAsync(default);
                ActivePairingPin = state.Pin;
                ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(state.ExpiresAtUnixMs);
                ShowPairingPin = true;
                StartPairingExpiryTimer();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start embedded pairing session.");
                AnnouncePairingProblem(LocalizationService.Instance["Status_FailedGeneratePin"]);
            }
        }
        // 2. If standalone host query service is active, request it over IPC
        else if (_standalonePairingPinQueryService is not null)
        {
            try
            {
                var activePin = await _standalonePairingPinQueryService.GeneratePairingPinAsync();
                if (activePin is not null)
                {
                    ActivePairingPin = activePin.Pin;
                    ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs);
                    ShowPairingPin = true;
                    StartPairingExpiryTimer();
                }
                else
                {
                    AnnouncePairingProblem(LocalizationService.Instance["Status_FailedGeneratePinHost"]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start standalone pairing session.");
                AnnouncePairingProblem(LocalizationService.Instance["Status_FailedGeneratePinHost"]);
            }
        }
        else
        {
            AnnouncePairingProblem(LocalizationService.Instance["Status_PairingServiceUnavailable"]);
        }
    }

    [RelayCommand]
    private void ClosePairingPin() => ShowPairingPin = false;

    /// <summary>Rolling window of latency samples (ms) for charting.</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    /// <summary>Hosts discovered via mDNS; populated after <see cref="DiscoverHostsCommand"/> completes.</summary>
    public ObservableCollection<string> DiscoveredHosts { get; } = new();

    /// <summary>Recently used connection addresses (most-recent first, max 10).</summary>
    public ObservableCollection<Remex.Core.Models.ConnectionProfile> ConnectionHistory { get; } = new();

    private readonly IMdnsDiscoveryService? _discoveryService;
    private readonly Remex.Desktop.Services.DashboardLayoutService? _layoutService;
    private readonly ILogger<ConnectionViewModel> _logger;

    public ConnectionViewModel() : this(null, null, null) { }

    public ConnectionViewModel(
        IMdnsDiscoveryService? discoveryService,
        Remex.Desktop.Services.DashboardLayoutService? layoutService,
        ILogger<ConnectionViewModel>? logger = null)
    {
        _discoveryService = discoveryService;
        _layoutService = layoutService;
        _logger = logger ?? NullLogger<ConnectionViewModel>.Instance;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Unconditional, and deliberately OUTSIDE the idle guard below. This one derives from
        // IsConnected, so [NotifyPropertyChangedFor] on _isConnected only re-raises it when the
        // connection actually flips - never on a language switch. A screen reader would keep
        // announcing the indicator's name in the previous language until the state happened to
        // change, which for a stable connection is indefinitely (RemEx-6ddx).
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ConnectionStatusAccessibleName));

            // Re-raised for the same reason, from different triggers: HostRuntimeSummary only fires
            // when HostCapabilities changes, so on a stable connection it would hold the previous
            // language indefinitely. PairingPinExpiresInText is refreshed every second by the
            // countdown, so it self-heals in under a second - included anyway, because a rule with
            // an exemption list is a rule nobody can check mechanically.
            OnPropertyChanged(nameof(HostRuntimeSummary));
            OnPropertyChanged(nameof(PairingPinExpiresInText));
        });

        // Refresh idle status when language changes
        if (!IsConnecting && !IsAutoReconnecting)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = IsConnected
                    ? LocalizationService.Instance["Status_Connected"]
                    : LocalizationService.Instance["Status_Disconnected"];
            });
        }
    }

    [RelayCommand]
    private async Task DiscoverHostsAsync()
    {
        if (_discoveryService == null)
        {
            StatusText = LocalizationService.Instance["Status_DiscoveryUnavailable"];
            return;
        }

        StatusText = LocalizationService.Instance["Status_SearchingHosts"];
        var foundHosts = await _discoveryService.DiscoverHostsAsync(TimeSpan.FromSeconds(5));
        var defaultAddress = $"wss://localhost:{RemexConstants.DefaultPort}{RemexConstants.WebSocketPath}";

        Dispatcher.UIThread.Post(() =>
        {
            DiscoveredHosts.Clear();
            foreach (var host in foundHosts)
                DiscoveredHosts.Add(host);

            if (foundHosts.Any())
            {
                var firstHost = foundHosts.First();
                if (string.IsNullOrWhiteSpace(HostAddress) || HostAddress == defaultAddress)
                {
                    HostAddress = firstHost;
                    StatusText = string.Format(LocalizationService.Instance["Status_FoundHostFormat"], firstHost);
                }
                else
                {
                    StatusText = string.Format(LocalizationService.Instance["Status_FoundMultipleHostsFormat"], foundHosts.Count);
                }
            }
            else
            {
                StatusText = LocalizationService.Instance["Status_NoHostsFound"];
            }
        });
    }

    public event Action<System.Collections.Generic.List<Remex.Core.Models.AppEntry>>? LauncherEntriesReceived;
    public event Action<TelemetryPayload>? TelemetryReceived;
    public event Action<Remex.Core.Models.DashboardProfile>? LayoutProfileReceived;
    public event Action<System.Collections.Generic.List<Remex.Core.Models.ProcessInfo>>? ProcessListReceived;
    public event Action<Remex.Core.Messages.RemexMessage>? FileTransferMessageReceived;

    [ObservableProperty]
    private ObservableCollection<Remex.Core.Models.ProcessInfo> _processes = new();

    public async Task RequestProcessListAsync()
    {
        var msg = new RemexMessage { Type = MessageTypes.ProcessListRequest };
        await SendGuardedAsync(msg);
    }

    [RelayCommand]
    public async Task LockAsync() => await SendPowerCommandAsync("Lock", "Wol_LockSent");

    [RelayCommand]
    public async Task SleepAsync() => await SendPowerCommandAsync("Sleep", "Wol_SleepSent");

    [RelayCommand]
    public async Task HibernateAsync() => await SendPowerCommandAsync("Hibernate", "Wol_HibernateSent");

    [RelayCommand]
    public async Task SignOutAsync() => await SendPowerCommandAsync("SignOut", "Wol_SignOutSent");

    [RelayCommand]
    public async Task ShutdownAsync() => await SendPowerCommandAsync("Shutdown", "Wol_ShutdownSent");

    [RelayCommand]
    public async Task ForceShutdownAsync() => await SendPowerCommandAsync("ForceShutdown", "Wol_ForceShutdownSent");

    [RelayCommand]
    public async Task RestartAsync() => await SendPowerCommandAsync("Restart", "Wol_RestartSent");

    // ForceRestart existed only on RemoteViewModel, bound straight from RemoteView.axaml, so the
    // command palette - which wires exclusively through shell.Connection.* - had no way to reach it and
    // was missing an action every other surface has. (RemEx-6cda.)
    [RelayCommand]
    public async Task ForceRestartAsync() => await SendPowerCommandAsync("ForceRestart", "Wol_ForceRestartSent");

    [RelayCommand]
    public async Task RestartToUefiAsync() => await SendPowerCommandAsync("RestartToUefi", "Wol_RebootUefiSent");

    /// <summary>
    /// Sends a power command and reports the outcome, in both directions.
    /// </summary>
    /// <remarks>
    /// The command palette and the Canvas lock button invoke these <c>[RelayCommand]</c>s directly,
    /// so the <c>(ok, message)</c> tuple <see cref="SendCommandAsync"/> returns has to be surfaced
    /// HERE. Discarding it made the palette silent whether the command worked or not - including
    /// when <see cref="SendCommandAsync"/> returns <c>(false, "Not connected")</c>, which is the
    /// case a user most needs to be told about. (RemEx-diyv.)
    ///
    /// Both directions are reported deliberately: a failure-only message would be inconsistent with
    /// silence on success, and would leave the user unable to tell "it worked" from "nothing
    /// happened". <c>RemoteViewModel.ExecuteRemoteCommandAsync</c> surfaces the same tuple into its
    /// own <c>WolStatusText</c> for the Remote screen's buttons, which take a different path and are
    /// unaffected by this - the two paths are disjoint, so nothing is reported or recorded twice.
    /// </remarks>
    private async Task SendPowerCommandAsync(string action, string sentMessageKey)
    {
        var (ok, message) = await SendCommandAsync(action);

        StatusText = ok
            ? string.Format(
                LocalizationService.Instance["Wol_SuccessFormat"],
                LocalizationService.Instance[sentMessageKey])
            : string.Format(LocalizationService.Instance["Wol_ErrorFormat"], message);

        // Only accepted commands reach the Home activity feed, matching RemoteViewModel: the feed
        // is a record of what the PC actually did, not of what was attempted.
        // Fully qualified: this file imports System.Diagnostics, which has its own ActivityKind.
        if (ok)
            ActivityService.Instance.Record(Services.ActivityKind.CommandRun, action);
    }

    // WakeOnLanAsync used to live here, sending "WakeOnLan" with no parameters on the guess that
    // the host "might use defaults". It does not: PingPongHandler's WAKEONLAN branch returns
    // (false, "Missing MacAddress parameter.") when no MAC is supplied, so the command could never
    // do anything. Its only caller was the command palette, and the tuple was discarded, so it
    // failed silently on every invocation since it was written. Removed rather than repaired,
    // because a working palette entry needs the Remote screen's configured MAC and its
    // not-connected local-send fallback - see RemEx-efse. Wake-on-LAN itself is
    // unaffected and still works from the Remote screen. (RemEx-paa7.)

    /// <param name="expectedName">
    /// The process name the user was shown, sent so the host can refuse if the PID has changed hands
    /// since. Omitted when null — the host then kills unverified, which is what it did for every
    /// client before RemEx-druh.
    /// </param>
    /// <param name="expectedStartUnixMs">
    /// When this client last saw that PID start. Lets the host tell a RELAUNCH of the same
    /// program into the same PID from the instance the user actually confirmed.
    /// </param>
    public async Task<Remex.Core.Models.IPC.CommandResponse> KillProcessWithResponseAsync(
        int processId,
        bool elevated = false,
        string? expectedName = null,
        long? expectedStartUnixMs = null)
    {
        if (_webSocket?.State != WebSocketState.Open) return new Remex.Core.Models.IPC.CommandResponse(false, "Not connected", null);
        var parameters = new System.Collections.Generic.Dictionary<string, string> { { "ProcessId", processId.ToString() } };

        // The client-side re-check in TaskManagerViewModel cannot close this on its own: it is
        // separated from the kill by a network round trip, and the PID can change hands inside it.
        // Sending the name lets the host check identity and kill in one step (RemEx-druh).
        if (!string.IsNullOrWhiteSpace(expectedName))
            parameters["ExpectedName"] = expectedName;

        // Omitted rather than sent as 0 when unknown: the host treats an absent value as
        // unchecked, and 0 would be a real timestamp that matches nothing (RemEx-on4n).
        if (expectedStartUnixMs is long startMs)
            parameters["ExpectedStartUnixMs"] = startMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var msg = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = elevated ? "KillProcessElevated" : "KillProcess",
            CommandParameters = parameters
        };
        try
        {
            var response = await SendCommandAndWaitAsync(msg);
            return new Remex.Core.Models.IPC.CommandResponse(response.CommandSuccess ?? false, response.CommandMessage ?? "", null);
        }
        catch (OperationCanceledException)
        {
            return new Remex.Core.Models.IPC.CommandResponse(false, "Timeout waiting for server response", null);
        }
    }

    [ObservableProperty]
    private double _averageLatency;

    [ObservableProperty]
    private double _maxLatency;

    public bool SupportsRemoteDesktop => HostCapabilities?.SupportsRemoteDesktop ?? true;

    /// <summary>
    /// The localized label for how the host is running. Note that the wire value "service" is
    /// legacy naming for a non-interactive/Session-0 Windows process, NOT a service install —
    /// there is none (RemEx-9z0f). Exposed so callers show this instead of the raw wire token.
    /// </summary>
    /// <remarks>
    /// NOT BOUND IN XAML, and correctly so - it is consumed by <c>AboutViewModel.UpdateHostVersion</c>
    /// and by <see cref="HostRuntimeSummary"/> below, both of which surface it through their own bound
    /// properties. Recorded because a scan for "localized property that no view binds" flags this and
    /// reads as dead code twice over (RemEx-r5pm); it is not.
    /// </remarks>
    public string HostRuntimeLabel => HostCapabilities?.RuntimeMode switch
    {
        "interactive" => LocalizationService.Instance["Status_InteractiveHost"],
        "service" => LocalizationService.Instance["Status_ServiceHost"],
        "headless" => LocalizationService.Instance["Status_HeadlessHost"],
        _ => LocalizationService.Instance["Status_Host"]
    };

    /// <summary>
    /// The host's operating system, named rather than spelled the way the wire spells it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HostCapabilitiesProvider.GetPlatform</c> emits the lowercase tokens <c>windows</c>,
    /// <c>linux</c> and <c>macos</c>. Those are wire values, and interpolating one straight into a
    /// sentence gave the About page "2.4.0 (windows, Interactive)" — lowercase mid-sentence beside a
    /// correctly-cased label, and untranslated in all eight non-English locales. The runtime half of
    /// that same string was given a localized label by RemEx-9z0f; this is the platform half, which
    /// was left behind (RemEx-6s34).
    /// </para>
    /// <para>
    /// AN UNKNOWN PLATFORM FALLS BACK TO THE RAW TOKEN rather than to a generic word. A host running
    /// on something this client has no name for should still say what it is — "freebsd" is
    /// imperfect but diagnosable, whereas "Unknown" throws the information away.
    /// </para>
    /// </remarks>
    public string HostPlatformLabel => HostCapabilities?.Platform switch
    {
        "windows" => LocalizationService.Instance["Status_PlatformWindows"],
        "linux" => LocalizationService.Instance["Status_PlatformLinux"],
        "macos" => LocalizationService.Instance["Status_PlatformMacOs"],
        var other when !string.IsNullOrWhiteSpace(other) => other,
        _ => LocalizationService.Instance["Status_Host"],
    };

    public string HostRuntimeSummary
    {
        get
        {
            if (HostCapabilities is null)
            {
                return IsConnected ? LocalizationService.Instance["Status_ConnectedToHost"] : LocalizationService.Instance["Status_HostNotConnected"];
            }

            // A localized FORMAT rather than an interpolated " on ": the English word was hardcoded
            // here, and its word order does not survive translation - Hindi and Turkish both put the
            // platform first. (RemEx-6s34)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Instance["Status_HostRuntimeOnPlatform"],
                HostRuntimeLabel,
                HostPlatformLabel);
        }
    }

    /// <remarks>
    /// NOT BOUND IN XAML, and correctly so - consumed by <c>SettingsViewModel.UpdateHostCapabilitySummary</c>
    /// and by <c>RemoteDesktopViewModel</c>, which expose it through their own bound properties. Flagged
    /// as apparently-dead by the RemEx-6ddx scan and checked; it is not (RemEx-r5pm).
    /// </remarks>
    public string RemoteDesktopAvailabilitySummary =>
        SupportsRemoteDesktop
            ? LocalizationService.Instance["Status_RemoteDesktopAvailable"]
            : HostCapabilities?.RemoteDesktopUnavailableReason
                ?? LocalizationService.Instance["Status_RemoteDesktopUnavailable"];

    private TelemetryPayload? _telemetry;
    public TelemetryPayload? Telemetry
    {
        get => _telemetry;
        set
        {
            _telemetry = value;
            OnPropertyChanged(nameof(Telemetry));
        }
    }

    partial void OnHostCapabilitiesChanged(HostCapabilities? value)
    {
        OnPropertyChanged(nameof(SupportsRemoteDesktop));
        OnPropertyChanged(nameof(HostRuntimeSummary));
        OnPropertyChanged(nameof(RemoteDesktopAvailabilitySummary));
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    private void SaveConnectionToHistory()
    {
        const int MaxHistoryEntries = 10;
        var address = HostAddress;

        var existing = ConnectionHistory.FirstOrDefault(h => h.HostAddress == address);
        if (existing != null)
            ConnectionHistory.Remove(existing);

        ConnectionHistory.Insert(0, new Remex.Core.Models.ConnectionProfile
        {
            Name = address,
            HostAddress = address,
            LastConnected = DateTime.Now
        });

        while (ConnectionHistory.Count > MaxHistoryEntries)
            ConnectionHistory.RemoveAt(ConnectionHistory.Count - 1);

        if (_layoutService != null)
        {
            var profile = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
            _layoutService.RequestSave(profile with { ConnectionHistory = ConnectionHistory.ToList() });
        }
    }
    private bool CanDisconnect() => IsConnected || IsConnecting;

    public System.Net.WebSockets.WebSocket? GetWebSocket() => _webSocket;
    public async Task<(bool Success, string Message)> SendCommandAsync(string action, System.Collections.Generic.Dictionary<string, string>? parameters = null)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return (false, LocalizationService.Instance["Status_NotConnected"]);

        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.Command,
                CommandAction = action,
                CommandParameters = parameters,
                Timestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
            };
            var response = await SendCommandAndWaitAsync(msg);
            return (response.CommandSuccess ?? false, response.CommandMessage ?? LocalizationService.Instance["Status_NoMessage"]);
        }
        catch (OperationCanceledException)
        {
            return (false, LocalizationService.Instance["Status_CommandTimedOut"]);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error sending command {Action}", action);
            return (false, $"Network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation sending command {Action}", action);
            return (false, $"Invalid operation: {ex.Message}");
        }
        // Let unexpected exceptions propagate
    }

    private bool CanSendPing() => IsConnected;

    // ---------------------------------------------------------------------------
    // Correlated command/response infrastructure
    // ---------------------------------------------------------------------------

    /// <summary>
    /// How long to wait for a command response before giving up.
    /// </summary>
    /// <remarks>
    /// Settable so <c>ConnectionViewModelTests</c> can shorten it; production never assigns it. It
    /// is a <see cref="TimeSpan"/> rather than a count of seconds purely so the timeout test costs
    /// milliseconds rather than adding ten seconds to the suite (RemEx-h01r).
    /// </remarks>
    internal TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private IWebSocketSender? _outboundSender;

    /// <summary>
    /// How outbound messages reach the wire. Defaults to the real guarded socket send.
    /// </summary>
    /// <remarks>
    /// Named for commands when it was introduced (RemEx-h01r) because correlation was the only
    /// caller. <see cref="SendAsync"/> now routes through it too, which is what makes file-transfer
    /// wire behaviour observable at all: <see cref="SendGuardedAsync"/> returns early when the
    /// socket is not open, so against a disconnected view model every send silently no-ops and a
    /// test can prove nothing about what was or was not put on the wire. That gap is why RemEx-mubp
    /// - a cancel emitted before the transfer it names - could exist with the suite green.
    /// <para>
    /// NOT EVERY OUTBOUND MESSAGE, despite the name. A few callers still reach
    /// <see cref="SendGuardedAsync"/> directly - the process-list request, the ping, and the layout
    /// update - so stubbing this does not silence them. They were left alone because routing them here
    /// changes production paths for no test that needs it yet; route them when something does.
    /// </para>
    /// </remarks>
    internal IWebSocketSender OutboundSender
    {
        get => _outboundSender ??= new GuardedSocketSender(this);
        set => _outboundSender = value;
    }

    /// <summary>The production sender: the existing lock-guarded socket write, behind the seam.</summary>
    private sealed class GuardedSocketSender(ConnectionViewModel owner) : IWebSocketSender
    {
        public Task SendAsync(RemexMessage message, CancellationToken ct)
            => owner.SendGuardedAsync(message, ct);
    }

    /// <summary>
    /// Pending command awaiters keyed by correlation ID.
    /// Replaces the former single <c>_pendingCommandResponse</c> field so concurrent
    /// callers no longer overwrite each other.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _pendingCommands = new();

    /// <summary>
    /// Stamps a correlation ID onto <paramref name="msg"/>, registers a TCS, sends the
    /// message, and awaits the matching response with a <see cref="CommandTimeout"/>
    /// timeout.  Cleans up the dictionary entry regardless of outcome.
    /// </summary>
    internal async Task<RemexMessage> SendCommandAndWaitAsync(RemexMessage msg, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[correlationId] = tcs;
        try
        {
            await OutboundSender.SendAsync(msg with { CorrelationId = correlationId }, ct);
            try
            {
                return await tcs.Task.WaitAsync(CommandTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new OperationCanceledException("Command timed out.");
            }
        }
        finally
        {
            _pendingCommands.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Hands a <c>command_response</c> to the caller waiting on its correlation ID.
    /// </summary>
    /// <remarks>
    /// Called by the receive loop, and directly by tests. Keeping it as ONE method is the point:
    /// a test that re-implemented this matching would keep passing while the real loop broke.
    /// </remarks>
    internal void DeliverCommandResponse(RemexMessage message)
    {
        if (message.CorrelationId is string cid
            && _pendingCommands.TryGetValue(cid, out var matchedTcs))
        {
            // Normal path: correlation ID present and matches a pending request
            matchedTcs.TrySetResult(message);
        }
        else if (message.CorrelationId is null && !_pendingCommands.IsEmpty)
        {
            // Fallback for hosts that do not echo correlation IDs back in their
            // CommandResponse messages (i.e. unpatched / older host versions).
            // LIMITATION: With multiple concurrent in-flight commands this path
            // delivers the response to at most one caller (the first whose TCS
            // accepts it); all remaining concurrent callers will eventually time
            // out.  Upgrade the host so it echoes CorrelationId to avoid this.
            if (_pendingCommands.Count > 1)
                Debug.WriteLine(
                    "[ConnectionViewModel] WARNING: Fallback correlation path taken with " +
                    $"{_pendingCommands.Count} concurrent in-flight commands. " +
                    "Only one caller will receive this response; the rest will time out. " +
                    "Upgrade the host to a version that echoes CorrelationId.");

            foreach (var entry in _pendingCommands)
            {
                if (entry.Value.TrySetResult(message))
                    break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // Validate inputs before attempting connection
        ValidateAllProperties();
        if (HasErrors)
        {
            var errors = GetErrors(nameof(HostAddress))
                .Cast<ValidationResult>()
                .Select(e => e.ErrorMessage)
                .FirstOrDefault();
            StatusText = errors ?? LocalizationService.Instance["Status_InvalidSettings"];
            return;
        }

        _userDisconnected = false;
        IsConnecting = true;
        HostCapabilities = null;
        StopReconnecting();

        // Define CTS outside try to be accessible in catch
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _receiveCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts.Token, timeoutCts.Token);

        try
        {
            StatusText = LocalizationService.Instance["Status_Connecting"];
            var uri = await PrepareTlsValidationForConnectAsync(allowTrustOnFirstUseForEmptyStore: true);
            _webSocket = CreateConfiguredWebSocket();

            await _webSocket.ConnectAsync(uri, linkedCts.Token);

            // Loopback connections target the in-process embedded host on the same machine.
            // Pairing exists to bootstrap trust with a *remote* host, so it adds no security
            // here — and would prompt the user for a PIN their own desktop generated.
            if (IsLoopbackHost(uri))
                _isPairedWithCurrentHost = true;

            UseInProcessTelemetryIfLocal(uri);

            if (!_isPairedWithCurrentHost)
            {
                StatusText = LocalizationService.Instance["Status_Pairing"];
                var certStore = App.Services.GetRequiredService<Remex.Desktop.Services.Security.PinnedCertStore>();
                var pairingClient = new Remex.Core.Native.PairingClient(_webSocket, null);

                var response = await pairingClient.StartPairingAsync(Environment.MachineName, "2.0.0", linkedCts.Token);
                if (response == null)
                {
                    StatusText = LocalizationService.Instance["Status_PairingFailed"];
                    Cleanup();
                    return;
                }

                var pairingResult = await PairWithDialogAsync(pairingClient, response, linkedCts.Token);
                switch (pairingResult)
                {
                    case Remex.Desktop.ViewModels.PairingDialogResult.Failed:
                        StatusText = LocalizationService.Instance["Status_PairingFailed"];
                        Cleanup();
                        return;
                    case Remex.Desktop.ViewModels.PairingDialogResult.Cancelled:
                        StatusText = LocalizationService.Instance["Status_PairingCancelled"];
                        Cleanup();
                        return;
                }

                // Pairing successful, save the SPKI hash!
                await certStore.SetPinAsync(response.HostId, response.CertificateSpkiHashBase64);
                _isPairedWithCurrentHost = true;
            }

            IsConnected = true;
            IsConnecting = false;
            StatusText = LocalizationService.Instance["Status_Connected"];
            LatencyText = "—";

            SaveConnectionToHistory();

            // Start background receive loop.
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = linkedCts.Token.IsCancellationRequested && !timeoutCts.Token.IsCancellationRequested
                ? LocalizationService.Instance["Status_ConnectionCancelled"]
                : LocalizationService.Instance["Status_ConnectionTimedOut"];
            Cleanup();
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket connection failed to {HostAddress}", HostAddress);
            StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], "Connection failed");
            Cleanup();
        }
        catch (UriFormatException ex)
        {
            _logger.LogError(ex, "Invalid WebSocket URI: {HostAddress}", HostAddress);
            StatusText = LocalizationService.Instance["Status_InvalidHostAddress"] ?? "Invalid host address format";
            Cleanup();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid WebSocket state during connection");
            StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message);
            Cleanup();
        }
        // Let unexpected exceptions (OutOfMemoryException, etc.) propagate to app-level handler
        finally
        {
            IsConnecting = false;
            // Always close the trust window on exit, even if ConnectAsync threw.
            _allowFirstTimeTrustForCurrentConnect = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        _userDisconnected = true;
        StopReconnecting();

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "User disconnected",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Best-effort close - WebSocket already in bad state
        }
        catch (OperationCanceledException)
        {
            // Best-effort close - operation was cancelled
        }
        catch (ObjectDisposedException)
        {
            // Best-effort close - WebSocket already disposed
        }

        Cleanup();
        StatusText = IsConnecting ? LocalizationService.Instance["Status_ConnectionCancelled"] : LocalizationService.Instance["Status_Disconnected"];
        LatencyText = "—";
    }

    [RelayCommand(CanExecute = nameof(CanSendPing))]
    private async Task SendPingAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            var ping = new RemexMessage
            {
                Type = MessageTypes.Ping,
                Timestamp = Stopwatch.GetTimestamp(),
            };
            await SendGuardedAsync(ping);
            StatusText = LocalizationService.Instance["Status_PingSent"];
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send ping message");
            StatusText = string.Format(LocalizationService.Instance["Status_SendErrorFormat"], "Network error");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation sending ping");
            StatusText = string.Format(LocalizationService.Instance["Status_SendErrorFormat"], ex.Message);
        }
    }

    public Task SendAsync(RemexMessage message) => OutboundSender.SendAsync(message, CancellationToken.None);

    /// <summary>
    /// The single outbound-send choke point. Acquires <see cref="_sendLock"/> so only one
    /// WebSocket.SendAsync is ever in flight — every caller (file-transfer chunks, browse, ping,
    /// commands, launcher/layout) routes through here instead of touching the socket directly.
    /// </summary>
    private async Task SendGuardedAsync(RemexMessage message, CancellationToken ct = default)
    {
        var ws = _webSocket;
        if (ws?.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            await MessageSerializer.SendAsync(ws, message, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendLayoutUpdateAsync(Remex.Core.Models.DashboardProfile profile)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.LayoutUpdate,
                DashboardProfile = profile,
            };
            await SendGuardedAsync(msg);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send layout update to host");
            Debug.WriteLine($"Failed to send layout update: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation sending layout update");
            Debug.WriteLine($"Failed to send layout update: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await MessageSerializer.ReceiveAsync(_webSocket, ct);

                if (message is null)
                    break;

                switch (message.Type)
                {
                    case MessageTypes.Pong when message.Timestamp.HasValue:
                        var elapsed = Stopwatch.GetElapsedTime(message.Timestamp.Value);
                        var ms = elapsed.TotalMilliseconds;
                        Dispatcher.UIThread.Post(() =>
                        {
                            LatencyText = $"{ms:F1} ms";
                            StatusText = string.Format(LocalizationService.Instance["Status_PongFormat"], ms);
                            PushLatency(ms);
                        });
                        break;

                    case MessageTypes.Pong:
                        Dispatcher.UIThread.Post(() =>
                        {
                            LatencyText = LocalizationService.Instance["Status_PongNoTimestamp"];
                            StatusText = LocalizationService.Instance["Status_Pong"];
                        });
                        break;

                    case MessageTypes.Telemetry when message.Telemetry is not null:
                        Dispatcher.UIThread.Post(() =>
                        {
                            Telemetry = message.Telemetry;
                            TelemetryReceived?.Invoke(message.Telemetry);
                        });
                        break;

                    case MessageTypes.CommandResponse:
                        DeliverCommandResponse(message);
                        break;

                    case MessageTypes.LauncherSync when message.LauncherEntries is not null:
                        Dispatcher.UIThread.Post(() => LauncherEntriesReceived?.Invoke(message.LauncherEntries));
                        break;

                    case MessageTypes.ProcessListSync when message.ProcessList is not null:
                        Dispatcher.UIThread.Post(() =>
                        {
                            Processes = new ObservableCollection<Remex.Core.Models.ProcessInfo>(message.ProcessList);
                            ProcessListReceived?.Invoke(message.ProcessList);
                        });
                        break;

                    case MessageTypes.HostInfo when message.HostCapabilities is not null:
                        Dispatcher.UIThread.Post(() => HostCapabilities = message.HostCapabilities);
                        break;

                    case MessageTypes.LayoutSync when message.DashboardProfile is not null:
                        Dispatcher.UIThread.Post(() => LayoutProfileReceived?.Invoke(message.DashboardProfile));
                        break;

                    case MessageTypes.FileBrowseResponse:
                    case MessageTypes.FileRootsResponse:
                    case MessageTypes.FileTransferChunk:
                    case MessageTypes.FileTransferEnd:
                    case MessageTypes.FileTransferProgress:
                    case MessageTypes.FileManageResponse:
                    case MessageTypes.FileHashResponse:
                    case MessageTypes.FileRootManageResponse:
                    // ── 2.1 File Sharing Overhaul (protocolVersion 3) responses — additive; older
                    //    hosts never send these, so v2 peers are unaffected. ──
                    case MessageTypes.FileVolumesResponse:
                    case MessageTypes.FileSearchResponse:
                    case MessageTypes.FileManifestResponse:
                    case MessageTypes.FileMetadataResponse:
                    case MessageTypes.FileThumbnailResponse:
                        FileTransferMessageReceived?.Invoke(message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect
            _logger.LogDebug("Receive loop cancelled during shutdown");
        }
        catch (WebSocketException ex)
        {
            // Connection lost
            _logger.LogWarning(ex, "WebSocket connection lost in receive loop");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize message from host");
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], LocalizationService.Instance["Status_InvalidMessageFormat"]);
            });
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error in receive loop");
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = string.Format(LocalizationService.Instance["Status_ReceiveErrorFormat"], "Connection error");
            });
        }
        // Let unexpected exceptions propagate to app-level handler

        // If we exited the loop because the server closed, update UI state.
        if (IsConnected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Cleanup();
                StatusText = LocalizationService.Instance["Status_ServerClosed"];
                LatencyText = "—";
            });

            // Auto-reconnect unless the user explicitly disconnected.
            if (!_userDisconnected)
            {
                _ = ReconnectLoopAsync();
            }
        }
    }

    /// <summary>
    /// Attempts to connect automatically on app startup.
    /// Retries with exponential backoff until connected or cancelled.
    /// </summary>
    public async Task AutoConnectAsync()
    {
        _userDisconnected = false;
        await ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        StopReconnecting();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        int delay = 2;

        Dispatcher.UIThread.Post(() => IsAutoReconnecting = true);

        try
        {
            while (!ct.IsCancellationRequested && !IsConnected)
            {
                Dispatcher.UIThread.Post(() => StatusText = string.Format(LocalizationService.Instance["Status_ReconnectingFormat"], delay));
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                if (ct.IsCancellationRequested) break;

                ClientWebSocket? ws = null;
                try
                {
                    Dispatcher.UIThread.Post(() => StatusText = LocalizationService.Instance["Status_Connecting"]);
                    var uri = await PrepareTlsValidationForConnectAsync(allowTrustOnFirstUseForEmptyStore: false);
                    ws = CreateConfiguredWebSocket();

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(uri, connectCts.Token);

                    if (IsLoopbackHost(uri))
                        _isPairedWithCurrentHost = true;

                    UseInProcessTelemetryIfLocal(uri);

                    // Success — adopt the new socket.
                    _webSocket = ws;
                    _receiveCts = new CancellationTokenSource();
                    HostCapabilities = null;

                    Dispatcher.UIThread.Post(() =>
                    {
                        IsConnected = true;
                        IsAutoReconnecting = false;
                        StatusText = LocalizationService.Instance["Status_Connected"];
                        LatencyText = "—";
                    });

                    _ = ReceiveLoopAsync(_receiveCts.Token);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    ws?.Dispose();
                    // Exponential backoff: 2, 4, 8, 16, 30, 30, ...
                    delay = Math.Min(delay * 2, MaxReconnectDelaySeconds);
                }
                finally
                {
                    _allowFirstTimeTrustForCurrentConnect = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled.
        }

        Dispatcher.UIThread.Post(() => IsAutoReconnecting = false);
    }

    private void StopReconnecting()
    {
        CancelAndDispose(ref _reconnectCts);
        IsAutoReconnecting = false;
    }

    private void PushLatency(double ms)
    {
        if (LatencyHistory.Count >= MaxLatencyPoints)
            LatencyHistory.RemoveAt(0);

        LatencyHistory.Add(ms);
        AverageLatency = LatencyHistory.Average();
        MaxLatency = LatencyHistory.Max();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellationTokenSource)
    {
        var current = Interlocked.Exchange(ref cancellationTokenSource, null);
        if (current is null)
            return;

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another teardown path already disposed this source.
        }

        current.Dispose();
    }

    private static void DisposeWebSocket(ref ClientWebSocket? webSocket)
    {
        var current = Interlocked.Exchange(ref webSocket, null);
        if (current is null)
            return;

        try
        {
            current.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Another teardown path already disposed this socket.
        }
    }

    private void Cleanup()
    {
        StopInProcessTelemetry();
        CancelAndDispose(ref _receiveCts);
        DisposeWebSocket(ref _webSocket);

        // Cancel all in-flight command awaiters so callers don't hang after disconnect
        foreach (var (_, pendingTcs) in _pendingCommands)
            pendingTcs.TrySetCanceled();
        _pendingCommands.Clear();

        IsConnected = false;
        HostCapabilities = null;
    }

    /// <summary>
    /// Reports a pairing failure where the user actually is (RemEx-7ykyn, item 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// **<see cref="StatusText"/> ALONE WAS NOT FEEDBACK.** It is a line inside a window that RemEx
    /// closes to the tray by default, so for most of this app's running life it announced pairing
    /// failures to a surface nobody was looking at. A user who pressed "Pair a phone", switched to
    /// their phone, and found nothing happening had no way to learn why. Routing it through
    /// <see cref="NotificationService"/> lets <see cref="NotificationRouter"/> make that call:
    /// <see cref="NotificationImportance.Problem"/> is the one importance that may interrupt, so it
    /// becomes a toast when the window is up and a tray balloon when it is not.
    /// </para>
    /// <para>
    /// THE STATUS LINE STAYS, AND IS NOT A DUPLICATE. It is the RECORD — the thing still on screen
    /// after a balloon has faded, and the thing the diagnostics export reads. Suppressing an
    /// announcement is a UI decision; suppressing the record is the RemEx-43ha defect.
    /// </para>
    /// <para>
    /// **THE PIN NEVER GOES IN A NOTIFICATION.** Only failures are announced here. A balloon or a
    /// toast carrying six digits would put the pairing secret on a lock screen, in a notification
    /// centre and in whatever history the OS keeps — the PIN is screen-only, in front of whoever is
    /// sitting at the PC, and that is the whole basis on which it is safe to show at all.
    /// </para>
    /// </remarks>
    /// <returns>Always true, so a caller can record that the user WAS told without a second flag.</returns>
    private bool AnnouncePairingProblem(string message)
    {
        StatusText = message;
        NotificationService.Instance.Notify(
            NotificationImportance.Problem,
            LocalizationService.Instance["Notify_PairingFailedTitle"],
            message);
        return true;
    }

    /// <summary>
    /// Opens the PIN panel for the session the QR code is about to encode (RemEx-7ykyn, item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE QR AND THE PIN ARE ONE ACT. <see cref="GenerateQrCodeAsync"/> has always started a pairing
    /// session and set <see cref="ActivePairingPin"/> — the payload carries that very PIN — but it left
    /// this panel hidden, so the two lived behind separate buttons and whichever the user pressed gave
    /// them half the answer. A phone that cannot scan (bad light, no camera permission, a tablet
    /// across the room) needs the digits, and the digits it needs are these, not a second session's.
    /// </para>
    /// <para>
    /// CALLED WHERE THE PIN IS OBTAINED, NOT AFTER THE IMAGE IS BUILT. Everything past this point can
    /// fail — the payload serialize, the encoder, the bitmap decode — and all of it is about drawing a
    /// picture. Revealing the digits at the end would mean a rendering failure cost the user the PIN
    /// as well as the code, when the PIN is the half that still works.
    /// </para>
    /// </remarks>
    private void RevealPinAlongsideTheCode() => ShowPairingPin = HasActivePairingPin;

    [RelayCommand]
    private async Task GenerateQrCodeAsync()
    {
        try
        {
            // THE PAIRING SESSION FIRST, THEN THE PICTURE (RemEx-7ykyn, item 3). This block used to
            // sit below the host and certificate lookup, so anything that threw up there — and
            // App.Services is dereferenced with no null guard — cost the user the PIN as well as the
            // code. The PIN is the half that works without a camera, so it must not depend on the
            // drawing succeeding. It is also what makes the join testable: every step below needs an
            // initialised Avalonia platform and this one does not.
            string? pairingPin = null;

            // A REAL FLAG, NOT A StatusText EMPTINESS PROBE (review). The first version gated the
            // fallback message on string.IsNullOrEmpty(StatusText) — and StatusText is initialised to
            // "Disconnected" and never set to empty by any of its 38 assignments, so that branch could
            // not run. With no pairing service attached at all, pressing the button produced silence:
            // no PIN, no code, no message. That is the exact defect this bead exists to remove,
            // reintroduced by the guard meant to prevent it.
            var announced = false;
            if (_pairingService is not null)
            {
                try
                {
                    var state = await _pairingService.GetOrStartPairingAsync(default);
                    pairingPin = state.Pin;
                    ActivePairingPin = state.Pin;
                    ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(state.ExpiresAtUnixMs);
                    StartPairingExpiryTimer();
                    RevealPinAlongsideTheCode();
                }
                catch (Exception ex)
                {
                    // AND SAY SO (review). This used to log and fall through to draw a QR carrying
                    // pin=null — a code that scans and then pairs with nothing, with no word on
                    // either screen. The separate PIN button reported this; collapsing the two
                    // buttons would otherwise have deleted the only feedback the surface had.
                    _logger.LogWarning(ex, "Failed to start embedded pairing session for QR code.");
                    announced |= AnnouncePairingProblem(LocalizationService.Instance["Status_FailedGeneratePin"]);
                }
            }
            else if (_standalonePairingPinQueryService is not null)
            {
                try
                {
                    var activePin = await _standalonePairingPinQueryService.GeneratePairingPinAsync();
                    if (activePin is not null)
                    {
                        pairingPin = activePin.Pin;
                        ActivePairingPin = activePin.Pin;
                        ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs);
                        StartPairingExpiryTimer();
                        RevealPinAlongsideTheCode();
                    }
                    else
                    {
                        // The null return had no else at all (review): the host answered "no pin" and
                        // the surface said nothing while drawing a dead code anyway.
                        announced |= AnnouncePairingProblem(LocalizationService.Instance["Status_FailedGeneratePinHost"]);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start standalone pairing session for QR code.");
                    announced |= AnnouncePairingProblem(LocalizationService.Instance["Status_FailedGeneratePinHost"]);
                }
            }

            // NO PIN, NO CODE — AND CHECKED HERE, BEFORE ANYTHING ELSE CAN FAIL (review). Sitting
            // lower down it was still correct about not drawing a dead code, but the address and
            // certificate lookups ran first and their own catch blocks overwrote StatusText: the user
            // was told the QR had failed when what actually happened was that the host produced no
            // PIN. Found by an exact-string assertion; the NotBeEmpty form it replaced was blind to it.
            // NO PIN, NO CODE (review). A QR encoding pin=null scans perfectly and then pairs with
            // nothing, which is a worse answer than no code: the user believes they have done their
            // part. The branches above have already said why there is no PIN.
            if (pairingPin is null)
            {
                // Covers the branch nothing else does: neither service attached, or one attached and
                // returning a null pin. Whatever the reason, the user pressed a button and must not
                // be met with silence.
                if (!announced)
                    AnnouncePairingProblem(LocalizationService.Instance["Status_PairingServiceUnavailable"]);
                ShowQrCode = false;
                return;
            }

            var uri = new Uri(HostAddress);
            // GetLocalIpv4Address by method group, NOT the _cachedLocalIpv4 field. The address is
            // derived from the outbound route, so it changes on VPN up/down, Wi-Fi/Ethernet switch,
            // dock/undock and DHCP renewal — and remex.agent autostarts at logon and runs all
            // session. Caching it would make every later QR encode a dead address with no error on
            // either end, which is the exact symptom this fix exists to remove, reintroduced by
            // another route. A UDP connect that sends no data is not worth a staleness window here.
            var host = ResolvePhoneReachableHost(uri, GetLocalIpv4Address);
            var port = uri.Port > 0 ? uri.Port : RemexConstants.DefaultPort;

            var certService = App.Services.GetService<ICertificateService>() // optional service
                           ?? App.EmbeddedHostServices?.GetService<ICertificateService>(); // optional service
            var spkiHash = certService?.GetSpkiSha256Base64() ?? "";

            var payload = JsonSerializer.Serialize(new
            {
                host,
                port,
                hostId = Environment.MachineName,
                spkiHashBase64 = spkiHash,
                pin = pairingPin,
            });

            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new PngByteQRCode(qrCodeData);
            var pngBytes = qrCode.GetGraphic(10);

            using var ms = new MemoryStream(pngBytes);
            var oldBitmap = QrCodeImage;
            QrCodeImage = new Avalonia.Media.Imaging.Bitmap(ms);
            oldBitmap?.Dispose();
            ShowQrCode = true;
        }
        catch (UriFormatException ex)
        {
            // THE CONNECT PATH ALREADY CATCHES THIS AND THIS ONE DID NOT (review). UriFormatException
            // derives from FormatException, so none of the three catches below saw it — and escaping
            // an async [RelayCommand] rethrows on the dispatcher, which takes the app down. Reachable
            // by typing an address with no scheme and pressing pair without connecting first: the
            // validation attribute records an error but does not block the setter, and only
            // ConnectCommand consults it.
            _logger.LogError(ex, "Cannot build a pairing QR for an unparseable host address.");
            AnnouncePairingProblem(LocalizationService.Instance["Status_InvalidHostAddress"]);
            ShowQrCode = false;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to serialize QR code payload");
            StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], LocalizationService.Instance["Status_InvalidQrData"]);
            ShowQrCode = false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Invalid argument generating QR code");
            StatusText = string.Format(LocalizationService.Instance["Status_QrCodeFailed"], "Invalid data");
            ShowQrCode = false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to generate QR code image");
            Debug.WriteLine($"Failed to generate QR code: {ex}");
            StatusText = string.Format(LocalizationService.Instance["Status_QrCodeFailed"], ex.Message);
            ShowQrCode = false;
        }
    }

    [RelayCommand]
    private void CloseQrCode()
    {
        ShowQrCode = false;
        var old = QrCodeImage;
        QrCodeImage = null;
        old?.Dispose();
    }

    /// <summary>
    /// Returns the machine's preferred outbound LAN IPv4 address by connecting a
    /// UDP socket (no data sent) so the OS selects the correct local interface.
    /// </summary>
    private static string? GetLocalIpv4Address()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The host a PHONE should be told to connect to, given the address this PC's UI is using.
    /// Loopback becomes this machine's LAN IPv4 where one exists, because a phone cannot reach
    /// loopback; anything else is already reachable and is returned unchanged.
    /// </summary>
    /// <remarks>
    /// Extracted and made <c>internal</c> so it can be tested directly. The QR path is where this
    /// defect actually shipped — <c>GenerateQrCodeCommand</c> is bound in ConnectionView and
    /// SettingsView — but its only output is a rendered QR bitmap, so asserting on the substituted
    /// host through the command would mean decoding a PNG and standing up an Avalonia render stack.
    /// A named function is both cheaper to test and clearer about what the step is for.
    ///
    /// It previously inlined <c>host is "localhost" or "127.0.0.1" or "::1"</c>, which missed the
    /// bracketed IPv6 form (<c>Uri.Host</c> keeps the brackets, so <c>"::1"</c> never matched) and
    /// everything in <c>127.0.0.0/8</c> past <c>127.0.0.1</c>. On a miss the loopback host went into
    /// the pairing payload unchanged and the phone scanned an address it can never reach — no error,
    /// just a pairing that silently cannot work (RemEx-eskd).
    ///
    /// The LAN address is supplied lazily because obtaining it opens a socket; a non-loopback host
    /// must not pay for that.
    /// </remarks>
    internal static string ResolvePhoneReachableHost(Uri uri, Func<string?> lanIpv4Provider) =>
        IsLoopbackAddress(uri) ? lanIpv4Provider() ?? uri.Host : uri.Host;

    private static bool IsLoopbackHost(Uri uri) =>
        uri.Host is "localhost" or "127.0.0.1" or "::1";

    /// <summary>
    /// True when this URI addresses this machine over loopback — the SAME set the host recognises
    /// with <c>IPAddress.IsLoopback</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="IsLoopbackHost"/>, which tests three literal strings and
    /// is therefore NARROWER in two ways that matter here: <c>Uri.Host</c> returns the bracketed
    /// <c>"[::1]"</c> for an IPv6 literal, so that arm has never matched anything, and
    /// <c>IPAddress.IsLoopback</c> accepts all of <c>127.0.0.0/8</c> rather than just
    /// <c>127.0.0.1</c>. Connecting to <c>wss://[::1]:5005/ws</c> or <c>wss://127.0.0.2:5005/ws</c>
    /// would leave the host treating the connection as loopback and skipping the telemetry stream
    /// while this UI waited for one — a dashboard that never populates behind a healthy "Connected".
    ///
    /// The two are not merged because <see cref="IsLoopbackHost"/> also gates the pairing bypass and
    /// trust-on-first-use, which CLAUDE.md places behind explicit sign-off. Aligning it is filed as
    /// RemEx-19pj and is waiting on the operator, not on anyone's analysis.
    ///
    /// THE FULL INVENTORY, because a vague "don't duplicate this" is what let the same miss survive
    /// in four places at once. These are the sites that CARRY OR ONCE CARRIED their own literal
    /// copy of the predicate — not every site that asks the question, since
    /// <c>UseInProcessTelemetryIfLocal</c> asks it too but has always delegated here:
    ///
    ///   1. <see cref="IsLoopbackHost"/> — 3 literals. Gates the pairing bypass and
    ///      trust-on-first-use. NOT aligned; waiting on operator sign-off (RemEx-19pj).
    ///   2. <c>RemoteDesktopService.PrepareTlsValidationAsync</c> (:423) — 3 literals. Gates
    ///      trust-on-first-use for /ws/desktop. Also NOT aligned, same reason, and it was missing
    ///      from RemEx-19pj's stated scope until RemEx-eskd added it.
    ///   3. This method — correct.
    ///   4. <see cref="LanHostAddress"/> — was a literal copy, now calls this (RemEx-eskd).
    ///   5. <c>GenerateQrCodeAsync</c> — was a literal copy, now calls this (RemEx-eskd). This is
    ///      the one that actually shipped the bug: the QR command is bound in two views, so a
    ///      loopback host went into the pairing payload and the phone scanned an unreachable
    ///      address.
    ///
    /// So two literal copies remain and both are on the sign-off side. Do not add a sixth: the
    /// bracketed-IPv6 miss survived precisely because each site re-derived the predicate.
    /// </remarks>
    private static bool IsLoopbackAddress(Uri uri) => uri.IsLoopback;

    /// <summary>The broadcaster we are currently subscribed to, if any.</summary>
    private Remex.Core.Services.ITelemetryBroadcaster? _inProcessTelemetry;

    /// <summary>
    /// Takes telemetry straight from the host in this process instead of off the socket, when the
    /// socket is pointed at ourselves (RemEx-ite8).
    /// </summary>
    /// <remarks>
    /// GATED ON LOOPBACK, and that is not a nicety. This reports the sample for the machine the UI is
    /// running on, so doing it while connected to another host would show this PC's readings under
    /// that PC's name — wrong data with nothing to indicate it. The host makes the matching decision
    /// from the connection's remote IP and stops streaming telemetry to loopback clients, so the two
    /// predicates MUST agree — see <see cref="IsLoopbackAddress"/> for why the obvious one does not.
    ///
    /// If the host is not in this process the subscription simply does not happen and the socket
    /// keeps feeding the dashboard, which is also what a non-loopback connection does.
    /// </remarks>
    private void UseInProcessTelemetryIfLocal(Uri uri)
    {
        StopInProcessTelemetry();

        if (!IsLoopbackAddress(uri))
            return;

        try
        {
            var broadcaster = Remex.Desktop.Services.EmbeddedHostServiceLocator
                .Require<Remex.Core.Services.ITelemetryBroadcaster>();

            broadcaster.TelemetryPublished += OnInProcessTelemetry;
            _inProcessTelemetry = broadcaster;

            // Seed from the sample already taken, so the dashboard is populated at once instead of
            // sitting on "Collecting Data" until the next tick.
            var current = broadcaster.CurrentTelemetry;
            if (current is not null)
                OnInProcessTelemetry(current);
        }
        catch (InvalidOperationException ex)
        {
            // No embedded host in this process. Nothing is broken: the socket still carries telemetry.
            _logger.LogDebug(ex, "No in-process telemetry available; using the socket.");
        }
    }

    private void StopInProcessTelemetry()
    {
        if (_inProcessTelemetry is null)
            return;

        _inProcessTelemetry.TelemetryPublished -= OnInProcessTelemetry;
        _inProcessTelemetry = null;
    }

    /// <summary>Raised on the sampler's thread, so everything it touches is posted to the UI thread.</summary>
    private void OnInProcessTelemetry(TelemetryPayload payload) =>
        Dispatcher.UIThread.Post(() =>
        {
            Telemetry = payload;
            TelemetryReceived?.Invoke(payload);
        });

    /// <summary>
    /// TLS server-certificate validation callback. Enforces SPKI pinning against the snapshot
    /// captured by <see cref="LoadPinSnapshotAsync"/> immediately before each socket connect.
    ///
    /// Security model:
    ///   - If the snapshot contains the incoming cert's SPKI hash, accept (matched pin).
    ///   - If the snapshot is non-empty and the hash does not match, reject (MITM defence).
    ///   - If the snapshot is empty AND the operator has opted into first-time trust for this
    ///     connect attempt, accept; the downstream PIN-based pairing handshake protects against
    ///     MITM in this trust-on-first-use window.
    ///   - Any other state (no snapshot loaded, opt-in not granted) → reject.
    ///
    /// The callback is fully synchronous: no <c>.GetAwaiter().GetResult()</c>, no DI lookup,
    /// no async I/O. Pins must be loaded into <see cref="_pinSnapshot"/> before the connect call.
    /// </summary>
    private bool AcceptSelfSignedCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null) return false;

        var hashBase64 = CertificatePinPolicy.ComputeSpkiHash(certificate);

        // The rule itself is shared with the Remote Desktop channel rather than restated here: both
        // channels reach the same host, and the one time they each kept their own copy the copies
        // disagreed (RemEx-mlce, RemEx-xmgw).
        var pins = _pinSnapshot;
        var accepted = CertificatePinPolicy.IsCertificateAcceptable(
            hashBase64, pins, _allowFirstTimeTrustForCurrentConnect);

        // The pairing-state update stays at the call site on purpose. A pure predicate that also
        // mutated this flag is how the two validators drifted; only a MATCHED PIN counts as paired,
        // so accepting on an empty store (trust-on-first-use) deliberately leaves it false.
        _isPairedWithCurrentHost = CertificatePinPolicy.IsPairedHost(accepted, pins);

        if (pins is null)
        {
            // ConnectAsync must populate the snapshot before invoking ConnectAsync on the socket.
            // Hitting this branch is a programming error; fail closed.
            _logger.LogError(
                "TLS validation callback invoked without a pin snapshot. Rejecting cert {Hash}.",
                hashBase64);
        }
        else if (accepted && pins.Count == 0)
        {
            // Empty pin store and the operator explicitly opted into first-time trust for this
            // connect attempt. The PIN-based pairing handshake that follows provides the MITM
            // protection in this window.
            _logger.LogInformation(
                "First-time pairing: accepting cert SPKI {Hash} for pairing handshake.",
                hashBase64);
        }
        else if (!accepted && pins.Count > 0)
        {
            _logger.LogError(
                "Rejecting host certificate: SPKI {Hash} does not match any pinned host. " +
                "If the host certificate has legitimately rotated, the operator must re-pair.",
                hashBase64);
        }
        else if (!accepted)
        {
            _logger.LogError(
                "Rejecting host certificate: SPKI {Hash} not pinned and first-time trust not granted.",
                hashBase64);
        }

        return accepted;
    }

    /// <summary>
    /// Loads the current pinned-host snapshot synchronously usable by the TLS callback. Must be
    /// called before each <c>ClientWebSocket.ConnectAsync</c> invocation.
    /// </summary>
    private async Task LoadPinSnapshotAsync()
    {
        var store = App.Services.GetService<Remex.Desktop.Services.Security.PinnedCertStore>(); // optional service
        if (store is null)
        {
            // No pin store configured (e.g. a misconfigured DI container). Empty snapshot will
            // cause the validation callback to fail closed unless first-time-trust is granted.
            _pinSnapshot = new Dictionary<string, string>();
            return;
        }

        _pinSnapshot = await store.GetAllPinsAsync();
    }

    private async Task<Uri> PrepareTlsValidationForConnectAsync(bool allowTrustOnFirstUseForEmptyStore)
    {
        var uri = new Uri(HostAddress);
        await LoadPinSnapshotAsync();
        _allowFirstTimeTrustForCurrentConnect =
            IsLoopbackHost(uri) ||
            (allowTrustOnFirstUseForEmptyStore && _pinSnapshot is not null && _pinSnapshot.Count == 0);
        return uri;
    }

    private ClientWebSocket CreateConfiguredWebSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.RemoteCertificateValidationCallback = AcceptSelfSignedCertificate;
        return socket;
    }

    /// <summary>
    /// Opens the pairing dialog and hands it the local delegate that verifies a PIN, then awaits its
    /// <see cref="Remex.Desktop.ViewModels.PairingDialogViewModel.ResultTask"/> (RemEx-x6a70.1). The
    /// dialog owns the whole verify/retry loop now — this method only reports the final true/false.
    /// </summary>
    private async Task<Remex.Desktop.ViewModels.PairingDialogResult> PairWithDialogAsync(
        Remex.Core.Native.PairingClient pairingClient,
        Remex.Core.Models.PairingResponse response,
        CancellationToken cancellationToken)
    {
        var result = Remex.Desktop.ViewModels.PairingDialogResult.Cancelled;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new Remex.Desktop.Views.PairingDialog
            {
                DataContext = new Remex.Desktop.ViewModels.PairingDialogViewModel(
                    (pin, ct) => pairingClient.CompletePairingAsync(pin, response, ct),
                    cancellationToken)
            };

            var shell = App.Services.GetService<Remex.Desktop.ViewModels.ShellViewModel>(); // optional service
            if (shell != null && App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow != null)
                {
                    result = await dialog.ShowDialog<Remex.Desktop.ViewModels.PairingDialogResult>(desktop.MainWindow);
                    return;
                }
            }
            // Fallback when no owner window is available — a single-view (non-window) lifetime, or
            // a null ShellViewModel / MainWindow. Not reachable in practice: this UI only runs as
            // the PC's classic desktop app, where a MainWindow is always present by the time pairing
            // is requested. If it ever were taken, the method reports pairing as cancelled.
        });
        return result;
    }

    public void Dispose()
    {
        // Not covered by Cleanup(): Dispose deliberately does a subset of it and never calls it. The
        // broadcaster is a process-lifetime singleton, so a handler left attached here keeps firing
        // into a disposed view model every second for the rest of the run (RemEx-ite8).
        StopInProcessTelemetry();
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        CancelAndDispose(ref _receiveCts);
        CancelAndDispose(ref _reconnectCts);
        StopStandalonePairingPinPolling();
        DisposeWebSocket(ref _webSocket);
    }
}
