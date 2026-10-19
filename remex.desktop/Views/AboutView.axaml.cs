using Avalonia.Controls;
using Avalonia.Input.Platform;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
        ConfigureViewModel();
    }

    private void ConfigureViewModel()
    {
        if (DataContext is not AboutViewModel vm)
            return;

        vm.CopyToClipboardAsync = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        };
    }
}
