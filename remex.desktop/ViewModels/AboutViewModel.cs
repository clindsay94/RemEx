using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the About page displaying version information and app details.
/// </summary>
public partial class AboutViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private string _clientVersion = "unknown";

    [ObservableProperty]
    private string _hostVersion = "Disconnected";
    
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

    public AboutViewModel(ConnectionViewModel connection, ShellViewModel shell)
    {
        _connection = connection;
        _shell = shell;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        // Live language switching: the What's New / FAQ lists are built from localized strings
        // once, so rebuild them when the culture changes.
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        // Get client version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        ClientVersion = version?.ToString() ?? "unknown";

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
        LoadWhatsNew();
        LoadFaq();
    }

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
        catch
        {
            // Fall back to the localized static highlights below if the changelog can't be read.
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
        for (int q = 1; q <= 11; q++)
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
        var version = caps.Version;
        var platform = caps.Platform;
        var runtime = caps.RuntimeMode;

        if (!string.IsNullOrEmpty(version) && version != "unknown")
        {
            HostVersion = $"{version} ({platform}, {runtime})";
        }
        else if (!string.IsNullOrEmpty(platform) && platform != "unknown")
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
        catch
        {
            // Silently fail if browser can't be opened
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
        catch
        {
            // Silently fail if a browser can't be opened.
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
