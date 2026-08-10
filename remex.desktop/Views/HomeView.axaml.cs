using Avalonia.Controls;
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
