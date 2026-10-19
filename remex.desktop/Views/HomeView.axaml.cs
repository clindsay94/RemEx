using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Remex.Desktop.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    /// <summary>
    /// Arms the dashboard's first-paint entrance (RemEx-dnfq0). Attachment, not the constructor,
    /// because DataContext is not yet set when the control is constructed (CanvasView.axaml.cs:148
    /// precedent) - by attachment time the DataContextChanged handler above has already run.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is HomeViewModel vm
            && StaggeredEntrance.ShouldPlay(nameof(HomeView), vm.Shell.IsReducedMotion))
        {
            DashboardSections.Classes.Add(StaggeredEntrance.Class);
        }
    }

    // Tapped/KeyDown rather than SelectionChanged, so merely moving the highlight never navigates away.
    private void OnPinnedSensorTapped(object? sender, TappedEventArgs e) => OpenSensorsFromItem(e.Source);

    private void OnPinnedSensorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space))
            return;

        OpenSensorsFromItem(e.Source);
        e.Handled = true;
    }

    private void OpenSensorsFromItem(object? source)
    {
        if (source is Visual visual
            && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null
            && DataContext is HomeViewModel vm)
        {
            vm.NavigateToCanvasCommand.Execute(null);
        }
    }

    /// <summary>
    /// Gives the system-status card somewhere to show its help text (RemEx-tb0a).
    /// </summary>
    /// <remarks>
    /// **WIRED ON DataContextChanged, NOT IN THE CONSTRUCTOR.** The DataContext is not set when the
    /// control is constructed, so wiring there would silently leave the delegate null and the Explain
    /// button inert — the same silent-binding failure the Fix button's own comment records, arrived at
    /// from the other side.
    ///
    /// Reuses <see cref="ConfirmationDialogHost"/> because this IS that dialog used informationally:
    /// one button, nothing destructive, and the returned bool is discarded. That also means it
    /// inherits the property that matters — a view with no parent window declines rather than throws.
    /// </remarks>
    private void ConfigureViewModel()
    {
        if (DataContext is HomeViewModel { SystemStatus: { } status })
        {
            status.OnExplainRequested = ConfirmationDialogHost.For(this);
        }
    }
}
