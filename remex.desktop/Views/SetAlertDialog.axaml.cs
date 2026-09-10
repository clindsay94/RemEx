using Avalonia.Controls;
using Remex.Desktop.ViewModels;
using Remex.Core.Models;

namespace Remex.Desktop.Views;

public partial class SetAlertDialog : Window
{
    public SetAlertDialog() { InitializeComponent(); }

    public SetAlertDialog(string sensorName, SensorAlert? existing, System.Action<SensorAlert?> onResult)
    {
        InitializeComponent();
        DataContext = new SetAlertViewModel(sensorName, existing, result =>
        {
            onResult(result);
            Close();
        });
    }

    // No hand-written InitializeComponent: a parameterless one shadows the generated
    // InitializeComponent(bool loadXaml = true) override, so AvaloniaXamlLoader.Load(this) never
    // runs and every x:Name field stays null — that NullReferenceException, in
    // ConfirmationDialog's TitleText field, is what froze RemEx on every destructive action
    // (RemEx-wdqx; ConfirmationDialog.axaml.cs itself is gone, deleted on this branch). This
    // markup names nothing, so the declaration was inert here; it is removed so the pattern
    // cannot be primed by a later edit that adds a named control.
}
