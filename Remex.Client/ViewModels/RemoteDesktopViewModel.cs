using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Remote Desktop page.
/// Will provide remote desktop / screen sharing functionality.
/// </summary>
public partial class RemoteDesktopViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public ConnectionViewModel Connection { get; }

    public RemoteDesktopViewModel(ConnectionViewModel connection, ShellViewModel shell)
    {
        Connection = connection;
        _shell = shell;
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
