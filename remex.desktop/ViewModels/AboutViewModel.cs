using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Services.Security;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Security;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the About page displaying version information and app details.
/// </summary>
public partial class AboutViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private readonly ShellViewModel _shell;

    /// <summary>
    /// An explicitly supplied certificate service (tests only). When null, the service is looked up
    /// from the containers on every read — see <see cref="ResolveCertificateService"/>.
    /// </summary>
    private readonly ICertificateService? _injectedCertificateService;

    [ObservableProperty]
    private string _clientVersion = "unknown";

    [ObservableProperty]
    private string _hostVersion = "Disconnected";

    /// <summary>
    /// This PC's own certificate fingerprint, in the grouped short form the Android client shows
    /// (RemEx-n8xk). Not localized: it is base64 plus the <c>(none)</c> marker, and the whole point
    /// is that it reads the same here as it does on the phone.
    /// </summary>
    [ObservableProperty]
    private string _hostFingerprint = SpkiFingerprintDisplay.Unavailable;

    [ObservableProperty]
    private bool _isShowShortcutsOpen;

    // ── Update check (RemEx-<update-checker>) ──────────────────────────────────
    private readonly UpdateCheckService? _updateService;
    private string? _downloadUrl;

    /// <summary>True while a check is running — the view disables the button and shows progress.</summary>
    [ObservableProperty]
    private bool _isCheckingForUpdate;

    /// <summary>True only when a newer release exists — gates the Download button and highlight.</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    /// <summary>Whether any status line (checking / up-to-date / available / failed) should show.</summary>
    [ObservableProperty]
    private bool _hasUpdateStatus;

    /// <summary>Localized status message for the update card.</summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    public ObservableCollection<WhatsNewItem> WhatsNewItems { get; } = new();

    /// <summary>Localized FAQ question/answer pairs, merged into About from the former standalone FAQ page.</summary>
    public ObservableCollection<FaqItem> FaqItems { get; } = new();

    /// <summary>The shared connection view-model (used for the host-version display).</summary>
    public ConnectionViewModel Connection => _connection;

    /// <param name="certificateService">
    /// Injected by tests. Left null in the app, where it is resolved the same way
    /// <c>ConnectionViewModel</c> resolves it for the pairing QR code: from the app container first,
    /// then from the embedded host's, because the host registers it and the two containers are
    /// separate.
    /// </param>
    public AboutViewModel(
        ConnectionViewModel connection,
        ShellViewModel shell,
        ICertificateService? certificateService = null)
    {
        _connection = connection;
        _shell = shell;
        _injectedCertificateService = certificateService;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        // Live language switching: the What's New / FAQ lists are built from localized strings
        // once, so rebuild them when the culture changes.
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        // Get client version from assembly, in the same three-part form the Android About screen
        // shows for the same release (e.g. "2.4.0") rather than a four-part "2.4.0.0".
        var version = AppVersion.Display;
        ClientVersion = string.IsNullOrEmpty(version) ? "unknown" : version;

        // The update check runs at startup (App.InitializeAppAsync); pick up any cached result so the
        // card is populated the instant About opens, and stay subscribed for the startup check that may
        // still be in flight. Resolved from the container like the other App.Services lookups here.
        _updateService = App.Services?.GetService(typeof(UpdateCheckService)) as UpdateCheckService;
        if (_updateService != null)
        {
            _updateService.ResultChanged += OnUpdateResultChanged;
            if (_updateService.LastResult != null)
                ApplyUpdateResult(_updateService.LastResult);
        }

        UpdateHostVersion();
        UpdateHostFingerprint();
        LoadWhatsNew();
        LoadFaq();
    }

    /// <summary>
    /// Finds the certificate service, app container first, then the embedded host's.
    /// </summary>
    /// <remarks>
    /// RESOLVED ON EVERY READ, NOT CACHED IN THE CONSTRUCTOR (review of RemEx-n8xk). The two
    /// containers are separate and the host registers the service in its own, so a view model built
    /// before the host publishes its container would cache null — and because
    /// <c>ShellViewModel</c> keeps one About instance for the session, that null would never be
    /// revisited. Re-resolving costs a dictionary lookup on a page the user opens by hand.
    /// </remarks>
    private ICertificateService? ResolveCertificateService()
        => _injectedCertificateService
            ?? FromContainer(App.Services)
            ?? FromContainer(App.EmbeddedHostServices);

    private static ICertificateService? FromContainer(IServiceProvider? provider)
        => provider?.GetService(typeof(ICertificateService)) as ICertificateService;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetCulture raises "Item", "Item[]" and "" in sequence; rebuild once per switch.
        if (!string.IsNullOrEmpty(e.PropertyName)) return;

        WhatsNewItems.Clear();
        LoadWhatsNew();
        FaqItems.Clear();
        LoadFaq();
        UpdateHostVersion();
        // Re-localize the update status line for the new culture.
        if (_updateService?.LastResult != null)
            ApplyUpdateResult(_updateService.LastResult);
    }
    
    private void LoadWhatsNew()
    {
        // Pull the highlights straight from the bundled CHANGELOG so this stays current every release.
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Remex.Desktop/Assets/CHANGELOG.md"));
            using var reader = new StreamReader(stream);
            foreach (var entry in ParseChangelog(reader.ReadToEnd(), maxItems: 12))
                WhatsNewItems.Add(entry);
            if (WhatsNewItems.Count > 0)
                return;
        }
        catch (Exception ex)
        {
            // Benign - the static highlights below are a real fallback - but recorded anyway, because
            // "the About page shows the wrong release notes" is otherwise unexplainable from a log.
            InMemoryLogSink.Append(LogLevel.Debug, "About",
                "Could not read the bundled changelog; falling back to static highlights", ex);
        }

        WhatsNewItems.Add(new WhatsNewItem(
            LocalizationService.Instance["About_WhatsNew_Features"],
            LocalizationService.Instance["About_WhatsNew_Features_Body"]));
        WhatsNewItems.Add(new WhatsNewItem(
            LocalizationService.Instance["About_WhatsNew_Fixes"],
            LocalizationService.Instance["About_WhatsNew_Fixes_Body"]));
        WhatsNewItems.Add(new WhatsNewItem(
            LocalizationService.Instance["About_WhatsNew_FileTransfer"],
            LocalizationService.Instance["About_WhatsNew_FileTransfer_Body"]));
        WhatsNewItems.Add(new WhatsNewItem(
            LocalizationService.Instance["About_WhatsNew_Android"],
            LocalizationService.Instance["About_WhatsNew_Android_Body"]));
        WhatsNewItems.Add(new WhatsNewItem(
            LocalizationService.Instance["About_WhatsNew_Issues"],
            LocalizationService.Instance["About_WhatsNew_Issues_Body"]));
    }

    /// <summary>
    /// Parses the newest *released* CHANGELOG section into What's-New highlights (bold title + description).
    /// "## [Unreleased]" is skipped — its entries aren't shipped yet — and so is any released section with
    /// no "- " bullets, otherwise an empty leading section makes this return 0 items and the About page
    /// silently falls back to stale hardcoded highlights (RemEx-xtc2). Internal for unit testing.
    /// </summary>
    internal static List<WhatsNewItem> ParseChangelog(string markdown, int maxItems)
    {
        var items = new List<WhatsNewItem>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            while (i < lines.Length && !lines[i].StartsWith("## [", StringComparison.Ordinal)) i++;
            if (i >= lines.Length) break;
            var isUnreleased = lines[i].Contains("[Unreleased]", StringComparison.OrdinalIgnoreCase);
            i++;
            for (; i < lines.Length && items.Count < maxItems; i++)
            {
                if (lines[i].StartsWith("## [", StringComparison.Ordinal))
                    break; // section over — decide below whether it produced anything
                if (isUnreleased)
                    continue;
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                    continue;
                var (title, description) = ParseBullet(trimmed.Substring(2));
                if (!string.IsNullOrWhiteSpace(title))
                    items.Add(new WhatsNewItem(title, description));
            }
            if (items.Count > 0)
                break; // newest non-empty released section found — only surface that one
        }
        return items;
    }

    private static (string Title, string Description) ParseBullet(string bullet)
    {
        bullet = bullet.Trim();
        string title = string.Empty, description = bullet;
        if (bullet.StartsWith("**", StringComparison.Ordinal))
        {
            int end = bullet.IndexOf("**", 2, StringComparison.Ordinal);
            if (end > 0)
            {
                title = bullet.Substring(2, end - 2).Trim().TrimEnd('.', ' ');
                description = bullet.Substring(end + 2).Trim();
            }
        }
        // Drop a trailing "(files…; RemEx-id.)" attribution — it's noise for end users.
        int lastParen = description.LastIndexOf('(');
        if (lastParen >= 0)
        {
            var tail = description.Substring(lastParen);
            if (tail.Contains("RemEx-") || tail.Contains('`'))
                description = description.Substring(0, lastParen).TrimEnd();
        }
        description = description.Replace("**", string.Empty).Replace("`", string.Empty).Trim();
        return (title, description);
    }

    private void LoadFaq()
    {
        for (int q = 1; q <= 16; q++)
        {
            FaqItems.Add(new FaqItem(
                LocalizationService.Instance[$"Faq_Q{q}_Question"],
                LocalizationService.Instance[$"Faq_Q{q}_Answer"]));
        }
    }

    [RelayCommand]
    private void ToggleShortcuts() => IsShowShortcutsOpen = !IsShowShortcutsOpen;

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.HostCapabilities) ||
            e.PropertyName == nameof(ConnectionViewModel.IsConnected))
        {
            UpdateHostVersion();
            // Also re-read the fingerprint, because the SERVICE can arrive late even though the
            // certificate cannot. HostBootstrapper awaits GetOrCreateCertificateAsync BEFORE it
            // registers ICertificateService, so the service and a loaded certificate become visible
            // together — a resolvable service holding an unloaded certificate is not a state the
            // desktop can observe. What is observable is no service at all: the embedded host is
            // started inside a try/catch, and About is cached for the session by ShellViewModel, so
            // without this re-read a host that published its container late would leave "(none)" on
            // screen permanently — on the one page a user opens precisely because their phone told
            // them to check this value.
            UpdateHostFingerprint();
        }
    }

    /// <summary>
    /// Reads this PC's certificate fingerprint into <see cref="HostFingerprint"/>, in the same
    /// grouped short form the Android client shows for the pin it holds (RemEx-n8xk).
    /// </summary>
    private void UpdateHostFingerprint()
    {
        var certificateService = ResolveCertificateService();
        if (certificateService is null)
        {
            HostFingerprint = SpkiFingerprintDisplay.Unavailable;

            // LOGGED, THOUGH IT IS AN EARLY RETURN RATHER THAN A CATCH (review of RemEx-n8xk). This
            // is the REACHABLE cause of an empty row — the embedded host is started inside a
            // try/catch, and a host that never came up registers no container — while the throw
            // below is not observable from the desktop at all. Leaving the reachable branch silent
            // put the only "(none)" a real user can hit nowhere in the diagnostics export, which is
            // the exact shape SwallowedErrorLoggingTests exists to prevent. Information, not Debug,
            // so it survives the export's level filter.
            InMemoryLogSink.Append(LogLevel.Information, "About",
                "No certificate service in this process, so this PC's fingerprint cannot be shown.", null);
            return;
        }

        try
        {
            HostFingerprint = SpkiFingerprintDisplay.ForDisplay(certificateService.GetSpkiSha256Base64());
        }
        catch (Exception ex)
        {
            // DEFENSIVE, not expected. GetSpkiSha256Base64 throws until the certificate is loaded,
            // but HostBootstrapper awaits that load before it registers the service, so a resolvable
            // service with an unloaded certificate is not a state this process can reach. Kept
            // because the alternative is an unhandled exception taking down the page the user was
            // told to visit, and because a certificate the host cannot READ would land here too.
            HostFingerprint = SpkiFingerprintDisplay.Unavailable;
            InMemoryLogSink.Append(LogLevel.Warning, "About",
                "The host certificate fingerprint could not be read", ex);
        }
    }

    private void UpdateHostVersion()
    {
        if (!_connection.IsConnected)
        {
            HostVersion = LocalizationService.Instance["Status_Disconnected"];
            return;
        }

        if (_connection.HostCapabilities == null)
        {
            HostVersion = LocalizationService.Instance["Status_Connected"];
            return;
        }

        var caps = _connection.HostCapabilities;
        // Normalised for DISPLAY ONLY — the wire value is untouched, so nothing the Android client
        // parses changes. The host still reports four components ("2.4.0.0") while this app shows
        // its own as three, and the two sit one divider apart on this page (RemEx-8jzu).
        var version = AppVersion.Normalize(caps.Version);
        // TWO VALUES ON PURPOSE, and keeping them apart is the point. The label is what the user
        // reads; the raw token is what the branch below decides on. Deciding on the label would mean
        // comparing an English wire sentinel against a string that is nominally translated - correct
        // only for as long as nobody localizes the unknown case, and silently wrong the moment
        // somebody does. (RemEx-6s34)
        var platform = _connection.HostPlatformLabel;
        var platformToken = caps.Platform;
        // Localized label, not the raw wire token: RuntimeMode "service" is legacy naming for a
        // non-interactive Windows process, not a service install (RemEx-9z0f).
        var runtime = _connection.HostRuntimeLabel;

        if (!string.IsNullOrEmpty(version) && version != "unknown")
        {
            HostVersion = $"{version} ({platform}, {runtime})";
        }
        else if (!string.IsNullOrEmpty(platformToken) && platformToken != "unknown")
        {
            HostVersion = $"{platform} ({runtime})";
        }
        else
        {
            HostVersion = LocalizationService.Instance["Status_Connected"];
        }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        try
        {
            var url = "https://github.com/clindsay94/remex";
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            // The user clicked a link and nothing happened. Silence here means the log cannot even
            // confirm the click was received, let alone why it did nothing.
            InMemoryLogSink.Append(LogLevel.Warning, "About", "Could not open a browser for a link", ex);
        }
    }

    /// <summary>Manual "Check for updates" button. Safe to invoke repeatedly; never throws.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        if (_updateService == null || IsCheckingForUpdate)
            return;

        IsCheckingForUpdate = true;
        HasUpdateStatus = true;
        UpdateStatusText = LocalizationService.Instance["About_Update_Checking"];
        try
        {
            ApplyUpdateResult(await _updateService.CheckAsync());
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    /// <summary>Opens the release's download page in the default browser.</summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        var url = _downloadUrl ?? _updateService?.ReleasesUrl ?? "https://github.com/clindsay94/remex/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "About",
                "Could not open a browser for the update download", ex);
        }
    }

    /// <summary>Fired (possibly off the UI thread) when the startup or a manual check completes.</summary>
    private void OnUpdateResultChanged(object? sender, EventArgs e)
    {
        var result = _updateService?.LastResult;
        if (result == null)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        _downloadUrl = result.DownloadUrl;
        HasUpdateStatus = true;
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable:
                IsUpdateAvailable = true;
                UpdateStatusText = string.Format(
                    LocalizationService.Instance["About_Update_Available"],
                    result.LatestVersion);
                break;
            case UpdateCheckStatus.UpToDate:
                IsUpdateAvailable = false;
                UpdateStatusText = LocalizationService.Instance["About_Update_UpToDate"];
                break;
            default:
                IsUpdateAvailable = false;
                UpdateStatusText = LocalizationService.Instance["About_Update_Failed"];
                break;
        }
    }

    [RelayCommand]
    private void NavigateBack()
    {
        _shell.NavigateToHome();
    }

    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        if (_updateService != null)
            _updateService.ResultChanged -= OnUpdateResultChanged;
    }
}

public record WhatsNewItem(string Version, string Description);
public record FaqItem(string Question, string Answer);
