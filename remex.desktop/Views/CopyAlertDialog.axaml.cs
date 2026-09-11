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

        // Title stays the short "Copy to…" the markup sets. Overwriting it with viewModel.Header
        // put the whole alert description in the OS title bar, where it ran under the close button
        // (RemEx-8wpvr.10); the body's first TextBlock already shows that header in full.
        DataContext = viewModel;
    }

    // No hand-written InitializeComponent: a parameterless one shadows the generated
    // InitializeComponent(bool loadXaml = true) override, so AvaloniaXamlLoader.Load(this) never
    // runs and every x:Name field stays null — that exact NullReferenceException, in
    // ConfirmationDialog's TitleText field, is what froze RemEx on every destructive action
    // (RemEx-wdqx; ConfirmationDialog.axaml.cs itself is gone, deleted on this branch). This
    // markup names no controls (everything is bound through the view model), so the declaration
    // would be inert here too.
}
