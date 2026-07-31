using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Avalonia.Threading;
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

        if (_previousVm != null)
        {
            _previousVm.PropertyChanged -= OnVmPropertyChanged;
            _previousVm.ShowSecondMetricRequested -= OnShowSecondMetricRequested;
            _previousVm.OnConfirmationRequested = null;
        }

        _previousVm = DataContext as CanvasDashboardViewModel;

        if (_previousVm != null)
        {
            _previousVm.ResetViewRequested += OnResetViewRequested;
            _previousVm.MinimapPanRequested += OnMinimapPanRequested;
            _previousVm.ShowSetAlertRequested += OnShowSetAlertRequested;
            _previousVm.ShowSecondMetricRequested += OnShowSecondMetricRequested;
            _previousVm.PropertyChanged += OnVmPropertyChanged;
            // Guards Reboot to UEFI (RemEx-5vcb). Wired here rather than in OnLoaded so the
            // delegate exists even if the button is somehow reached before the view first loads -
            // without it the command fails closed and the button would silently do nothing.
            _previousVm.OnConfirmationRequested = ConfirmationDialogHost.For(this);
        }

        // Re-wire the minimap's PanRequested when the VM changes.
        WireMinimapControl();
    }

    // ═══════════════ Coach mark positioning ═══════════════
    //
    // The coach hint must point at the *real* "Sensors" toolbar button, whose X drifts with
    // window width and localized button widths (the toolbar buttons are a right-aligned group).
    // So we measure the button at layout time rather than hard-coding a position. The overlay
    // scrim covers only the canvas (row 1), not the toolbar (row 0), so the button stays bright
    // above the dim — we only need its X and can draw the whole arrow upward within the canvas.

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasDashboardViewModel.CoachMarkVisible))
            Dispatcher.UIThread.Post(PositionCoachMark, DispatcherPriority.Loaded);
    }

    private void OnCoachCardSizeChanged(object? sender, SizeChangedEventArgs e) => PositionCoachMark();

    private void PositionCoachMark()
    {
        if (CoachOverlay is null || !CoachOverlay.IsVisible) return;
        if (CoachCanvas is null || CoachCard is null || CoachArrow is null
            || CoachArrowHead is null || SensorsButton is null) return;

        double canvasW = CoachCanvas.Bounds.Width;
        if (canvasW <= 0) return;

        // Button center X in the canvas coordinate space (Y is above the canvas, so ignored — the
        // arrow just points up at the top edge beneath the button). Fallback ≈ where the Sensors
        // button typically sits if layout isn't ready yet; corrected on the next size change.
        var pt = SensorsButton.TranslatePoint(new Point(SensorsButton.Bounds.Width / 2, 0), CoachCanvas);
        double buttonX = pt?.X ?? canvasW * 0.6;

        double cardW = CoachCard.Bounds.Width > 0 ? CoachCard.Bounds.Width : CoachCard.Width;
        const double cardTop = 72;
        double cardLeft = Math.Clamp(buttonX - cardW / 2, 12, Math.Max(12, canvasW - cardW - 12));
        Canvas.SetLeft(CoachCard, cardLeft);
        Canvas.SetTop(CoachCard, cardTop);

        // Arrow: from the card's top edge up to the top of the canvas, right under the button.
        double startX = Math.Clamp(buttonX, cardLeft + 24, cardLeft + cardW - 24);
        double startY = cardTop - 8;
        double apexY = 4;
        double headBaseY = apexY + 12;
        double midY = (startY + headBaseY) / 2;

        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(new Point(startX, startY), false);
            ctx.CubicBezierTo(new Point(startX, midY), new Point(buttonX, midY), new Point(buttonX, headBaseY));
            ctx.EndFigure(false);
        }
        CoachArrow.Data = arrow;

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(buttonX, apexY), true);
            ctx.LineTo(new Point(buttonX - 7, apexY + 12));
            ctx.LineTo(new Point(buttonX + 7, apexY + 12));
            ctx.EndFigure(true);
        }
        CoachArrowHead.Data = head;
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

    // ═══════════════ Second Metric (DualMetric) Picker ═══════════════

    private async void OnShowSecondMetricRequested(CanvasCardViewModel card)
    {
        try
        {
            if (DataContext is not CanvasDashboardViewModel vm || card.Sensor is null) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window ownerWindow) return;

            var candidates = vm.SecondaryCandidatesFor(card);
            var dialog = new SecondMetricDialog(
                card.Sensor.DisplayName,
                candidates,
                card.Sensor.SecondarySensorId,
                name => vm.ApplySecondMetric(card, name));

            await dialog.ShowDialog(ownerWindow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CanvasView] SecondMetric dialog error: {ex.Message}");
        }
    }

    // ═══════════════ Inline Card Rename ═══════════════

    private void OnRenameBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Focus + select whenever the editor becomes visible (i.e. rename is opened).
        tb.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == Visual.IsVisibleProperty && ev.NewValue is true)
            {
                tb.Focus();
                tb.SelectAll();
            }
        };

        if (tb.IsVisible)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            CommitRename(sender);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e) => CommitRename(sender);

    private static void CommitRename(object? sender)
    {
        if ((sender as Control)?.DataContext is CanvasCardViewModel { Sensor: { } sensor }
            && sensor.IsEditingTitle)
        {
            sensor.EndRenameCommand.Execute(null);
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

        // Re-aim the coach arrow at the Sensors button after any viewport resize.
        if (CoachOverlay is { IsVisible: true })
            Dispatcher.UIThread.Post(PositionCoachMark, DispatcherPriority.Loaded);
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
            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus($"Export failed: {ex.Message}", succeeded: false);
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
                (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus("Clipboard unavailable", succeeded: false);
                return;
            }

            await topLevel.Clipboard.SetBitmapAsync(rtb);
            await topLevel.Clipboard.FlushAsync();

            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus("Copied to clipboard");
        }
        catch (Exception ex)
        {
            (DataContext as CanvasDashboardViewModel)?.SetSnapshotStatus($"Copy failed: {ex.Message}", succeeded: false);
        }
    }
}
