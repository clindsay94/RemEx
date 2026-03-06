using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Customization page.
/// Will provide theming, layout presets, and visual customization options.
/// </summary>
public partial class CustomizationViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public CustomizationViewModel(ShellViewModel shell)
    {
        _shell = shell;
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
