using Avalonia;
using Avalonia.Controls;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class CanvasView : UserControl
{
    public CanvasView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // Keep the ViewModel informed of the viewport width
        // so it can detect drag-to-drawer drop targets.
        if (DataContext is CanvasDashboardViewModel vm)
        {
            vm.CanvasViewWidth = e.NewSize.Width;
        }
    }
}
