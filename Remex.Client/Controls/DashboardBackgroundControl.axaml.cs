using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Remex.Client.Controls;

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
