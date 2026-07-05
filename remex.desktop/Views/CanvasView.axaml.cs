using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Remex.Desktop.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

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
            _previousVm.ResetViewRequested -= OnResetViewRequested;
            _previousVm.MinimapPanRequested -= OnMinimapPanRequested;
            _previousVm.ShowSetAlertRequested -= OnShowSetAlertRequested;
        }

        _previousVm = DataContext as CanvasDashboardViewModel;

        if (_previousVm != null)
        {
            _previousVm.ResetViewRequested += OnResetViewRequested;
            _previousVm.MinimapPanRequested += OnMinimapPanRequested;
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
        try
        {
            if (DataContext is not CanvasDashboardViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window ownerWindow) return;

            Remex.Core.Models.SensorAlert? result = null;
            var dialog = new SetAlertDialog(sensorName, existing, r => result = r);
            await dialog.ShowDialog(ownerWindow);

            // null means cancelled/dismissed — don't touch the existing alert.
            // ClearAlert passes a non-null sentinel (empty SensorName) for explicit removal.
            if (result != null)
                vm.ApplySensorAlert(sensorName, result);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CanvasView] SetAlert dialog error: {ex.Message}");
        }
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

    // ═══════════════ Snapshot Export (P8-I) ═══════════════

    private void OnExportSnapshot(object? sender, RoutedEventArgs e)
    {
        var canvas = this.FindControl<ZoomableCanvas>("MainCanvas");
        if (canvas is null) return;

        try
        {
            var pixelSize = new PixelSize((int)canvas.Bounds.Width, (int)canvas.Bounds.Height);
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return;

            var rtb = new RenderTargetBitmap(pixelSize);
            rtb.Render(canvas);

            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var fileName = $"Remex_Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(pictures, fileName);

            rtb.Save(filePath);

            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus($"Saved to {fileName}");
        }
        catch (Exception ex)
        {
            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus($"Export failed: {ex.Message}");
        }
    }

    private async void OnCopySnapshot(object? sender, RoutedEventArgs e)
    {
        var canvas = this.FindControl<ZoomableCanvas>("MainCanvas");
        if (canvas is null) return;

        try
        {
            var pixelSize = new PixelSize((int)canvas.Bounds.Width, (int)canvas.Bounds.Height);
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return;

            var rtb = new RenderTargetBitmap(pixelSize);
            rtb.Render(canvas);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is null)
            {
                (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus("Clipboard unavailable");
                return;
            }

            await topLevel.Clipboard.SetBitmapAsync(rtb);
            await topLevel.Clipboard.FlushAsync();

            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus("Copied to clipboard");
        }
        catch (Exception ex)
        {
            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus($"Copy failed: {ex.Message}");
        }
    }
}
