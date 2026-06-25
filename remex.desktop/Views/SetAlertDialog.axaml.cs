using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
