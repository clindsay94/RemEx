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
        {
            _previousVm.ResetViewRequested   -= OnResetViewRequested;
            _previousVm.MinimapPanRequested  -= OnMinimapPanRequested;
            _previousVm.ShowSetAlertRequested -= OnShowSetAlertRequested;
        }

        _previousVm = DataContext as CanvasDashboardViewModel;

        if (_previousVm != null)
        {
            _previousVm.ResetViewRequested   += OnResetViewRequested;
            _previousVm.MinimapPanRequested  += OnMinimapPanRequested;
            _previousVm.ShowSetAlertRequested += OnShowSetAlertRequested;
        }

        // Re-wire the minimap's PanRequested when the VM changes.
        WireMinimapControl();
    }

    // ═══════════════ Reset View ═══════════════

    private void OnResetViewRequested(object? sender, EventArgs e)
    {
        this.FindControl<ZoomableCanvas>("MainCanvas")?.ResetView();
    }

    // ═══════════════ Minimap ═══════════════

    private void WireMinimapControl()
    {
        var minimap = this.FindControl<CanvasMinimap>("Minimap");
        if (minimap == null) return;

        minimap.PanRequested -= OnMinimapControlClicked;
        minimap.PanRequested += OnMinimapControlClicked;

        var canvas = this.FindControl<ZoomableCanvas>("MainCanvas");
        if (canvas == null) return;

        canvas.ViewportChanged -= OnCanvasViewportChanged;
        canvas.ViewportChanged += OnCanvasViewportChanged;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireMinimapControl();
    }

    private void OnCanvasViewportChanged(double offsetX, double offsetY, double zoom)
    {
        if (DataContext is CanvasDashboardViewModel vm)
        {
            var canvas = this.FindControl<ZoomableCanvas>("MainCanvas");
            if (canvas != null)
                vm.UpdateMinimapViewport(offsetX, offsetY, zoom,
                    canvas.Bounds.Width, canvas.Bounds.Height);
        }
    }

    private void OnMinimapControlClicked(double worldX, double worldY)
        => (DataContext as CanvasDashboardViewModel)?.OnMinimapClicked(worldX, worldY);

    private void OnMinimapPanRequested(double worldX, double worldY)
        => this.FindControl<ZoomableCanvas>("MainCanvas")?.PanTo(worldX, worldY);

    // ═══════════════ Set Alert Dialog ═══════════════

    private async void OnShowSetAlertRequested(string sensorName, Remex.Core.Models.SensorAlert? existing)
    {
        if (DataContext is not CanvasDashboardViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window ownerWindow) return;

        Remex.Core.Models.SensorAlert? result = null;
        var dialog = new SetAlertDialog(sensorName, existing, r => result = r);
        await dialog.ShowDialog(ownerWindow);

        vm.ApplySensorAlert(sensorName, result);
    }

    // ═══════════════ Viewport size tracking ═══════════════

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
