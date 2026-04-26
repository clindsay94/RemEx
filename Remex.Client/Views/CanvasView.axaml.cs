using System;
using Avalonia;
using Avalonia.Controls;
using Remex.Client.Controls;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class CanvasView : UserControl
{
    private CanvasDashboardViewModel? _previousVm;

    public CanvasView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_previousVm != null)
            _previousVm.ResetViewRequested -= OnResetViewRequested;

        _previousVm = DataContext as CanvasDashboardViewModel;

        if (_previousVm != null)
            _previousVm.ResetViewRequested += OnResetViewRequested;
    }

    private void OnResetViewRequested(object? sender, EventArgs e)
    {
        this.FindControl<ZoomableCanvas>("MainCanvas")?.ResetView();
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
