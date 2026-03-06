using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the App Launcher page.
/// Will provide remote application launching capabilities.
/// </summary>
public partial class AppLauncherViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public ConnectionViewModel Connection { get; }

    public AppLauncherViewModel(ConnectionViewModel connection, ShellViewModel shell)
    {
        Connection = connection;
        _shell = shell;
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
