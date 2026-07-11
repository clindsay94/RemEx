using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class AddProgramWindow : Window
{
    public AddProgramWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AddProgramViewModel>();

        if (DataContext is AddProgramViewModel viewModel)
        {
            viewModel.OnCloseRequested = () => Close();
            viewModel.PickFileAsync = async (options) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    return await topLevel.StorageProvider.OpenFilePickerAsync(options);
                }
                return System.Array.Empty<IStorageFile>();
            };

            // Window Title switches between "Add Program" and "Edit Program" based on IsEditMode.
            // IsEditMode is set once (via LoadForEdit) before the caller shows the dialog, but this
            // is driven off PropertyChanged rather than read once so it stays correct regardless of
            // when the caller flips it relative to ShowDialog.
            UpdateTitle(viewModel.IsEditMode);
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AddProgramViewModel.IsEditMode))
                {
                    UpdateTitle(viewModel.IsEditMode);
                }
            };
        }
    }

    private void UpdateTitle(bool isEditMode)
    {
        Title = LocalizationService.Instance[isEditMode ? "AddProgram_EditTitle" : "AddProgram_Title"];
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
