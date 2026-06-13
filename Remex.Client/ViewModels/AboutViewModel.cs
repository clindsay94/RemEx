using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;

namespace Remex.Client.ViewModels;

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
    public ObservableCollection<FaqItem> FaqItems { get; } = new();

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
        FaqItems.Clear();
        LoadWhatsNew();
        LoadFaq();
        UpdateHostVersion();
    }
    
    private void LoadWhatsNew()
    {
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

    private void LoadFaq()
    {
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q1_Question"],
            LocalizationService.Instance["Faq_Q1_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q2_Question"],
            LocalizationService.Instance["Faq_Q2_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q3_Question"],
            LocalizationService.Instance["Faq_Q3_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q4_Question"],
            LocalizationService.Instance["Faq_Q4_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q5_Question"],
            LocalizationService.Instance["Faq_Q5_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q6_Question"],
            LocalizationService.Instance["Faq_Q6_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q7_Question"],
            LocalizationService.Instance["Faq_Q7_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q8_Question"],
            LocalizationService.Instance["Faq_Q8_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q9_Question"],
            LocalizationService.Instance["Faq_Q9_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q10_Question"],
            LocalizationService.Instance["Faq_Q10_Answer"]));
        FaqItems.Add(new FaqItem(
            LocalizationService.Instance["Faq_Q11_Question"],
            LocalizationService.Instance["Faq_Q11_Answer"]));
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
