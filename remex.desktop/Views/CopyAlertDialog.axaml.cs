using Avalonia.Controls;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class CopyAlertDialog : Window
{
    public CopyAlertDialog() { InitializeComponent(); }

    public CopyAlertDialog(SensorAlert source, ISensorCatalog catalog, SensorAlertStore store)
    {
        InitializeComponent();

        var viewModel = new CopyAlertDialogViewModel(source, catalog, store);
        viewModel.RequestClose += () => Close();

        DataContext = viewModel;
        Title = viewModel.Header;
    }

    // No hand-written InitializeComponent — see the note in SetAlertDialog. This markup names no
    // controls (everything is bound through the view model), so the declaration would be inert.
}
