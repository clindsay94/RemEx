using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class AppLauncherView : UserControl
{
    public AppLauncherView()
    {
        InitializeComponent();
        DataContext = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AppLauncherViewModel>(App.Services);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is AppLauncherViewModel viewModel)
        {
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
                        catch (System.Exception)
                        {
                            // TODO: Log error
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
}
