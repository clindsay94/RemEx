using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Remex.Desktop.Controls;

public partial class DashboardBackgroundControl : ContentControl
{
    public DashboardBackgroundControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
