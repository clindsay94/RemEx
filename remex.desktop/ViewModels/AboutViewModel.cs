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

    /// <summary>Parses the most recent CHANGELOG version section into What's-New highlights (bold title + description).</summary>
    private static List<WhatsNewItem> ParseChangelog(string markdown, int maxItems)
    {
        var items = new List<WhatsNewItem>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length && !lines[i].StartsWith("## [", StringComparison.Ordinal)) i++;
        i++; // move past the first version heading (most recent section)
        for (; i < lines.Length && items.Count < maxItems; i++)
        {
            if (lines[i].StartsWith("## [", StringComparison.Ordinal))
                break; // stop at the next version — only surface the latest
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                continue;
            var (title, description) = ParseBullet(trimmed.Substring(2));
            if (!string.IsNullOrWhiteSpace(title))
                items.Add(new WhatsNewItem(title, description));
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

    [RelayCommand]
    private void NavigateBack()
    {
        _shell.NavigateToHome();
    }

    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }
}

public record WhatsNewItem(string Version, string Description);
public record FaqItem(string Question, string Answer);
