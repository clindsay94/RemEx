using Avalonia.Controls;
using Avalonia.Input;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class FileTransferView : UserControl
{
    public FileTransferView()
    {
        InitializeComponent();
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (DataContext is not FileTransferViewModel vm) return;

        // Navigate into directories on double-click for both panels
        if (e.Source is Control { DataContext: Remex.Core.Models.FileEntry entry })
        {
            if (vm.LocalEntries.Contains(entry))
                vm.NavigateLocalEntryCommand.Execute(entry);
            else if (vm.RemoteEntries.Contains(entry))
                vm.NavigateRemoteEntryCommand.Execute(entry);
        }
    }
}
