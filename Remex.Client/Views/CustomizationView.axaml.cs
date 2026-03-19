using Avalonia.Controls;
using Avalonia.Input;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class CustomizationView : UserControl
{
    public CustomizationView()
    {
        InitializeComponent();
    }

    private void OnSelectBaseDark(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CustomizationViewModel vm) vm.SelectThemeCommand.Execute("BaseDarkGlass");
    }

    private void OnSelectCyberNOC(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CustomizationViewModel vm) vm.SelectThemeCommand.Execute("CyberNOC");
    }

    private void OnSelectSolarFlare(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CustomizationViewModel vm) vm.SelectThemeCommand.Execute("SolarFlare");
    }

    private void OnSelectMonolith(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CustomizationViewModel vm) vm.SelectThemeCommand.Execute("Monolith");
    }
}
