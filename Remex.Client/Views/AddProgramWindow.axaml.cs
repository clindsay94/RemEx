using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

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
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
