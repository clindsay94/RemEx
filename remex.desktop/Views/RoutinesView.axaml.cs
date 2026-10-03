using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Remex.Desktop.Controls;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

/// <summary>
/// The PC Routines page (routines spec §2.3, S4b). The view model does the work; this wires the
/// confirmation dialog, the 900 breakpoint (§2.4), the list keyboard (R-UX-48), and the motion gates
/// (P-M1, P-M3).
/// </summary>
public partial class RoutinesView : UserControl
{
    /// <summary>Content width at which the list and the detail sit side by side (P1); below it, one column (P2).</summary>
    internal const double WideBreakpoint = 900;

    /// <summary>The list column's minimum in the wide layout (§2.4 "list (min 360) + detail").</summary>
    internal const double ListMinWidth = 360;

    private RoutinesViewModel? _viewModel;
    private Func<string, string, string, string, Task<bool>>? _confirm;

    public RoutinesView()
    {
        InitializeComponent();

        RoutinesRoot.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);

        // Tunnel, so Space reaches us before the ListBox turns it into a selection (R-UX-48: Space
        // toggles enable). Only when the card row itself has focus; Space on the switch or on Run now
        // still activates that control.
        AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not RoutinesViewModel vm)
        {
            return;
        }

        _viewModel = vm;
        // A dialog that cannot show (no visible parent window) declines: Run now then does not run
        // (fail closed, T21) - ConfirmationDialogHost's own rule.
        _confirm = ConfirmationDialogHost.ForTinted(this);
        vm.OnConfirmationRequested = _confirm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.Refresh();

        ApplyDetailTransition(vm.IsReducedMotion);
        ApplyLayout(RoutinesRoot.Bounds.Width);

        // P-M1: the cards stagger in on the first visit per process, never under reduced motion.
        if (StaggeredEntrance.ShouldPlay(nameof(RoutinesView), vm.IsReducedMotion))
        {
            RoutineGroups.Classes.Add(StaggeredEntrance.Class);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        // Only clear the delegate this view installed: a newer view instance may already own it.
        if (ReferenceEquals(_viewModel.OnConfirmationRequested, _confirm))
        {
            _viewModel.OnConfirmationRequested = null;
        }

        _viewModel = null;
        _confirm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoutinesViewModel.IsReducedMotion) && _viewModel is not null)
        {
            ApplyDetailTransition(_viewModel.IsReducedMotion);
        }
    }

    /// <summary>
    /// P-M3: the detail cross-fades over <see cref="Remex.Desktop.Styles.Motion.StateChange"/> (150 ms) on the M3
    /// standard curve; instant under reduced motion.
    /// </summary>
    private void ApplyDetailTransition(bool reducedMotion)
    {
        DetailHost.PageTransition = reducedMotion
            ? null
            : new CrossFade(Remex.Desktop.Styles.Motion.StateChange)
            {
                FadeInEasing = Remex.Desktop.Styles.Motion.Standard,
                FadeOutEasing = Remex.Desktop.Styles.Motion.Standard,
            };
    }

    private void ApplyLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var wide = width >= WideBreakpoint;
        var columns = ContentGrid.ColumnDefinitions;
        columns[0].Width = new GridLength(1, GridUnitType.Star);
        columns[0].MinWidth = wide ? ListMinWidth : 0;
        columns[1].Width = new GridLength(wide ? 24 : 0);
        columns[2].Width = wide ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);

        if (_viewModel is not null)
        {
            _viewModel.IsWideLayout = wide;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Source is ListBoxItem { DataContext: RoutineCardViewModel card } item
            && item.FindAncestorOfType<ListBox>() is { } list
            && list.Classes.Contains("routine-list"))
        {
            list.SelectedItem = card;
            card.ToggleEnabledCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The card's context menu (right-click, Shift+F10 or the menu key): Run now, Turn off or on, History
    /// (R-UX-48). Built per request, so each card's menu binds to its own commands.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source)
        {
            return;
        }

        var item = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not RoutineCardViewModel card
            || item.FindAncestorOfType<ListBox>() is not { } list
            || !list.Classes.Contains("routine-list"))
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                new MenuItem { Header = loc["Routines_RunNow"], Command = card.RunNowCommand },
                new MenuItem
                {
                    Header = card.IsOn ? loc["Routines_Menu_TurnOff"] : loc["Routines_Menu_TurnOn"],
                    Command = card.ToggleEnabledCommand,
                    IsEnabled = card.CanToggle,
                },
                new MenuItem
                {
                    Header = loc["Routines_Menu_History"],
                    Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => ShowHistory(list, card)),
                },
            },
        };

        menu.Open(item);
        e.Handled = true;
    }

    private void ShowHistory(ListBox list, RoutineCardViewModel card)
    {
        list.SelectedItem = card;
        if (_viewModel?.IsWideLayout == true)
        {
            DetailPane.Focus(NavigationMethod.Tab);
        }
    }
}
