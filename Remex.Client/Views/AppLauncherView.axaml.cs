using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Remex.Client.ViewModels;
using Remex.Core.Models;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace Remex.Client.Views;

public partial class AppLauncherView : UserControl
{
    private static readonly DataFormat<byte[]> AppEntryIdFormat = DataFormat.CreateBytesApplicationFormat("application.x-remex-appentry-id");

    public AppLauncherView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is AppEntry entry)
        {
            var dragData = new DataTransfer();
            dragData.Add(DataTransferItem.Create(AppEntryIdFormat, entry.Id.ToByteArray()));
            await DragDrop.DoDragDropAsync(e, dragData, DragDropEffects.Move);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(AppEntryIdFormat))
        {
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var item = e.DataTransfer.Items.FirstOrDefault(i => i.Formats.Contains(AppEntryIdFormat));
        if (item != null && item.TryGetRaw(AppEntryIdFormat) is byte[] bytes)
        {
            var sourceId = new Guid(bytes);
            
            if (sender is Border targetBorder &&
                targetBorder.DataContext is AppEntry targetEntry &&
                DataContext is AppLauncherViewModel viewModel)
            {
                var sourceEntry = viewModel.Launchers.FirstOrDefault(x => x.Id == sourceId);
                if (sourceEntry != null)
                {
                    viewModel.ReorderLauncherCommand.Execute((sourceEntry, targetEntry));
                }
            }
        }
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
                        catch (System.Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Remex] Failed to save launchers: {ex.Message}");
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
