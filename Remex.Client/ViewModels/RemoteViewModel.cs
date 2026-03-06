using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the PC Remote page.
/// Provides power controls (shutdown, restart, lock, screen off)
/// and Wake-on-LAN functionality.
/// </summary>
public partial class RemoteViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    /// <summary>Shared connection — needed to send power commands to the host.</summary>
    public ConnectionViewModel Connection { get; }

    public RemoteViewModel(ConnectionViewModel connection, ShellViewModel shell)
    {
        Connection = connection;
        _shell = shell;
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
