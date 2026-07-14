using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class DiagnosticLogsView : UserControl
{
    public DiagnosticLogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    private void ConfigureViewModel()
    {
        if (DataContext is not DiagnosticLogsViewModel vm)
            return;

        // Provide the save-file picker so log export can prompt the user to name the file.
        vm.PickSaveFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return null;
            return await topLevel.StorageProvider.SaveFilePickerAsync(options);
        };
    }
}
