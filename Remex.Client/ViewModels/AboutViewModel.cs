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

        // Get client version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        ClientVersion = version?.ToString() ?? "unknown";

        UpdateHostVersion();
        LoadWhatsNew();
        LoadFaq();
    }
    
    private void LoadWhatsNew()
    {
        WhatsNewItems.Add(new WhatsNewItem("Features", "End-to-End Encryption, QR Code Pairing, Remote File Transfer, Command Palette, System Tray, Dynamic Layouts"));
        WhatsNewItems.Add(new WhatsNewItem("Fixes", "Settings freeze (Linux), Async-void crashes, Alert memory leaks, Duplicate style blocks"));
        WhatsNewItems.Add(new WhatsNewItem("Issues (Known)", "Bitmap-on-clipboard deferred to 2.x"));
    }

    private void LoadFaq()
    {
        FaqItems.Add(new FaqItem("What do I need to run on my PC?", "You need either Remex.Client.Desktop or Remex.Host running on your PC. Both are self-contained (all runtimes included) — just extract the publish folder and run. Download from the GitHub releases page."));
        FaqItems.Add(new FaqItem("How do I pair my phone or second PC?", "Scan the QR code found in the Settings -> Connection section on the desktop client using the RemEx Android app, or enter the 6-digit PIN displayed on the host."));
        FaqItems.Add(new FaqItem("How do I find my PC's IP address?", "Windows: 'ipconfig' in Command Prompt. macOS: System Settings -> Network. Linux: 'ip addr' in terminal. You can also see the IP directly in the Remex.Client.Desktop window."));
        FaqItems.Add(new FaqItem("Auto-discovery isn't finding my PC. What should I check?", "1. Ensure Remex.Host is running.\n2. Both devices must be on the same Wi-Fi / LAN.\n3. Check if your router has 'AP/Client Isolation' enabled.\n4. Corporate/guest networks often block discovery (mDNS)."));
        FaqItems.Add(new FaqItem("What is the default port?", "The default port is 5005. If port 5005 is in use, it will fall back to 5006. You can see the active port in the desktop client's window."));
        FaqItems.Add(new FaqItem("Can I connect over the internet?", "RemEx is designed for local network (LAN) use. You can use a VPN for remote access. Port forwarding is not recommended for security reasons."));
        FaqItems.Add(new FaqItem("Remote Desktop is laggy. What can I do?", "• Lower the Quality slider.\n• Reduce Target FPS (15-20 is plenty).\n• Lower the Scale.\n• Ensure you are on a fast 5GHz Wi-Fi network."));
        FaqItems.Add(new FaqItem("What is Wake-on-LAN (WOL)?", "Wake-on-LAN lets you power on your PC remotely. Enable it in your BIOS/UEFI and network adapter properties. Then enter your PC's MAC address in the Connection screen."));
        FaqItems.Add(new FaqItem("How do I transfer files?", "Navigate to the File Transfer section using the sidebar or command palette to browse, upload, and download files from configured shared folders."));
        FaqItems.Add(new FaqItem("Can I lock my PC remotely?", "Yes! Use the Lock PC Quick Settings tile on Android, or the Remote Control screen / Command Palette to lock your PC."));
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
    }
}

public record WhatsNewItem(string Version, string Description);
public record FaqItem(string Question, string Answer);
