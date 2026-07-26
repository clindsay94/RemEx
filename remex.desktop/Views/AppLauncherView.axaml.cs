using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class AppLauncherView : UserControl
{
    // Must match the WrapPanel's ItemWidth/ItemHeight in AppLauncherView.axaml.
    private const double ItemWidth = 160;
    private const double ItemHeight = 160;
    private const double MouseDragThreshold = 8;
    private const double TouchLongPressMoveThreshold = 10;
    private const int TouchLongPressDelayMs = 250;

    // ═══ Drag state (pointer-capture based — NOT Avalonia DragDrop; see AGENTS.md WP-C notes) ═══
    private ContentPresenter? _dragContainer;
    private AppEntry? _dragEntry;
    private Point _pressPoint;
    private bool _dragArmed;
    private bool _dragActive;
    private int _hoverIndex = -1;
    private DispatcherTimer? _longPressTimer;
    private ContentPresenter? _dropTargetContainer;
    private TranslateTransform? _dragGhostTransform;

    public AppLauncherView()
    {
        InitializeComponent();

        if (this.FindControl<ItemsControl>("LauncherGrid") is { } grid)
        {
            // Tunneled (not bubbled): the launch Button inside each card would otherwise consume
            // the press before it ever reaches us.
            grid.AddHandler(InputElement.PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
            grid.AddHandler(InputElement.PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);
            grid.AddHandler(InputElement.PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel);
            grid.AddHandler(InputElement.PointerCaptureLostEvent, OnGridCaptureLost);
        }

        // External file / shortcut drop → add to the launcher (separate from the internal
        // pointer-capture reorder above; OS drag-drop uses a different event family).
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not AppLauncherViewModel vm) return;
            if (!e.DataTransfer.Contains(DataFormat.File)) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;

            var paths = files.OfType<IStorageFile>()
                             .Select(f => f.TryGetLocalPath())
                             .Where(p => !string.IsNullOrEmpty(p))
                             .Select(p => p!)
                             .ToList();
            if (paths.Count > 0)
                await vm.AddDroppedAppsAsync(paths);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Remex] Launcher drop failed: {ex.Message}");
        }
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is AppLauncherViewModel viewModel)
        {
            // Guards removing a launcher card (RemEx-6p1f).
            viewModel.OnConfirmationRequested = ConfirmationDialogHost.For(this);

            viewModel.OnOpenAddProgramDialogRequested = async () =>
            {
                var dialog = new AddProgramWindow();

                if (dialog.DataContext is AddProgramViewModel addProgramVm)
                {
                    addProgramVm.OnSaveRequested = async (entry) =>
                    {
                        viewModel.Launchers.Add(entry);
                        try
                        {
                            await viewModel.SaveLaunchersAsync();
                        }
                        catch (System.Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Remex] Failed to save launchers: {ex.Message}");
                        }
                    };
                }

                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is Window parentWindow)
                {
                    await dialog.ShowDialog(parentWindow);
                }
            };

            viewModel.OnOpenEditProgramDialogRequested = async (entry) =>
            {
                var dialog = new AddProgramWindow();

                if (dialog.DataContext is AddProgramViewModel addProgramVm)
                {
                    addProgramVm.LoadForEdit(entry);
                    addProgramVm.OnSaveRequested = async (updated) =>
                    {
                        try
                        {
                            await viewModel.ApplyEditedEntryAsync(updated);
                        }
                        catch (System.Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Remex] Failed to save edited launcher entry: {ex.Message}");
                        }
                    };
                }

                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is Window parentWindow)
                {
                    await dialog.ShowDialog(parentWindow);
                }
            };
        }
    }

    // ═══════════════ Drag-to-rearrange ═══════════════

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AppLauncherViewModel vm) return;

        var props = e.GetCurrentPoint(sender as Visual).Properties;
        if (!props.IsLeftButtonPressed) return;

        // FilteredApps index mapping is ambiguous while a search filter is active — arrows remain
        // the only reorder path in that state (see AppLauncher_DragDisabledWhileFiltered).
        if (!string.IsNullOrWhiteSpace(vm.SearchText)) return;

        var container = FindAncestorContentPresenter(e.Source as Visual);
        if (container?.DataContext is not AppEntry entry) return;

        var panel = GetWrapPanel();
        if (panel == null) return;

        _dragContainer = container;
        _dragEntry = entry;
        _dragArmed = false;
        _dragActive = false;
        _hoverIndex = -1;
        _pressPoint = e.GetPosition(panel);

        if (e.Pointer.Type == PointerType.Touch || e.Pointer.Type == PointerType.Pen)
        {
            CancelLongPress();
            var capturedPointer = e.Pointer;
            _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TouchLongPressDelayMs) };
            _longPressTimer.Tick += (_, _) =>
            {
                CancelLongPress();
                if (!_dragActive && _dragContainer != null)
                {
                    BeginDrag(capturedPointer);
                }
            };
            _longPressTimer.Start();
        }
        else
        {
            _dragArmed = true; // mouse: needs an 8px move before a drag begins
        }

        // Do NOT set e.Handled — plain clicks (and the ← → ✎ ✕ toolbar buttons) must still fire.
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragContainer == null) return;

        var panel = GetWrapPanel();
        if (panel == null) return;

        var current = e.GetPosition(panel);

        if (!_dragActive)
        {
            var dx = current.X - _pressPoint.X;
            var dy = current.Y - _pressPoint.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);

            if (_longPressTimer != null)
            {
                // Touch/pen: moving too far before the long-press fires cancels the drag entirely
                // (lets the view keep scrolling/panning instead).
                if (distance > TouchLongPressMoveThreshold)
                {
                    CancelLongPress();
                    ResetDragState();
                }
                return;
            }

            if (_dragArmed && distance >= MouseDragThreshold)
            {
                BeginDrag(e.Pointer);
            }
            else
            {
                return;
            }
        }

        // Actively dragging.
        e.Handled = true;

        if (_dragGhostTransform != null)
        {
            _dragGhostTransform.X = current.X - _pressPoint.X;
            _dragGhostTransform.Y = current.Y - _pressPoint.Y;
        }

        if (DataContext is AppLauncherViewModel vm)
        {
            var itemCount = vm.Launchers.Count;
            var targetIndex = LauncherLayoutMath.IndexFromPoint(current, panel.Bounds.Width, ItemWidth, ItemHeight, itemCount);
            if (targetIndex >= 0 && targetIndex != _hoverIndex)
            {
                UpdateHoverHighlight(targetIndex);
            }
        }
    }

    private async void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CancelLongPress();

        if (!_dragActive)
        {
            ResetDragState();
            return;
        }

        e.Handled = true; // suppresses the launch click that would otherwise follow a drag

        var vm = DataContext as AppLauncherViewModel;
        var draggedEntry = _dragEntry;
        var targetHoverIndex = _hoverIndex;

        ResetVisualDragState();
        e.Pointer.Capture(null);

        try
        {
            if (vm != null && draggedEntry != null && targetHoverIndex >= 0 && targetHoverIndex < vm.Launchers.Count)
            {
                var currentIndex = vm.Launchers.IndexOf(draggedEntry);
                if (currentIndex >= 0 && targetHoverIndex != currentIndex)
                {
                    // The existing (previously orphaned) reorder command — now finally wired up.
                    // Persist happens once inside it.
                    var targetEntry = vm.Launchers[targetHoverIndex];
                    await vm.ReorderLauncherCommand.ExecuteAsync((draggedEntry, targetEntry));
                }
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Remex] Failed to persist launcher reorder: {ex.Message}");
        }
        finally
        {
            ResetDragState();
        }
    }

    private void OnGridCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CancelLongPress();
        ResetVisualDragState();
        ResetDragState();
    }

    private void BeginDrag(IPointer pointer)
    {
        if (_dragContainer == null) return;

        _dragActive = true;
        _dragArmed = false;
        CancelLongPress();

        pointer.Capture(_dragContainer);

        _dragContainer.ZIndex = 100;
        _dragContainer.Opacity = 0.75;
        _dragGhostTransform = new TranslateTransform();
        _dragContainer.RenderTransform = _dragGhostTransform;
        _dragContainer.Classes.Add("dragging");

        _hoverIndex = DataContext is AppLauncherViewModel vm && _dragEntry != null
            ? vm.Launchers.IndexOf(_dragEntry)
            : -1;
    }

    private void UpdateHoverHighlight(int index)
    {
        if (this.FindControl<ItemsControl>("LauncherGrid") is not { } grid) return;

        if (_dropTargetContainer != null)
        {
            _dropTargetContainer.Classes.Remove("drop-target");
            _dropTargetContainer = null;
        }

        if (grid.ContainerFromIndex(index) is ContentPresenter container && container != _dragContainer)
        {
            container.Classes.Add("drop-target");
            _dropTargetContainer = container;
        }

        _hoverIndex = index;
    }

    private void ResetVisualDragState()
    {
        if (_dragContainer != null)
        {
            _dragContainer.Classes.Remove("dragging");
            _dragContainer.ZIndex = 0;
            _dragContainer.Opacity = 1.0;
            _dragContainer.RenderTransform = null;
        }

        if (_dropTargetContainer != null)
        {
            _dropTargetContainer.Classes.Remove("drop-target");
        }
    }

    private void ResetDragState()
    {
        _dragContainer = null;
        _dragEntry = null;
        _dragArmed = false;
        _dragActive = false;
        _hoverIndex = -1;
        _dropTargetContainer = null;
        _dragGhostTransform = null;
    }

    private void CancelLongPress()
    {
        if (_longPressTimer != null)
        {
            _longPressTimer.Stop();
            _longPressTimer = null;
        }
    }

    private Panel? GetWrapPanel() => this.FindControl<ItemsControl>("LauncherGrid")?.Presenter?.Panel;

    private static ContentPresenter? FindAncestorContentPresenter(Visual? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is ContentPresenter { DataContext: AppEntry } cp)
                return cp;
            current = current.GetVisualParent();
        }
        return null;
    }
}
