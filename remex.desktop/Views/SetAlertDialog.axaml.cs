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

    // No hand-written InitializeComponent — see the note in ConfirmationDialog. This markup
    // names nothing, so the declaration was inert here; it is removed so the pattern cannot be
    // primed by a later edit that adds a named control (RemEx-wdqx).
}
