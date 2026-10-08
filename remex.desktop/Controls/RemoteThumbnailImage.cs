using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Controls;

/// <summary>
/// A row's thumbnail in the File Transfer list or grid (2026-10-08 redesign). It asks for its picture only once it is
/// on screen, and gives up the request when the row scrolls away or is reused for another file, so a folder of a
/// thousand photos fetches what is visible and no more. Shows nothing for a file without one, leaving the icon
/// beneath it in view.
/// </summary>
public sealed class RemoteThumbnailImage : Image
{
    public static readonly StyledProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.Register<RemoteThumbnailImage, FileEntry?>(nameof(Entry));

    private CancellationTokenSource? _request;

    public FileEntry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Image);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EntryProperty && this.IsAttachedToVisualTree())
            RequestAsync().FireAndForget("file list thumbnail");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestAsync().FireAndForget("file list thumbnail");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void Cancel()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }

    private async Task RequestAsync()
    {
        Cancel();
        Source = null;
        IsVisible = false;
        if (Entry is not { IsDirectory: false } entry)
            return;

        var screen = this.GetVisualAncestors().OfType<Control>()
            .Select(c => c.DataContext).OfType<FileTransferViewModel>().FirstOrDefault();
        if (screen is null)
            return;

        _request = new CancellationTokenSource();
        var ct = _request.Token;
        try
        {
            var image = await screen.GetThumbnailAsync(entry, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(entry, Entry))
                return;
            Source = image;
            IsVisible = image is not null;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or reused; the next request owns this control.
        }
    }
}
