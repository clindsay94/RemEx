using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>What a queued transfer represents, used to pick a localized label/icon.</summary>
public enum FileTransferQueueKind
{
    Upload,
    Download,
    SendToPhone,
}

/// <summary>
/// A single entry in the local transfer queue (plan §1.4). Surfaced in the transfer-queue panel with its
/// live state and progress. The PC UI drives the actual bytes over the existing (v2-compatible)
/// upload/download path; this item is the local, per-transfer view of that work.
/// </summary>
public sealed partial class FileTransferQueueItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public FileTransferQueueKind Kind { get; }

    public string FileName { get; }

    internal Func<IProgress<double>, CancellationToken, Task> Work { get; }

    internal CancellationTokenSource Cts { get; } = new();

    /// <summary>Completes when the item reaches a terminal state — used by callers/tests to await the result.</summary>
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsTerminal))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private TransferState _state = TransferState.Queued;

    /// <summary>Percentage 0–100.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _errorMessage;

    public FileTransferQueueItem(FileTransferQueueKind kind, string fileName, Func<IProgress<double>, CancellationToken, Task> work)
    {
        Kind = kind;
        FileName = fileName;
        Work = work;
    }

    public bool IsActive => State is TransferState.Negotiating or TransferState.Active or TransferState.Verifying;

    public bool IsTerminal => State is TransferState.Done or TransferState.Failed or TransferState.Cancelled;

    public bool CanCancel => !IsTerminal;

    /// <summary>Localized one-word description of the transfer direction.</summary>
    public string ModeLabel => Kind switch
    {
        FileTransferQueueKind.Upload => LocalizationService.Instance["FileTransfer_QueueKindUpload"],
        FileTransferQueueKind.Download => LocalizationService.Instance["FileTransfer_QueueKindDownload"],
        FileTransferQueueKind.SendToPhone => LocalizationService.Instance["FileTransfer_QueueKindSend"],
        _ => string.Empty,
    };

    /// <summary>Localized current state (Queued / Transferring / Done / …).</summary>
    public string StateLabel => State switch
    {
        TransferState.Queued => LocalizationService.Instance["FileTransfer_QueueStateQueued"],
        TransferState.Negotiating => LocalizationService.Instance["FileTransfer_QueueStateNegotiating"],
        TransferState.Active => LocalizationService.Instance["FileTransfer_QueueStateActive"],
        TransferState.Paused => LocalizationService.Instance["FileTransfer_QueueStatePaused"],
        TransferState.Verifying => LocalizationService.Instance["FileTransfer_QueueStateVerifying"],
        TransferState.Done => LocalizationService.Instance["FileTransfer_QueueStateDone"],
        TransferState.Failed => LocalizationService.Instance["FileTransfer_QueueStateFailed"],
        TransferState.Cancelled => LocalizationService.Instance["FileTransfer_QueueStateCancelled"],
        _ => string.Empty,
    };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        try { Cts.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }
}

/// <summary>
/// Local, in-process transfer queue (plan §1.4): FIFO, one active transfer at a time. Persistence to
/// <c>transfer_queue.json</c> and the binary <c>/ws/files</c> channel are the host-side responsibility
/// (WP4); this queue drives the PC UI's transfers over the existing path and gives the UI a live,
/// cancellable view. UI mutations are marshalled through <see cref="_post"/> so it is safe from any thread
/// and drivable synchronously in tests.
/// </summary>
public sealed class FileTransferQueue
{
    private readonly Action<Action> _post;
    private readonly ConcurrentQueue<FileTransferQueueItem> _pending = new();
    private readonly object _pumpLock = new();
    private bool _pumping;

    /// <summary>Live, ordered view of every transfer (active, queued, and completed).</summary>
    public ObservableCollection<FileTransferQueueItem> Items { get; } = new();

    /// <summary>Raised whenever an item is added or an item's state changes (for aggregate UI recomputation).</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread once an item completes successfully (reaches <see cref="TransferState.Done"/>).
    /// Feeds the Home "Recent activity" panel; failed/cancelled items are intentionally not surfaced.</summary>
    public event Action<FileTransferQueueItem>? ItemCompleted;

    public FileTransferQueue()
        : this(null)
    {
    }

    /// <param name="post">UI-thread marshaller. Defaults to <see cref="Dispatcher.UIThread"/>; tests pass a synchronous invoker.</param>
    public FileTransferQueue(Action<Action>? post)
    {
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    /// <summary>Adds a transfer to the tail of the queue and starts the pump if idle. Returns the new item.</summary>
    public FileTransferQueueItem Enqueue(FileTransferQueueKind kind, string fileName, Func<IProgress<double>, CancellationToken, Task> work)
    {
        var item = new FileTransferQueueItem(kind, fileName, work);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTransferQueueItem.State))
                _post(() => Changed?.Invoke());
        };
        _pending.Enqueue(item);
        _post(() =>
        {
            Items.Add(item);
            Changed?.Invoke();
        });
        StartPumpIfNeeded();
        return item;
    }

    /// <summary>Removes every item that has reached a terminal state (Done/Failed/Cancelled).</summary>
    public void ClearCompleted()
    {
        _post(() =>
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].IsTerminal)
                    Items.RemoveAt(i);
            }
            Changed?.Invoke();
        });
    }

    private void StartPumpIfNeeded()
    {
        lock (_pumpLock)
        {
            if (_pumping)
                return;
            _pumping = true;
        }
        _ = Task.Run(PumpLoopAsync);
    }

    private async Task PumpLoopAsync()
    {
        while (true)
        {
            if (!_pending.TryDequeue(out var item))
            {
                lock (_pumpLock)
                {
                    // Re-check under the lock to avoid a lost wakeup: an item enqueued between the failed
                    // dequeue and here would otherwise strand the queue with _pumping cleared.
                    if (_pending.IsEmpty)
                    {
                        _pumping = false;
                        return;
                    }
                    continue;
                }
            }

            await RunItemAsync(item);
        }
    }

    private async Task RunItemAsync(FileTransferQueueItem item)
    {
        // Cancelled while still queued → never touch the wire.
        if (item.Cts.IsCancellationRequested)
        {
            SetState(item, TransferState.Cancelled);
            item.Completion.TrySetResult();
            item.Cts.Dispose();
            return;
        }

        SetState(item, TransferState.Active);
        var progress = new Progress<double>(p => _post(() => item.Progress = Math.Clamp(p * 100.0, 0.0, 100.0)));

        try
        {
            await item.Work(progress, item.Cts.Token);
            _post(() => item.Progress = 100.0);
            SetState(item, TransferState.Done);
            item.Completion.TrySetResult();
            _post(() => ItemCompleted?.Invoke(item));
        }
        catch (OperationCanceledException)
        {
            SetState(item, TransferState.Cancelled);
            item.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            _post(() => item.ErrorMessage = ex.Message);
            SetState(item, TransferState.Failed);
            item.Completion.TrySetResult();
        }
        finally
        {
            item.Cts.Dispose();
        }
    }

    private void SetState(FileTransferQueueItem item, TransferState state)
        => _post(() => item.State = state);
}
